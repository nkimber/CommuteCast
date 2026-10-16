using CommuteCast.Core;

namespace CommuteCast.Infrastructure;

public sealed partial class AuditionGenerator
{
    public const int MaximumComparisonCharacters = 3600;

    /// <summary>Private local preview using production segmentation, pause joins and final loudness.
    /// The passage is already prepared; every candidate speaks the same text exactly once.</summary>
    public async Task<AuditionAudio> GenerateComparisonAsync(string spokenText, NarrationSettings settings, string ffmpeg,
        CancellationToken ct, Action<string>? progress = null)
    {
        if (settings.Engine is not ("kokoro" or "piper")) throw new ArgumentException("Voice comparison uses installed local Kokoro or Piper voices.");
        if (string.IsNullOrWhiteSpace(spokenText) || spokenText.Length > MaximumComparisonCharacters)
            throw new ArgumentException($"Choose a spoken passage of 1–{MaximumComparisonCharacters:N0} characters. Nothing is truncated.");
        for (var i = 0; i < spokenText.Length; i++)
        {
            if (char.IsHighSurrogate(spokenText[i]))
            { if (++i == spokenText.Length || !char.IsLowSurrogate(spokenText[i])) throw new ArgumentException("Choose complete Unicode characters for comparison."); }
            else if (char.IsLowSurrogate(spokenText[i])) throw new ArgumentException("Choose complete Unicode characters for comparison.");
        }
        var natural = settings.LocalVoice?.NaturalPhrasing == true;
        var chunks = natural ? NaturalChunker.Split(spokenText) : Chunker.Split(spokenText, 450);
        // Validate complete Unicode and settings before acquiring speech or creating a file.
        foreach (var chunk in chunks) AuditionRequest.ApprovedExcerpt(chunk.Text, settings);
        await inferenceGate.WaitAsync(ct).ConfigureAwait(false);
        var owned = new List<AuditionAudio>();
        AuditionAudio? result = null;
        try
        {
            var pcm = new List<AuditionAudio>();
            NarrationSettings? captured = null;
            var pipeline = new AudioPipeline(new() { Ffmpeg = ffmpeg });
            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested(); progress?.Invoke($"Rendering segment {chunk.Index + 1} of {chunks.Count}");
                var raw = await GenerateCoreAsync(AuditionRequest.ApprovedExcerpt(chunk.Text, settings), ct, true).ConfigureAwait(false);
                owned.Add(raw);
                if (captured is not null && (captured.ProviderFingerprint != raw.Settings.ProviderFingerprint || captured.ProviderImageId != raw.Settings.ProviderImageId))
                    throw new IOException("The local model changed during comparison. Start this sample again with one installed model.");
                captured = raw.Settings;
                var input = await VerifiedPreviewPathAsync(raw, ct).ConfigureAwait(false);
                var normalized = await WriteComparisonPreviewAsync(captured, spokenText, async output =>
                {
                    var conversion = await ProcessRunner.RunToFileAsync(ffmpeg,
                        ["-v", "error", "-nostdin", "-n", "-protocol_whitelist", "file,pipe,fd", "-i", input,
                         "-map", "0:a:0", "-ar", "24000", "-ac", "1", "-c:a", "pcm_s16le", "-f", "wav", "-fd", "1", "fd:"],
                        (FileStream)output, 32L * 1024 * 1024, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
                    if (conversion.ExitCode != 0) throw new IOException("Comparison audio conversion failed.");
                }, ct).ConfigureAwait(false);
                owned.Add(normalized); pcm.Add(normalized);
                await pipeline.ValidateChunkAsync(await VerifiedPreviewPathAsync(normalized, ct).ConfigureAwait(false), chunk.Text, ct).ConfigureAwait(false);
            }
            progress?.Invoke("Joining segments and matching listening volume");
            var paths = new List<string>();
            foreach (var clip in pcm) paths.Add(await VerifiedPreviewPathAsync(clip, ct).ConfigureAwait(false));
            var gaps = chunks.Take(chunks.Count - 1).Select((chunk, i) => natural
                ? NaturalAudio.AddedGapSamples(paths[i], paths[i + 1], NaturalChunker.PauseMilliseconds(chunk, settings.LocalVoice!))
                : chunk.Text.TrimEnd().EndsWith('.') || chunk.Text.EndsWith('\n') ? 3600 : 0).ToArray();
            var samples = paths.Sum(path => WaveAudio.DataRegion(path).Length / 2) + gaps.Sum(g => (long)g);
            var joined = await WriteComparisonPreviewAsync(captured!, spokenText, async output =>
            {
                WaveAudio.WriteHeader(output, samples);
                for (var i = 0; i < pcm.Count; i++)
                {
                    var path = await VerifiedPreviewPathAsync(pcm[i], ct).ConfigureAwait(false);
                    await using var input = File.OpenRead(path);
                    var (offset, length) = WaveAudio.DataRegion(path); input.Position = offset;
                    var buffer = new byte[81920];
                    while (length > 0)
                    {
                        var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(length, buffer.Length)), ct).ConfigureAwait(false);
                        if (read == 0) throw new IOException("A comparison segment changed during assembly.");
                        await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false); length -= read;
                    }
                    if (i < gaps.Length) await output.WriteAsync(new byte[gaps[i] * 2], ct).ConfigureAwait(false);
                }
            }, ct).ConfigureAwait(false);
            owned.Add(joined);
            if (!natural) result = joined;
            else
            {
                var input = await VerifiedPreviewPathAsync(joined, ct).ConfigureAwait(false);
                var filter = await NaturalAudio.LoudnessFilterAsync(ffmpeg, input, ct).ConfigureAwait(false);
                result = await WriteComparisonPreviewAsync(captured!, spokenText, async output =>
                {
                    var conversion = await ProcessRunner.RunToFileAsync(ffmpeg,
                        ["-v", "error", "-nostdin", "-n", "-protocol_whitelist", "file,pipe,fd", "-i", input,
                         "-af", filter, "-ar", "24000", "-ac", "1", "-c:a", "pcm_s16le", "-f", "wav", "-fd", "1", "fd:"],
                        (FileStream)output, 32L * 1024 * 1024, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
                    if (conversion.ExitCode != 0) throw new IOException("Comparison volume matching failed.");
                }, ct).ConfigureAwait(false);
                owned.Add(result);
            }
            WaveAudio.Inspect(await VerifiedPreviewPathAsync(result, ct).ConfigureAwait(false));
            ct.ThrowIfCancellationRequested();
            // Remove intermediates before handing ownership of the completed clip to the caller.
            foreach (var clip in owned.Where(clip => clip != result).ToArray())
            { await RemoveAsync(clip).ConfigureAwait(false); owned.Remove(clip); }
            owned.Clear();
            return result;
        }
        finally
        {
            try { foreach (var clip in owned) await RemoveAsync(clip).ConfigureAwait(false); }
            finally { inferenceGate.Release(); }
        }
    }

    private async Task<string> VerifiedPreviewPathAsync(AuditionAudio audio, CancellationToken ct)
    {
        var path = OwnedFileRemoval.Resolve(workspace.Root, audio.RelativePath);
        Workspace.RejectReparsePoints(Path.GetDirectoryName(path)!); Workspace.RejectFileReparsePoint(path);
        if (await Workspace.HashFileAsync(path, ct).ConfigureAwait(false) != audio.Hash) throw new IOException("A comparison clip changed. Its files were preserved.");
        return path;
    }

    private async Task<AuditionAudio> WriteComparisonPreviewAsync(NarrationSettings settings, string spokenText,
        Func<Stream, Task> write, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N"); var name = "audition-" + id + ".wav";
        var journal = new AuditionWriteJournal(id, DateTimeOffset.UtcNow, []);
        var directory = Path.Combine(workspace.Root, "auditions");
        Workspace.RejectReparsePoints(directory); Directory.CreateDirectory(directory);
        await ownership.SaveAsync(journal, ct).ConfigureAwait(false);
        try
        {
            await using (var held = ExportStagingFile.Create(directory, name))
            {
                journal.Artifacts.Add(new(name, "", CreationIdentity: held.Identity)); await ownership.SaveAsync(journal, ct).ConfigureAwait(false);
                await write(held.Stream).ConfigureAwait(false); held.Stream.Flush(true);
                WaveAudio.DataRegion(held.Stream);
                var hash = await held.HashAsync(ct).ConfigureAwait(false);
                journal.Artifacts.Clear(); journal.Artifacts.Add(new(name, hash, PromotionIdentity: held.Identity));
                await ownership.SaveAsync(journal, ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            return new("auditions/" + name, journal.Artifacts.Single().Hash, settings,
                new(spokenText, [], "comparison-spoken-v1"), id);
        }
        catch
        {
            var saved = (await ownership.LoadAsync().ConfigureAwait(false)).SingleOrDefault(record => record.Id == id);
            if (saved is not null) await RemoveJournalAsync(saved, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
