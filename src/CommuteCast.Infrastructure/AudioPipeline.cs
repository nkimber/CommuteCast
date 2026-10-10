using CommuteCast.Core;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CommuteCast.Infrastructure;

public sealed class AudioPipeline(AppSettings settings) : IAudioPipeline
{
    public const string ContractVersion = "pcm24k-s16le-mono-mp3128-gap150-v1";
    private static void ValidateManifest(Job job)
    {
        if (job.PrivateArtifacts.Any(r => r.PromotionIdentity is not null || r.CreationIdentity is not null)) throw new IOException("An interrupted private audio write or rename must be reconciled before assembly or validation.");
        if (job.Episode is not null) PodcastScript.ValidateManifest(job);
        else if (job.AudioContractVersion != ContractVersion || job.ChunkingVersion != "chunk450-v1") throw new IOException("Unsupported audio/chunk contract. Publication is blocked.");
        Chunker.ValidateManifest(job.Chunks, job.Prepared.Script, job.Episode is null ? 900 : 1800);
        if (job.Receipts.Count != job.Chunks.Count || !job.Receipts.OrderBy(r => r.Index).Select(r => r.Index).SequenceEqual(Enumerable.Range(0, job.Chunks.Count)) ||
            job.Receipts.Any(r => r.Fingerprint != job.Fingerprint || !double.IsFinite(r.Duration) || r.Duration <= 0)) throw new IOException("Validated receipt sequence or contract is incompatible. Publication is blocked.");
    }
    public Task NormalizeAsync(string input, string output, CancellationToken ct) =>
        NormalizeAsync(new Job(), input, output, () => Task.CompletedTask, ct);

    public async Task NormalizeAsync(Job job, string input, string output, Func<Task> checkpoint, CancellationToken ct)
    {
        WaveAudio.DataRegion(input, false);
        await PrivateJobFiles.WriteRecordedAsync(job, Path.GetDirectoryName(Path.GetFullPath(output))!, Path.GetFileName(output), async stream =>
        {
            var arguments = new List<string> { "-v", "error", "-nostdin", "-n", "-protocol_whitelist", "file,pipe,fd", "-i", input, "-map", "0:a:0" };
            if (job.Episode is not null) arguments.AddRange(["-af", "loudnorm=I=-19:TP=-2:LRA=7"]);
            arguments.AddRange(["-ar", "24000", "-ac", "1", "-c:a", "pcm_s16le", "-f", "wav", "-fd", "1", "fd:"]);
            var result = await ProcessRunner.RunToFileAsync(settings.Ffmpeg, arguments, (FileStream)stream, 192L * 1024 * 1024, TimeSpan.FromMinutes(2), ct);
            if (result.ExitCode != 0) throw new IOException("The speech audio could not be decoded to PCM. This chunk will be regenerated on retry.");
            WaveAudio.DataRegion(stream);
        }, checkpoint, ct);
    }
    public Task<AudioInfo> ValidateChunkAsync(string path, string text, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var info = WaveAudio.Inspect(path);
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var words = tokens.Length;
        var characters = tokens.Sum(token => token.Length);
        var longestToken = tokens.Select(token => token.Length).DefaultIfEmpty().Max();
        var minimumDuration = words > 12 ? Math.Max(0.08, words * 0.07) : 0.08;
        // Keep the prose allowance; unusually long tokens can be spelled or expanded
        // by the provider (URLs, identifiers, numbers, or arbitrary unbroken strings).
        // Budget half a second for each character beyond twelve in any token. This
        // heuristic extends only the upper bound; it cannot prove spoken fidelity.
        var extraCharacters = tokens.Sum(token => Math.Max(0, token.Length - 12));
        var maximumDuration = Math.Min(300, Math.Max(30, words * 2.5 + extraCharacters * 0.5));
        if (info.Duration < minimumDuration || info.Duration > maximumDuration)
            throw new IOException(FormattableString.Invariant($"Chunk duration is implausible for the prepared text: measured {info.Duration:F3} seconds for {words} whitespace-delimited words, {characters} non-whitespace characters (longest token {longestToken}); expected {minimumDuration:F3}–{maximumDuration:F3} seconds. Review the voice or text and retry."));
        if (info.Rms < 0.0001 || info.Peak > 1) throw new IOException("Chunk contains silence or invalid sample values. Export is blocked.");
        return info;
    }, ct);

    public Task AssembleAsync(Job job, string directory, CancellationToken ct) => AssembleAsync(job, directory, ct, null);
    public async Task AssembleAsync(Job job, string directory, CancellationToken ct, Func<Task>? checkpoint)
    {
        ValidateManifest(job);
        var assembled = Path.Combine(directory, "assembled.wav");
        var samples = job.Receipts.Sum(r => (long)Math.Round(r.Duration * 24000));
        // Add 150ms between chunks ending a sentence/paragraph. Hard splits have no inserted gap.
        var gaps = job.Chunks.Take(job.Chunks.Count - 1).Select(c => GapSamples(job, c)).ToArray();
        var total = samples + gaps.Sum(g => (long)g);
        if (total * 2 > uint.MaxValue - 36) throw new IOException("This narration exceeds the supported WAV size. Split it into separate submissions.");
        var comment = Mp3Comments.CreateFrame(Mp3Comments.Describe(job));
        await PrivateJobFiles.WriteRecordedAsync(job, directory, "assembled.wav", async output =>
        {
            WaveAudio.WriteHeader(output, total);
            foreach (var chunk in job.Chunks)
            {
                ct.ThrowIfCancellationRequested();
                var path = Path.Combine(directory, $"chunk-{chunk.Index:D5}.wav");
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                var receipt = job.Receipts.Single(r => r.Index == chunk.Index);
                if (Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(input, ct)) != receipt.Hash) throw new IOException("A validated chunk changed before assembly. Publication is blocked.");
                var (offset, length) = WaveAudio.DataRegion(path);
                if (length / 2 != (long)Math.Round(receipt.Duration * 24000)) throw new IOException("Chunk sample count differs from its validated receipt. Publication is blocked.");
                input.Position = offset;
                var buffer = new byte[81920];
                while (length > 0)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), ct);
                    if (read == 0) throw new IOException("A validated chunk changed during assembly.");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    length -= read;
                }
                if (chunk.Index < gaps.Length) await output.WriteAsync(new byte[gaps[chunk.Index] * 2], ct);
            }
        }, checkpoint ?? (() => Task.CompletedTask), ct);
        await PrivateJobFiles.WriteRecordedAsync(job, directory, "encoded.partial.mp3", async output =>
        {
            var encode = await ProcessRunner.RunToFileAsync(settings.Ffmpeg, ["-v", "error", "-nostdin", "-n", "-protocol_whitelist", "file,pipe,fd", "-f", "wav", "-i", assembled, "-c:a", "libmp3lame", "-b:a", "128k", "-id3v2_version", "3", "-metadata_header_padding", (comment.Length + 10).ToString(CultureInfo.InvariantCulture), "-metadata", "title=" + job.Title, "-metadata", "artist=CommuteCast", "-metadata", "date=" + job.CreatedUtc.ToString("O"), "-metadata", "commutecast_created_utc=" + job.CreatedUtc.ToString("O"), "-metadata", "commutecast_job_id=" + job.Id, "-f", "mp3", "-fd", "1", "fd:"], (FileStream)output, checked(total * 2 + 1024 * 1024), TimeSpan.FromMinutes(15), ct);
            if (encode.ExitCode != 0) throw new IOException("MP3 encoding failed. Validated chunks are retained. Check FFmpeg and free disk space.");
            ct.ThrowIfCancellationRequested();
            Mp3Comments.WriteFrame(output, comment);
        }, checkpoint ?? (() => Task.CompletedTask), ct);
        ct.ThrowIfCancellationRequested();
        await PrivateJobFiles.MoveRecordedAsync(job, directory, "encoded.partial.mp3", "complete.mp3", checkpoint ?? (() => Task.CompletedTask), ct);
    }

    public async Task<AudioInfo> ValidateFinalAsync(Job job, string path, CancellationToken ct)
    {
        ValidateManifest(job);
        var probe = await ProcessRunner.RunAsync(settings.Ffprobe, ["-v", "error", "-protocol_whitelist", "file,pipe", "-f", "mp3", "-select_streams", "a:0", "-show_entries", "stream=codec_name,sample_rate,channels,duration", "-of", "json", path], TimeSpan.FromSeconds(30), ct);
        if (probe.ExitCode != 0) throw new IOException("The finished MP3 could not be probed. Export is blocked.");
        using var json = JsonDocument.Parse(probe.Output);
        var stream = json.RootElement.GetProperty("streams")[0];
        var duration = double.Parse(stream.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        if (stream.GetProperty("codec_name").GetString() != "mp3" || stream.GetProperty("sample_rate").GetString() != "24000" || stream.GetProperty("channels").GetInt32() != 1) throw new IOException("Finished MP3 has an unexpected format.");
        var gaps = job.Chunks.Take(job.Chunks.Count - 1).Sum(c => GapSamples(job, c)) / 24000.0;
        var expected = job.Receipts.Sum(r => r.Duration) + gaps;
        if (Math.Abs(expected - duration) > .3) throw new IOException("Finished MP3 duration does not match the complete ordered chunk sequence.");
        var decode = await ProcessRunner.RunAsync(settings.Ffmpeg, ["-v", "error", "-xerror", "-nostdin", "-protocol_whitelist", "file,pipe", "-f", "mp3", "-i", path, "-f", "null", "-"], TimeSpan.FromMinutes(10), ct);
        if (decode.ExitCode != 0) throw new IOException("Finished MP3 failed full decoding. Export is blocked.");
        return new(duration, 24000, 1, (long)(duration * 24000), 0, 0);
    }
    private static int GapSamples(Job job, TextChunk unit) => job.Episode is null
        ? unit.Text.TrimEnd().EndsWith('.') || unit.Text.EndsWith('\n') ? 3600 : 0
        : unit.Index + 1 < job.Chunks.Count && unit.Turns![^1].Speaker != job.Chunks[unit.Index + 1].Turns![0].Speaker ? 1920 : 0;
}

public static class WaveAudio
{
    public static (long Offset, long Length) DataRegion(string path, bool canonical = true)
    {
        using var file = File.OpenRead(path);
        return DataRegion(file, canonical);
    }
    public static (long Offset, long Length) DataRegion(Stream file, bool canonical = true)
    {
        file.Position = 0;
        using var reader = new BinaryReader(file, Encoding.ASCII, leaveOpen: true);
        if (file.Length < 44) throw new IOException("Truncated WAV header.");
        if (new string(reader.ReadChars(4)) != "RIFF") throw new IOException("Expected RIFF audio.");
        if (reader.ReadUInt32() + 8L != file.Length) throw new IOException("WAV declared size does not match the complete file.");
        if (new string(reader.ReadChars(4)) != "WAVE") throw new IOException("Expected WAV audio.");
        var validFormat = false;
        ushort blockAlignment = 0;
        (long Offset, long Length)? region = null;
        while (file.Position + 8 <= file.Length)
        {
            var type = new string(reader.ReadChars(4));
            var length = reader.ReadUInt32();
            var offset = file.Position;
            if (offset + length + length % 2 > file.Length) throw new IOException("Truncated WAV audio or padding.");
            if (type == "fmt ")
            {
                if (validFormat || length < 16 || reader.ReadUInt16() != 1) throw new IOException("Expected one lossless PCM format; compressed or duplicate formats are rejected.");
                var channels = reader.ReadUInt16(); var rate = reader.ReadInt32(); var byteRate = reader.ReadInt32(); var alignment = reader.ReadUInt16(); var bits = reader.ReadUInt16();
                if (channels is < 1 or > 2 || rate is < 8000 or > 96000 || alignment != channels * 2 || bits != 16 || byteRate != rate * alignment) throw new IOException("Unexpected native PCM sample format.");
                if (canonical && (channels != 1 || rate != 24000)) throw new IOException("Expected mono 24kHz normalized PCM audio.");
                validFormat = true;
                blockAlignment = alignment;
            }
            if (type == "data")
            {
                if (!validFormat || region is not null || length == 0 || length % blockAlignment != 0) throw new IOException("Invalid or duplicate PCM data region.");
                region = (offset, length);
            }
            file.Position = offset + length + length % 2;
        }
        if (file.Position != file.Length || region is null) throw new IOException("WAV data is missing or trailing structure is incomplete.");
        return region.Value;
    }
    public static AudioInfo Inspect(string path)
    {
        var (offset, length) = DataRegion(path);
        using var file = File.OpenRead(path);
        file.Position = offset;
        using var reader = new BinaryReader(file);
        double squares = 0, peak = 0;
        for (long i = 0; i < length / 2; i++)
        {
            var sample = reader.ReadInt16() / 32768.0;
            squares += sample * sample;
            peak = Math.Max(peak, Math.Abs(sample));
        }
        return new(length / 2.0 / 24000, 24000, 1, length / 2, peak, Math.Sqrt(squares / (length / 2)));
    }
    public static void WriteHeader(Stream output, long samples)
    {
        using var writer = new BinaryWriter(output, Encoding.ASCII, true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(checked((uint)(36 + samples * 2)));
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
        writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(24000); writer.Write(48000);
        writer.Write((ushort)2); writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(checked((uint)(samples * 2)));
    }
}
