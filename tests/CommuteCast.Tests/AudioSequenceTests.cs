using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Text.Json;

namespace CommuteCast.Tests;

public class AudioSequenceTests
{
    [Fact] public async Task NormalizationPersistsExclusiveCreationThenCanonicalHashBeforeReturning()
    {
        using var test = new TestWorkspace(); var job = new Job(); var store = new SqliteJobStore(test.Workspace);
        var directory = test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "input.wav"); TestWorkspace.WriteWave(input);
        var output = Path.Combine(directory, "normalized.partial.wav"); var saves = 0;
        await new AudioPipeline(new()).NormalizeAsync(job, input, output, async () =>
        {
            await store.SaveAsync(job); var receipt = Assert.Single(Assert.Single(await store.LoadAsync()).PrivateArtifacts);
            Assert.Throws<IOException>(() => File.WriteAllText(output, "unknown replacement"));
            if (++saves == 1) { Assert.NotNull(receipt.CreationIdentity); Assert.Equal("", receipt.Hash); }
            else { Assert.Null(receipt.CreationIdentity); Assert.Equal(64, receipt.Hash.Length); }
        }, default);
        Assert.Equal(2, saves); Assert.Equal(24000, WaveAudio.Inspect(output).Samples);
        Assert.Equal(await Workspace.HashFileAsync(output), PrivateJobFiles.Inventory(Assert.Single(await store.LoadAsync()))["normalized.partial.wav"]);
        var original = await File.ReadAllBytesAsync(input);
        await Assert.ThrowsAsync<IOException>(() => new AudioPipeline(new()).NormalizeAsync(input, output, default));
        Assert.Equal(original, await File.ReadAllBytesAsync(input)); Assert.Equal(24000, WaveAudio.Inspect(output).Samples);
    }

    [Fact] public async Task EncoderPersistsCreationUnderExclusiveHandleAndRetainsDurableFinalReceipt()
    {
        using var test = new TestWorkspace(); var job = await ToneJobAsync(test); var store = new SqliteJobStore(test.Workspace);
        var directory = test.Workspace.JobDirectory(job.Id); var observed = false;
        await new AudioPipeline(new()).AssembleAsync(job, directory, default, async () =>
        {
            await store.SaveAsync(job);
            if (!job.PrivateArtifacts.Any(r => r.RelativePath == "encoded.partial.mp3" && r.CreationIdentity is not null)) return;
            var saved = Assert.Single(await store.LoadAsync()); Assert.NotNull(saved.PrivateArtifacts.Single(r => r.RelativePath == "encoded.partial.mp3").CreationIdentity);
            Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(directory, "encoded.partial.mp3"), "replacement")); observed = true;
        });
        Assert.True(observed); var reopened = Assert.Single(await store.LoadAsync());
        Assert.DoesNotContain(reopened.PrivateArtifacts, r => r.CreationIdentity is not null || r.PromotionIdentity is not null);
        Assert.Equal(await Workspace.HashFileAsync(test.Workspace.FinalPath(job)), PrivateJobFiles.Inventory(reopened)["complete.mp3"]);
    }

    [Fact] public async Task FailedNormalizerDeletesItsHeldPartialAndAllowsDurableRetry()
    {
        using var test = new TestWorkspace(); var job = new Job(); var store = new SqliteJobStore(test.Workspace);
        var directory = test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "input.wav"); TestWorkspace.WriteWave(input);
        var output = Path.Combine(directory, "normalized.partial.wav"); var unknown = Path.Combine(directory, "unknown.txt"); await File.WriteAllTextAsync(unknown, "preserve");
        await Assert.ThrowsAsync<IOException>(() => new AudioPipeline(new() { Ffmpeg = "missing-commutecast-tool.exe" }).NormalizeAsync(job, input, output, () => store.SaveAsync(job), default));
        Assert.False(File.Exists(output)); var retry = Assert.Single(await store.LoadAsync());
        Assert.NotNull(Assert.Single(retry.PrivateArtifacts).CreationIdentity);
        await new AudioPipeline(new()).NormalizeAsync(retry, input, output, () => store.SaveAsync(retry), default);
        Assert.Equal(24000, WaveAudio.Inspect(output).Samples); Assert.Equal("preserve", await File.ReadAllTextAsync(unknown));
    }

    [Fact] public async Task OrdinalTonesSurviveOneMp3EncodeInOrderWithMetadataAndFrameCount()
    {
        using var test = new TestWorkspace(); var job = await ToneJobAsync(test); var audio = new AudioPipeline(new());
        await audio.AssembleAsync(job, test.Workspace.JobDirectory(job.Id), default);
        var info = await audio.ValidateFinalAsync(job, test.Workspace.FinalPath(job), default);
        Assert.InRange(info.Duration, 3.3, 3.5);
        var decoded = Path.Combine(test.Workspace.Root, "decoded.wav");
        var result = await ProcessRunner.RunAsync("ffmpeg", ["-v", "error", "-y", "-i", test.Workspace.FinalPath(job), "-ar", "24000", "-ac", "1", "-c:a", "pcm_s16le", decoded], TimeSpan.FromSeconds(10)); Assert.Equal(0, result.ExitCode);
        var decodedInfo = WaveAudio.Inspect(decoded); Assert.Equal(79200, decodedInfo.Samples);
        for (var ordinal = 0; ordinal < 3; ordinal++)
        {
            var powers = new[] { 220, 440, 660 }.Select(f => TonePower(decoded, ordinal * 1.15 + .25, f)).ToArray();
            Assert.True(powers[ordinal] > powers.Where((_, i) => i != ordinal).Max() * 100, "Each segment must contain its expected ordinal tone.");
        }
        var metadata = await ProcessRunner.RunAsync("ffprobe", ["-v", "error", "-show_entries", "format_tags=title,artist,date,comment,commutecast_created_utc,commutecast_job_id", "-of", "json", test.Workspace.FinalPath(job)], TimeSpan.FromSeconds(10)); Assert.Equal(0, metadata.ExitCode);
        using var document = JsonDocument.Parse(metadata.Output); var tags = document.RootElement.GetProperty("format").GetProperty("tags");
        Assert.Equal(job.Title, tags.GetProperty("title").GetString()); Assert.Equal("CommuteCast", tags.GetProperty("artist").GetString());
        Assert.Equal(job.CreatedUtc.ToString("yyyy"), tags.GetProperty("date").GetString());
        Assert.Equal(job.CreatedUtc.ToString("O"), tags.GetProperty("commutecast_created_utc").GetString()); Assert.Equal(job.Id, tags.GetProperty("commutecast_job_id").GetString());
        Assert.Equal("CommuteCast job " + job.Id, tags.GetProperty("comment").GetString());
    }
    [Theory] [InlineData("missing")] [InlineData("duplicate")] [InlineData("reordered")] [InlineData("ordinal")] [InlineData("offset")] [InlineData("receipt")] [InlineData("sample-count")] [InlineData("corruption")]
    public async Task InvalidSequenceCannotProduceACompletedMp3(string defect)
    {
        using var test = new TestWorkspace(); var job = await ToneJobAsync(test);
        switch (defect)
        {
            case "missing": job.Chunks.RemoveAt(1); break;
            case "duplicate": job.Chunks.Insert(1, job.Chunks[0]); break;
            case "reordered": (job.Chunks[0], job.Chunks[1]) = (job.Chunks[1], job.Chunks[0]); break;
            case "ordinal": job.Chunks[0] = job.Chunks[0] with { Index = 1 }; job.Chunks[1] = job.Chunks[1] with { Index = 0 }; break;
            case "offset": job.Chunks[1] = job.Chunks[1] with { Start = 0 }; break;
            case "receipt": job.Receipts[1] = job.Receipts[1] with { Index = 0 }; break;
            case "sample-count": job.Receipts[1] = job.Receipts[1] with { Duration = 1.01 }; break;
            case "corruption": TestWorkspace.WriteWave(test.Workspace.ChunkPath(job, 1), 1); break;
        }
        await Assert.ThrowsAsync<IOException>(() => new AudioPipeline(new()).AssembleAsync(job, test.Workspace.JobDirectory(job.Id), default));
        Assert.False(File.Exists(test.Workspace.FinalPath(job))); Assert.Empty(Directory.GetFiles(test.Destination, "*.mp3"));
    }
    [Theory] [InlineData("declared-size")] [InlineData("empty-data")] [InlineData("incomplete-frame")] [InlineData("duplicate-data")] [InlineData("duplicate-format")] [InlineData("trailing-header")] [InlineData("byte-rate")]
    public void MalformedWavStructureIsRejectedBeforeNormalization(string defect)
    {
        using var test = new TestWorkspace(); var path = Path.Combine(test.Workspace.Root, "malformed.wav"); TestWorkspace.WriteWave(path);
        var bytes = File.ReadAllBytes(path);
        switch (defect)
        {
            case "declared-size": BitConverter.GetBytes(1u).CopyTo(bytes, 4); break;
            case "empty-data": bytes = bytes[..44]; BitConverter.GetBytes(36u).CopyTo(bytes, 4); BitConverter.GetBytes(0u).CopyTo(bytes, 40); break;
            case "incomplete-frame": BitConverter.GetBytes((ushort)2).CopyTo(bytes, 22); BitConverter.GetBytes(96000).CopyTo(bytes, 28); BitConverter.GetBytes((ushort)4).CopyTo(bytes, 32); bytes = bytes[..^2]; BitConverter.GetBytes((uint)(bytes.Length - 8)).CopyTo(bytes, 4); BitConverter.GetBytes((uint)(bytes.Length - 44)).CopyTo(bytes, 40); break;
            case "duplicate-data": bytes = bytes.Concat(bytes[36..]).ToArray(); BitConverter.GetBytes((uint)(bytes.Length - 8)).CopyTo(bytes, 4); break;
            case "duplicate-format": bytes = bytes.Concat(bytes[12..36]).ToArray(); BitConverter.GetBytes((uint)(bytes.Length - 8)).CopyTo(bytes, 4); break;
            case "trailing-header": bytes = bytes.Concat(new byte[] { 1, 2 }).ToArray(); BitConverter.GetBytes((uint)(bytes.Length - 8)).CopyTo(bytes, 4); break;
            case "byte-rate": BitConverter.GetBytes(7).CopyTo(bytes, 28); break;
        }
        File.WriteAllBytes(path, bytes); Assert.Throws<IOException>(() => WaveAudio.DataRegion(path, false));
    }
    [Fact] public async Task FailedAssemblyRemovesItsOwnPartialAndCanRetryFromRetainedChunks()
    {
        using var test = new TestWorkspace(); var job = await ToneJobAsync(test);
        var directory = test.Workspace.JobDirectory(job.Id);
        var chunk = test.Workspace.ChunkPath(job, 1); var original = await File.ReadAllBytesAsync(chunk);
        var unknown = Path.Combine(directory, "notes.txt"); await File.WriteAllTextAsync(unknown, "Keep this");
        TestWorkspace.WriteWave(chunk, 1);
        await Assert.ThrowsAsync<IOException>(() => new AudioPipeline(new()).AssembleAsync(job, directory, default));
        Assert.False(File.Exists(Path.Combine(directory, "assembled.wav")));
        Assert.DoesNotContain(job.PrivateArtifacts, r => r.RelativePath == "assembled.wav");
        Assert.Equal("Keep this", await File.ReadAllTextAsync(unknown));
        await File.WriteAllBytesAsync(chunk, original);
        var store = new SqliteJobStore(test.Workspace);
        await new AudioPipeline(new()).AssembleAsync(job, directory, default, () => store.SaveAsync(job));
        var reopened = Assert.Single(await store.LoadAsync());
        Assert.Equal(await Workspace.HashFileAsync(Path.Combine(directory, "assembled.wav")),
            PrivateJobFiles.Inventory(reopened)["assembled.wav"]);
        Assert.InRange((await new AudioPipeline(new()).ValidateFinalAsync(reopened, test.Workspace.FinalPath(job), default)).Duration, 3.3, 3.5);
    }

    [Fact] public void ChunkAndAudioVersionsInvalidateCacheFingerprint()
    {
        var job = new Job { Prepared = TextPreparation.Prepare("Faithful source.") }; var original = job.Fingerprint;
        job.AudioContractVersion = "changed-audio"; Assert.NotEqual(original, job.Fingerprint); job.AudioContractVersion = AudioPipeline.ContractVersion;
        job.ChunkingVersion = "changed-chunking"; Assert.NotEqual(original, job.Fingerprint);
    }
    private static async Task<Job> ToneJobAsync(TestWorkspace test)
    {
        var texts = new[] { "First marker.\n\n", "Second marker.\n\n", "Third marker." };
        var job = new Job { Title = "Ordinal tones — API & Unicode 😀", CreatedUtc = new DateTimeOffset(2026, 10, 5, 20, 10, 0, TimeSpan.Zero), Prepared = new(string.Concat(texts), []), Settings = new("kokoro", "af_heart", 1, false, "", "fixture") };
        Directory.CreateDirectory(test.Workspace.JobDirectory(job.Id)); var offset = 0;
        for (var i = 0; i < texts.Length; i++)
        {
            job.Chunks.Add(new(i, offset, texts[i].Length, texts[i], false)); offset += texts[i].Length;
            var path = test.Workspace.ChunkPath(job, i); using (var file = File.Create(path))
            {
                WaveAudio.WriteHeader(file, 24000); using var writer = new BinaryWriter(file);
                for (var frame = 0; frame < 24000; frame++) writer.Write((short)(Math.Sin(frame * 2 * Math.PI * 220 * (i + 1) / 24000) * 8000));
            }
            job.Receipts.Add(new(i, await Workspace.HashFileAsync(path), job.Fingerprint, 1));
        }
        return job;
    }
    private static double TonePower(string path, double seconds, int frequency)
    {
        var region = WaveAudio.DataRegion(path); using var file = File.OpenRead(path); file.Position = region.Offset + (long)(seconds * 24000) * 2; using var reader = new BinaryReader(file);
        double real = 0, imaginary = 0;
        for (var i = 0; i < 4800; i++) { var sample = reader.ReadInt16() / 32768.0; real += sample * Math.Cos(i * 2 * Math.PI * frequency / 24000); imaginary += sample * Math.Sin(i * 2 * Math.PI * frequency / 24000); }
        return real * real + imaginary * imaginary;
    }
}
