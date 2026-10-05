using CommuteCast.Core;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CommuteCast.Infrastructure;

public sealed class AudioPipeline(AppSettings settings) : IAudioPipeline
{
    public async Task NormalizeAsync(string input, string output, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(settings.Ffmpeg, ["-v", "error", "-nostdin", "-y", "-i", input, "-map", "0:a:0", "-ar", "24000", "-ac", "1", "-c:a", "pcm_s16le", "-f", "wav", output], TimeSpan.FromMinutes(2), ct);
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

    public async Task AssembleAsync(Job job, string directory, CancellationToken ct)
    {
        var assembled = Path.Combine(directory, "assembled.wav");
        var samples = job.Receipts.Sum(r => (long)Math.Round(r.Duration * 24000));
        // Add 150ms between chunks ending a sentence/paragraph. Hard splits have no inserted gap.
        var gaps = job.Chunks.Take(job.Chunks.Count - 1).Select(c => c.Text.TrimEnd().EndsWith('.') || c.Text.EndsWith('\n') ? 3600 : 0).ToArray();
        var total = samples + gaps.Sum(g => (long)g);
        if (total * 2 > uint.MaxValue - 36) throw new IOException("This narration exceeds the supported WAV size. Split it into separate submissions.");
        await using (var output = new FileStream(assembled, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            WaveAudio.WriteHeader(output, total);
            foreach (var chunk in job.Chunks)
            {
                ct.ThrowIfCancellationRequested();
                var path = Path.Combine(directory, $"chunk-{chunk.Index:D5}.wav");
                var (offset, length) = WaveAudio.DataRegion(path);
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
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
        var temporary = Path.Combine(directory, "encoded.partial.mp3");
        var encode = await ProcessRunner.RunAsync(settings.Ffmpeg, ["-v", "error", "-nostdin", "-y", "-i", assembled, "-c:a", "libmp3lame", "-b:a", "128k", "-id3v2_version", "3", "-metadata", "title=" + job.Title, "-metadata", "artist=CommuteCast", "-metadata", "date=" + job.CreatedUtc.ToString("O"), "-metadata", "comment=CommuteCast job " + job.Id, temporary], TimeSpan.FromMinutes(15), ct);
        if (encode.ExitCode != 0) throw new IOException("MP3 encoding failed. Validated chunks are retained. Check FFmpeg and free disk space.");
        ct.ThrowIfCancellationRequested();
        File.Move(temporary, Path.Combine(directory, "complete.mp3"), true);
    }

    public async Task<AudioInfo> ValidateFinalAsync(Job job, string path, CancellationToken ct)
    {
        var probe = await ProcessRunner.RunAsync(settings.Ffprobe, ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_name,sample_rate,channels,duration", "-of", "json", path], TimeSpan.FromSeconds(30), ct);
        if (probe.ExitCode != 0) throw new IOException("The finished MP3 could not be probed. Export is blocked.");
        using var json = JsonDocument.Parse(probe.Output);
        var stream = json.RootElement.GetProperty("streams")[0];
        var duration = double.Parse(stream.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        if (stream.GetProperty("codec_name").GetString() != "mp3" || stream.GetProperty("sample_rate").GetString() != "24000" || stream.GetProperty("channels").GetInt32() != 1) throw new IOException("Finished MP3 has an unexpected format.");
        var gaps = job.Chunks.Take(job.Chunks.Count - 1).Count(c => c.Text.TrimEnd().EndsWith('.') || c.Text.EndsWith('\n')) * .15;
        var expected = job.Receipts.Sum(r => r.Duration) + gaps;
        if (Math.Abs(expected - duration) > .3) throw new IOException("Finished MP3 duration does not match the complete ordered chunk sequence.");
        var decode = await ProcessRunner.RunAsync(settings.Ffmpeg, ["-v", "error", "-xerror", "-nostdin", "-i", path, "-f", "null", "-"], TimeSpan.FromMinutes(10), ct);
        if (decode.ExitCode != 0) throw new IOException("Finished MP3 failed full decoding. Export is blocked.");
        return new(duration, 24000, 1, (long)(duration * 24000), 0, 0);
    }
}

public static class WaveAudio
{
    public static (long Offset, long Length) DataRegion(string path)
    {
        using var file = File.OpenRead(path);
        using var reader = new BinaryReader(file, Encoding.ASCII);
        if (new string(reader.ReadChars(4)) != "RIFF") throw new IOException("Expected RIFF audio.");
        reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new IOException("Expected WAV audio.");
        var validFormat = false;
        while (file.Position + 8 <= file.Length)
        {
            var type = new string(reader.ReadChars(4));
            var length = reader.ReadUInt32();
            var offset = file.Position;
            if (offset + length > file.Length) throw new IOException("Truncated WAV audio.");
            if (type == "fmt ")
            {
                if (length < 16 || reader.ReadUInt16() != 1 || reader.ReadUInt16() != 1 || reader.ReadInt32() != 24000) throw new IOException("Expected mono 24kHz PCM audio.");
                reader.ReadInt32();
                if (reader.ReadUInt16() != 2 || reader.ReadUInt16() != 16) throw new IOException("Expected 16-bit PCM audio.");
                validFormat = true;
            }
            if (type == "data")
            {
                if (!validFormat || length == 0 || length % 2 != 0) throw new IOException("Invalid PCM data region.");
                return (offset, length);
            }
            file.Position = offset + length + length % 2;
        }
        throw new IOException("WAV data is missing.");
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
