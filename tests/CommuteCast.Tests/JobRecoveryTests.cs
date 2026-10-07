using CommuteCast.Core;

namespace CommuteCast.Tests;

public class JobRecoveryTests
{
    [Fact]
    public void ExhaustionExplainsTheLimitWithoutInventingACauseAndUsesSavedEngine()
    {
        var job = new Job { Stage = JobStage.Failed, FailedStage = JobStage.WaitingForService,
            Settings = new("piper", "en_US-lessac-medium", 1, false, "", ""),
            Error = "Automatic speech recovery is exhausted for this outage." };
        var steps = JobRecovery.Instructions(job);
        Assert.Contains("does not identify the original", steps);
        Assert.Contains("piper recovery allowance", steps);
        Assert.Contains("Check setup", steps);
        Assert.Contains("Retry / resume on this saved narration", steps);
        Assert.Contains("Resume queue", steps);
        Assert.DoesNotContain("kokoro", steps);
    }

    [Fact]
    public void ChangedModelDoesNotRecommendCombiningExistingAudioWithNewSettings()
    {
        var job = new Job { Stage = JobStage.Failed, Error = "The speech model or image changed since submission." };
        var steps = JobRecovery.Instructions(job);
        Assert.Contains("original compatible", steps);
        Assert.Contains("Use as a new draft", steps);
        Assert.Contains("cannot be mixed", steps);
    }

    [Theory]
    [InlineData(FailureCategory.Export, "Retry export to another folder")]
    [InlineData(FailureCategory.Storage, "free disk space")]
    [InlineData(FailureCategory.AccessDenied, "permissions")]
    [InlineData(FailureCategory.ServiceConnection, "Check setup")]
    [InlineData(FailureCategory.AudioValidation, "Check encoder")]
    [InlineData(FailureCategory.Unexpected, "copy these details")]
    public void RecoveryMatchesTheRecordedFailure(FailureCategory category, string action)
    {
        var job = new Job { Stage = JobStage.Failed, FailureCategory = category };
        Assert.Contains(action, JobRecovery.Instructions(job));
    }

    [Theory]
    [InlineData(JobStage.Queued)] [InlineData(JobStage.Synthesizing)] [InlineData(JobStage.Exported)]
    public void HealthyStagesDoNotShowRepairInstructions(JobStage stage) =>
        Assert.Empty(JobRecovery.Instructions(new Job { Stage = stage }));
}
