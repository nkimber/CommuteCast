using CommuteCast.Core;

namespace CommuteCast.Tests;

public class LibraryQueryTests
{
    private static Job Item(string title, JobStage stage, long position = 0, double duration = 0) =>
        new() { Title = title, Stage = stage, QueuePosition = position, DurationSeconds = duration, ExportCommitted = stage == JobStage.Exported };
    [Fact]
    public void SearchMatchesAllWordsWithoutReadingPrivateSource()
    {
        var a = Item("Technical API briefing", JobStage.Exported); a.Source = "secret topic";
        var b = Item("API overview", JobStage.Queued);
        Assert.Equal([a], LibraryQuery.Apply([a, b], " API  technical ", LibraryFilter.All, LibrarySort.Title));
        Assert.Empty(LibraryQuery.Apply([a], "secret", LibraryFilter.All, LibrarySort.Title));
    }
    [Theory]
    [InlineData(LibraryFilter.Completed, JobStage.Exported)]
    [InlineData(LibraryFilter.Queued, JobStage.Queued)]
    [InlineData(LibraryFilter.Processing, JobStage.Synthesizing)]
    [InlineData(LibraryFilter.Stopped, JobStage.Cancelled)]
    public void StatusFilterSelectsOnlyMatchingState(LibraryFilter filter, JobStage stage)
    {
        var jobs = new[] { Item("complete", JobStage.Exported), Item("queue", JobStage.Queued), Item("active", JobStage.Synthesizing), Item("stop", JobStage.Cancelled) };
        Assert.Equal(stage, Assert.Single(LibraryQuery.Apply(jobs, "", filter, LibrarySort.Newest)).Stage);
    }
    [Fact]
    public void DisplaySortingDoesNotChangeQueueOrderAndUnknownLengthsSortLast()
    {
        var a = Item("Zeta", JobStage.Queued, 1); var b = Item("Alpha", JobStage.Queued, 2);
        Assert.Equal([b, a], LibraryQuery.Apply([a, b], "", LibraryFilter.All, LibrarySort.Title));
        Assert.Equal([a, b], LibraryQuery.Apply([b, a], "", LibraryFilter.All, LibrarySort.QueueThenNewest));
        Assert.Equal(1, a.QueuePosition); Assert.Equal(2, b.QueuePosition);
        var shortJob = Item("short", JobStage.Exported, duration: 10); var longJob = Item("long", JobStage.Exported, duration: 20);
        Assert.Equal([shortJob, longJob, a], LibraryQuery.Apply([a, longJob, shortJob], "", LibraryFilter.All, LibrarySort.Shortest));
        Assert.Equal([longJob, shortJob, a], LibraryQuery.Apply([a, longJob, shortJob], "", LibraryFilter.All, LibrarySort.Longest));
    }
}
