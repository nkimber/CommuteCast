using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;

namespace CommuteCast.Infrastructure;

public static class NaturalAudio
{
    // Count only edge silence; never trim speech, breaths or meaningful pauses.
    public static int AddedGapSamples(string left, string right, int requestedMilliseconds)
    {
        if (requestedMilliseconds <= 0) return 0;
        return Math.Max(0, requestedMilliseconds * 24 - EdgeSilence(left, false) - EdgeSilence(right, true));
    }
    private static int EdgeSilence(string path, bool leading)
    {
        using var file = File.OpenRead(path); var (offset, length) = WaveAudio.DataRegion(path);
        var samples = (int)Math.Min(length / 2, 48000); var data = new byte[samples * 2];
        file.Position = leading ? offset : offset + length - data.Length; file.ReadExactly(data);
        var silent = 0;
        for (var block = 0; block < samples; block += 240)
        {
            var count = Math.Min(240, samples - block); double sum = 0;
            for (var n = 0; n < count; n++)
            {
                var index = leading ? block + n : samples - block - n - 1;
                var value = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(index * 2, 2)) / 32768.0; sum += value * value;
            }
            if (Math.Sqrt(sum / count) > .001) break;
            silent += count;
        }
        return silent;
    }

    public static async Task<string> LoudnessFilterAsync(string ffmpeg, string input, CancellationToken ct)
    {
        var measured = await ProcessRunner.RunAsync(ffmpeg, ["-v", "info", "-nostats", "-nostdin", "-protocol_whitelist", "file,pipe", "-i", input,
            "-af", "loudnorm=I=-19:TP=-2:LRA=11:print_format=json", "-f", "null", "-"], TimeSpan.FromMinutes(15), ct);
        if (measured.ExitCode != 0) throw new IOException("The episode loudness measurement failed. Validated segments are retained.");
        var start = measured.Error.LastIndexOf('{'); var end = measured.Error.LastIndexOf('}');
        if (start < 0 || end <= start) throw new IOException("The loudness measurement returned no usable result.");
        using var json = JsonDocument.Parse(measured.Error[start..(end + 1)]);
        if (json.RootElement.GetProperty("input_i").GetString() == "-inf")
        {
            // LUFS has a 400ms measurement window. Short utterances use bounded static gain.
            var audio = WaveAudio.Inspect(input);
            if (audio.Rms <= 0 || audio.Peak <= 0) throw new IOException("The narration contains no usable audio.");
            var gain = Math.Min(Math.Pow(10, -19 / 20.0) / audio.Rms, Math.Pow(10, -2 / 20.0) / audio.Peak);
            return "volume=" + gain.ToString("R", CultureInfo.InvariantCulture);
        }
        string Value(string key)
        {
            var value = json.RootElement.GetProperty(key).GetString();
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                throw new IOException("The episode is too quiet for a valid loudness measurement.");
            return number.ToString("R", CultureInfo.InvariantCulture);
        }
        return $"loudnorm=I=-19:TP=-2:LRA=11:measured_I={Value("input_i")}:measured_TP={Value("input_tp")}:measured_LRA={Value("input_lra")}:measured_thresh={Value("input_thresh")}:offset={Value("target_offset")}:linear=true";
    }
}
