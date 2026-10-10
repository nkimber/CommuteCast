namespace CommuteCast.Core;

public enum LibraryFilter { All, Completed, Queued, Processing, Stopped }
public enum LibrarySort { QueueThenNewest, Newest, Oldest, Title, Shortest, Longest }

/// <summary>Display ordering never changes the persisted dispatch order.</summary>
public static class LibraryQuery
{
    public static IReadOnlyList<Job> Apply(IEnumerable<Job> jobs, string search, LibraryFilter filter, LibrarySort sort)
    {
        var terms = search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var selected = jobs.Where(j => terms.All(t => j.Title.Contains(t, StringComparison.OrdinalIgnoreCase)) && filter switch
        {
            LibraryFilter.Completed => j.Stage == JobStage.Exported && j.ExportCommitted,
            LibraryFilter.Queued => j.Stage == JobStage.Queued && !j.DeletionRequested,
            LibraryFilter.Processing => j.Stage is JobStage.Preparing or JobStage.WaitingForService or JobStage.Synthesizing or JobStage.Assembling or JobStage.Validating or JobStage.Exporting,
            LibraryFilter.Stopped => j.Stage is JobStage.Failed or JobStage.Cancelled or JobStage.Deleting,
            _ => true
        });
        var ordered = sort switch
        {
            LibrarySort.Newest => selected.OrderByDescending(j => j.CreatedUtc),
            LibrarySort.Oldest => selected.OrderBy(j => j.CreatedUtc),
            LibrarySort.Title => selected.OrderBy(j => j.Title, StringComparer.OrdinalIgnoreCase),
            LibrarySort.Shortest => selected.OrderBy(j => ValidDuration(j) ? 0 : 1).ThenBy(j => j.DurationSeconds),
            LibrarySort.Longest => selected.OrderBy(j => ValidDuration(j) ? 0 : 1).ThenByDescending(j => j.DurationSeconds),
            _ => selected.OrderBy(j => j.Stage == JobStage.Queued ? 0 : 1)
                .ThenBy(j => j.Stage == JobStage.Queued ? j.QueuePosition : 0).ThenByDescending(j => j.CreatedUtc)
        };
        return ordered.ThenBy(j => j.Id, StringComparer.Ordinal).ToArray();
    }
    private static bool ValidDuration(Job j) => double.IsFinite(j.DurationSeconds) && j.DurationSeconds > 0;
}
