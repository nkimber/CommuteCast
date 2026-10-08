using CommuteCast.Core;

namespace CommuteCast.Tests;

public class JobProgressTests
{
    [Fact]
    public void SavedQueuedJobExplainsThatResubmissionIsUnnecessary()
    {
        var job = new Job();
        var pending = JobProgress.Describe(job);
        Assert.Contains("saved", pending.Status);
        Assert.Contains("do not need to submit", pending.Guidance);
        Assert.False(pending.IsWorking);
        var paused = JobProgress.Describe(job, true);
        Assert.Contains("paused", paused.Status);
        Assert.Contains("Resume the queue", paused.Guidance);
    }

    [Fact]
    public void FailureAndCancellationDirectUsersBackToTheExistingItem()
    {
        var job = new Job { Stage = JobStage.Failed, Error = "Speech services are not provisioned." };
        var failed = JobProgress.Describe(job);
        Assert.True(failed.NeedsAttention);
        Assert.False(failed.IsWorking);
        Assert.Contains("existing item", failed.Guidance);
        job.Stage = JobStage.Cancelled;
        Assert.Contains("existing narration", JobProgress.Describe(job).Guidance);
        Assert.False(JobProgress.Describe(job).NeedsAttention);
    }

    [Theory]
    [InlineData(JobStage.Preparing)]
    [InlineData(JobStage.WaitingForService)]
    [InlineData(JobStage.Synthesizing)]
    [InlineData(JobStage.Assembling)]
    [InlineData(JobStage.Validating)]
    [InlineData(JobStage.Exporting)]
    public void ActiveStagesRemainWorkingEvenWhenAllChunksHaveCompleted(JobStage stage)
    {
        var job = new Job { Stage = stage, CompletedChunks = 1, Chunks = [new(0, 0, 5, "Hello", false)], Receipts = [new(0, "hash", "fingerprint", 1)] };
        var progress = JobProgress.Describe(job);
        Assert.True(progress.IsWorking);
        Assert.False(progress.NeedsAttention);
        Assert.DoesNotContain("Exported", progress.Status);
    }

    [Fact]
    public void FailedRemovalDoesNotAnimateAsActiveWork()
    {
        var job = new Job { Stage = JobStage.Deleting, Error = "Access denied" };
        Assert.True(JobProgress.Describe(job).NeedsAttention);
        Assert.False(JobProgress.Describe(job).IsWorking);
    }

    [Fact]
    public void CancellationKeepsWorkingUntilItHasSettled()
    {
        var job = new Job { Stage = JobStage.Synthesizing, CancellationRequested = true };
        Assert.Contains("Cancelling", JobProgress.Describe(job).Status);
        Assert.True(JobProgress.Describe(job).IsWorking);
        job.Stage = JobStage.Cancelled;
        Assert.False(JobProgress.Describe(job).IsWorking);
    }

    [Theory]
    [InlineData(JobStage.Failed)]
    [InlineData(JobStage.Deleting)]
    public void FailureWinsOverAnOutstandingCancellationRequest(JobStage stage)
    {
        var job = new Job { Stage = stage, Error = "Access denied", CancellationRequested = true };
        var progress = JobProgress.Describe(job);
        Assert.True(progress.NeedsAttention);
        Assert.False(progress.IsWorking);
        Assert.DoesNotContain("Cancelling", progress.Status);
    }

    [Fact]
    public void CommittedExportWinsOverLateCancellation()
    {
        var job = new Job { Stage = JobStage.Exported, ExportCommitted = true, CancellationRequested = true };
        var progress = JobProgress.Describe(job);
        Assert.Contains("Exported locally", progress.Status);
        Assert.Contains("Check OneDrive", progress.Guidance);
        Assert.False(progress.IsWorking);
    }
}
