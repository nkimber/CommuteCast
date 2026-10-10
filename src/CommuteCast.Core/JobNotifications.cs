namespace CommuteCast.Core;

public enum JobNotificationKind { Exported, NeedsAttention }
public sealed record JobNotification(string JobId, JobNotificationKind Kind)
{
    // No private text, titles, paths or raw errors leave the application window.
    public string Heading => Kind == JobNotificationKind.Exported ? "CommuteCast · MP3 ready" : "CommuteCast · narration needs attention";
    public string Message => Kind == JobNotificationKind.Exported ? "A validated MP3 was exported locally. Open CommuteCast to check delivery." : "Open the saved narration in CommuteCast for the error and recovery steps.";
}
public sealed class JobNotifications
{
    private bool initialized;
    private readonly Dictionary<string, JobNotificationKind?> previous = [];
    public IReadOnlyList<JobNotification> Observe(IEnumerable<Job> jobs)
    {
        var next = jobs.ToDictionary(j => j.Id, j => j.Stage == JobStage.Exported && j.ExportCommitted ? (JobNotificationKind?)JobNotificationKind.Exported :
            j.Stage == JobStage.Failed || j.Stage == JobStage.Deleting && j.Error.Length > 0 ? JobNotificationKind.NeedsAttention : null);
        var result = initialized ? next.Where(p => p.Value is not null && (!previous.TryGetValue(p.Key, out var old) || old != p.Value))
            .Select(p => new JobNotification(p.Key, p.Value!.Value)).ToArray() : [];
        initialized = true; previous.Clear(); foreach (var entry in next) previous.Add(entry.Key, entry.Value);
        return result;
    }
}
