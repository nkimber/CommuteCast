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
        if (job.AudioContractVersion != ContractVersion || job.ChunkingVersion != "chunk450-v1") throw new IOException("Unsupported audio/chunk contract. Publication is blocked.");
        Chunker.ValidateManifest(job.Chunks, job.Prepared.Script);
        if (job.Receipts.Count != job.Chunks.Count || !job.Receipts.OrderBy(r => r.Index).Select(r => r.Index).SequenceEqual(Enumerable.Range(0, job.Chunks.Count)) ||
            job.Receipts.Any(r => r.Fingerprint != job.Fingerprint || !double.IsFinite(r.Duration) || r.Duration <= 0)) throw new IOException("Validated receipt sequence or contract is incompatible. Publication is blocked.");
    }
    public async Task NormalizeAsync(string input, string output, CancellationToken ct)
    {
        WaveAudio.DataRegion(input, false);
        var result = await ProcessRunner.RunAsync(settings.Ffmpeg, ["-v", "error", "-nostdin", "-y", "-protocol_whitelist", "file,pipe", "-i", input, "-map", "0:a:0", "-ar", "24000", "-ac", "1", "-c:a", "pcm_s16le", "-f", "wav", output], TimeSpan.FromMinutes(2), ct);
        if (result.ExitCode != 0) throw new IOException("The speech audio could not be decoded to PCM. This chunk will be regenerated on retry.");
    }
    public Task<AudioInfo> ValidateChunkAsync(string path, string text, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var info = WaveAudio.Inspect(path);
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        if (info.Duration < 0.08 || info.Duration > Math.Max(30, words * 2.5) || (words > 12 && info.Duration < words * 0.07)) throw new IOException("Chunk duration is implausible for the prepared text. Review the voice or text and retry.");
        if (info.Rms < 0.0001 || info.Peak > 1) throw new IOException("Chunk contains silence or invalid sample values. Export is blocked.");
        return info;
    }, ct);

    public Task AssembleAsync(Job job, string directory, CancellationToken ct) => AssembleAsync(job, directory, ct, null);
    public async Task AssembleAsync(Job job, string directory, CancellationToken ct, Func<Task>? checkpoint)
    {
        ValidateManifest(job);
        var assembled = Path.Combine(directory, "assembled.wav");
        await PrivateJobFiles.PrepareOutputAsync(job, directory, "assembled.wav", ct);
        var samples = job.Receipts.Sum(r => (long)Math.Round(r.Duration * 24000));
        // Add 150ms between chunks ending a sentence/paragraph. Hard splits have no inserted gap.
        var gaps = job.Chunks.Take(job.Chunks.Count - 1).Select(c => c.Text.TrimEnd().EndsWith('.') || c.Text.EndsWith('\n') ? 3600 : 0).ToArray();
        var total = samples + gaps.Sum(g => (long)g);
        if (total * 2 > uint.MaxValue - 36) throw new IOException("This narration exceeds the supported WAV size. Split it into separate submissions.");
        await using (var output = new FileStream(assembled, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
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
        }
        await PrivateJobFiles.RecordAsync(job, directory, "assembled.wav", ct);
        if (checkpoint is not null) await checkpoint();
        var temporary = Path.Combine(directory, "encoded.partial.mp3");
        await PrivateJobFiles.PrepareOutputAsync(job, directory, "encoded.partial.mp3", ct);
        var encode = await ProcessRunner.RunAsync(settings.Ffmpeg, ["-v", "error", "-nostdin", "-y", "-protocol_whitelist", "file,pipe", "-f", "wav", "-i", assembled, "-c:a", "libmp3lame", "-b:a", "128k", "-id3v2_version", "3", "-metadata", "title=" + job.Title, "-metadata", "artist=CommuteCast", "-metadata", "date=" + job.CreatedUtc.ToString("O"), "-metadata", "commutecast_created_utc=" + job.CreatedUtc.ToString("O"), "-metadata", "commutecast_job_id=" + job.Id, "-metadata", "comment=CommuteCast job " + job.Id, temporary], TimeSpan.FromMinutes(15), ct);
        if (encode.ExitCode != 0) throw new IOException("MP3 encoding failed. Validated chunks are retained. Check FFmpeg and free disk space.");
        await PrivateJobFiles.RecordAsync(job, directory, "encoded.partial.mp3", ct);
        if (checkpoint is not null) await checkpoint();
        ct.ThrowIfCancellationRequested();
        await PrivateJobFiles.PrepareOutputAsync(job, directory, "complete.mp3", ct);
        File.Move(temporary, Path.Combine(directory, "complete.mp3"), false);
        await PrivateJobFiles.RecordAsync(job, directory, "complete.mp3", ct);
        if (checkpoint is not null) await checkpoint();
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
        var gaps = job.Chunks.Take(job.Chunks.Count - 1).Count(c => c.Text.TrimEnd().EndsWith('.') || c.Text.EndsWith('\n')) * .15;
        var expected = job.Receipts.Sum(r => r.Duration) + gaps;
        if (Math.Abs(expected - duration) > .3) throw new IOException("Finished MP3 duration does not match the complete ordered chunk sequence.");
        var decode = await ProcessRunner.RunAsync(settings.Ffmpeg, ["-v", "error", "-xerror", "-nostdin", "-protocol_whitelist", "file,pipe", "-f", "mp3", "-i", path, "-f", "null", "-"], TimeSpan.FromMinutes(10), ct);
        if (decode.ExitCode != 0) throw new IOException("Finished MP3 failed full decoding. Export is blocked.");
        return new(duration, 24000, 1, (long)(duration * 24000), 0, 0);
    }
}

public static class WaveAudio
{
    public static (long Offset, long Length) DataRegion(string path, bool canonical = true)
    {
        using var file = File.OpenRead(path);
        using var reader = new BinaryReader(file, Encoding.ASCII);
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
