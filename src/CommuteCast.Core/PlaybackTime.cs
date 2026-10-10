namespace CommuteCast.Core;

public static class PlaybackTime
{
    public static double Clamp(double seconds, double duration) => double.IsFinite(seconds) && double.IsFinite(duration) && duration > 0 ? Math.Clamp(seconds, 0, duration) : 0;
    public static string Format(double seconds)
    {
        var span = TimeSpan.FromSeconds(double.IsFinite(seconds) ? Math.Clamp(seconds, 0, 360_000) : 0);
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{span.Minutes}:{span.Seconds:00}";
    }
}
