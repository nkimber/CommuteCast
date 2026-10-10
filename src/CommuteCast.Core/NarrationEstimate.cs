using System.Text.RegularExpressions;

namespace CommuteCast.Core;

public sealed record EstimateRange(double LowerSeconds, double UpperSeconds);
public sealed record NarrationEstimate(int WordCount, int SampleCount, EstimateRange? Listening, EstimateRange? Generation)
{
    public const int RequiredSamples = 3;
    public static int CountWords(string text) => Regex.Matches(text, @"[\p{L}\p{N}]+(?:['’][\p{L}\p{N}]+)*", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)).Count;
    public static NarrationEstimate Calculate(string script, NarrationOptions options, ProviderInfo? provider, IEnumerable<Job> history)
    {
        options.Validate();
        var words = CountWords(script);
        if (words == 0 || provider is null) return new(words, 0, null, null);
        // Model/image and voice are exact. Normalize pace rather than mixing raw durations.
        // Technical material remains uncertain: ranges are widened and are never a validation threshold.
        var samples = history.Where(j => j.Stage == JobStage.Exported && j.ExportCommitted && j.Attempts <= 1 &&
                j.Settings.Engine == options.Engine && j.Settings.Voice == options.Voice &&
                j.Settings.ProviderFingerprint == provider.Fingerprint && j.Settings.ProviderImageId == provider.ImageId &&
                double.IsFinite(j.DurationSeconds) && j.DurationSeconds > 0 && double.IsFinite(j.Settings.Speed) && j.Settings.Speed is >= .7 and <= 1.4)
            .OrderByDescending(j => j.CreatedUtc).Take(30)
            .Select(j => (Job: j, Words: CountWords(j.Prepared.Script))).Where(s => s.Words >= 20).ToArray();
        if (samples.Length < RequiredSamples) return new(words, samples.Length, null, null);
        var listening = Bounds(samples.Select(s => s.Job.DurationSeconds * s.Job.Settings.Speed / s.Words * words / options.Speed));
        var timed = samples.Where(s => s.Job.RunTiming is { ProcessingMilliseconds: > 0 } timing && s.Job.SpeechReadinessMilliseconds is >= 0 &&
            s.Job.SpeechReadinessMilliseconds < timing.ProcessingMilliseconds).ToArray();
        EstimateRange? generation = null;
        if (timed.Length >= RequiredSamples)
        {
            // Service startup/loading is a fixed observed allowance, separate from work proportional to script length.
            var work = Bounds(timed.Select(s => (s.Job.RunTiming!.ProcessingMilliseconds - s.Job.SpeechReadinessMilliseconds!.Value) / 1000.0 /
                s.Words * words * s.Job.Settings.Speed / options.Speed));
            generation = new(work.LowerSeconds + timed.Min(s => s.Job.SpeechReadinessMilliseconds!.Value) / 1000.0,
                work.UpperSeconds + timed.Max(s => s.Job.SpeechReadinessMilliseconds!.Value) / 1000.0 * 1.25);
        }
        return new(words, samples.Length, listening, generation);
    }
    private static EstimateRange Bounds(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return new(Math.Max(1, sorted[0] * .8), Math.Max(1, sorted[^1] * 1.25));
    }
    public static string FormatRange(EstimateRange range) => $"{FormatTime(range.LowerSeconds)}–{FormatTime(range.UpperSeconds)}";
    private static string FormatTime(double seconds) => seconds < 60 ? $"{Math.Ceiling(seconds):0}s" : $"{Math.Ceiling(seconds / 60):0} min";
}
