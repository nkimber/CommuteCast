using CommuteCast.Core;
using CommuteCast.Desktop;

namespace CommuteCast.Desktop.Tests;

[Collection("Desktop")]
public class AssemblyProgressTests
{
    [Fact] public void AssemblyResetsDisplayedProgressAndRefreshesJoinedSegments()
    {
        var job = new Job { Stage = JobStage.Assembling, CompletedChunks = 2,
            Chunks = [new(0, 0, 1, "A", false), new(1, 1, 2, "B", false)] };
        var view = new JobView(job, DesktopHost.Workspace());
        job.Activity.Start(new(10000, null, 0)); job.Activity.StartAssembly(2);
        Assert.Equal(0, view.Progress);
        Assert.Contains("0 of 2 segments joined", view.ProgressSummary);
        Assert.Contains("MP3 assembly time", view.TimingSummary);
        var notifications = new List<string?>(); view.PropertyChanged += (_, change) => notifications.Add(change.PropertyName);
        job.Activity.SegmentJoined(); view.RefreshActivity();
        Assert.Equal(1, view.Progress);
        Assert.Contains(nameof(JobView.Progress), notifications);
        Assert.Contains(nameof(JobView.ProgressSummary), notifications);
        job.Activity.SegmentJoined(); job.Activity.Update("All 2 segments joined · encoding the MP3");
        Assert.Equal(2, view.Progress); Assert.True(view.IsWorking);
        Assert.Contains("encoding", view.ActivityNotice);
        job.Activity.FinishAssembly(); job.Stage = JobStage.Validating;
        Assert.Equal(2, view.Progress);
        Assert.Contains("completed and validated", view.ProgressSummary);
    }
}
