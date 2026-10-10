using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public class PowerNotificationTests
{
    [Fact]
    public void NotificationsDoNotReplayHistoryAndOnlyAnnounceNewOutcomeTransitions()
    {
        var tracker = new JobNotifications(); var job = new Job { Title = "Private title", Source = "Private source", Error = "Private error", Stage = JobStage.Exported, ExportCommitted = true };
        Assert.Empty(tracker.Observe([job]));
        job.Stage = JobStage.Queued; job.ExportCommitted = false; Assert.Empty(tracker.Observe([job]));
        job.Stage = JobStage.Failed;
        var failure = Assert.Single(tracker.Observe([job])); Assert.Equal(JobNotificationKind.NeedsAttention, failure.Kind);
        Assert.DoesNotContain("Private", failure.Message); Assert.DoesNotContain("Private", failure.Heading);
        Assert.Empty(tracker.Observe([job]));
        job.Stage = JobStage.Queued; Assert.Empty(tracker.Observe([job]));
        job.Stage = JobStage.Exported; Assert.Empty(tracker.Observe([job]));
        job.ExportCommitted = true; Assert.Equal(JobNotificationKind.Exported, Assert.Single(tracker.Observe([job])).Kind);
        job.Stage = JobStage.Cancelled; job.ExportCommitted = false; Assert.Empty(tracker.Observe([job]));
    }
    [Fact]
    public async Task SuspendPreservesValidatedChunksAndWakeReusesThemWithoutChangingPauseOrSource()
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace); var provider = new PowerProvider();
        await using var queue = new QueueCoordinator(test.Workspace, store, provider, new(new()), new(test.Workspace, store));
        var source = string.Join("\n\n", Enumerable.Range(0, 3).Select(i => $"Marker {i}. " + new string('x', 410) + "."));
        var job = new Job { Source = source, Prepared = TextPreparation.Prepare(source), Settings = new("kokoro", "af_heart", 1, false, "", "fixture"), Destination = test.Destination };
        await queue.InitializeAsync(); await queue.AddAsync(job);
        await provider.SecondEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var first = Assert.Single(queue.Snapshot()).Receipts.Single(); var hash = await Workspace.HashFileAsync(test.Workspace.ChunkPath(job, 0));
        await queue.SuspendForPowerAsync();
        var interrupted = Assert.Single(await store.LoadAsync());
        Assert.Equal(JobStage.Queued, interrupted.Stage); Assert.False(interrupted.CancellationRequested); Assert.False(queue.Paused);
        Assert.Equal(source, interrupted.Source); Assert.Equal(first, Assert.Single(interrupted.Receipts));
        Assert.True(queue.PowerSuspended); await Task.Delay(500); Assert.Equal(1, provider.Completed);
        queue.Paused = true; provider.BlockSecond = false; queue.ReleasePowerHold();
        await Task.Delay(500); Assert.Equal(1, provider.Completed); Assert.True(queue.Paused);
        queue.Paused = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (Assert.Single(queue.Snapshot()).Stage != JobStage.Exported) await Task.Delay(50, deadline.Token);
        Assert.Equal(3, provider.Completed); Assert.Equal(hash, await Workspace.HashFileAsync(test.Workspace.ChunkPath(job, 0)));
        Assert.Equal(first, Assert.Single(queue.Snapshot()).Receipts.Single(r => r.Index == 0));
        Assert.Equal(source, Assert.Single(queue.Snapshot()).Source);
    }
    private sealed class PowerProvider : ISpeechProvider
    {
        public TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockSecond { get; set; } = true;
        public int Completed { get; private set; }
        public Task<ProviderInfo> ReadyAsync(string engine, CancellationToken ct) => Task.FromResult(new ProviderInfo(engine, "fixture", ["af_heart"], "ready", 0));
        public async Task SynthesizeAsync(NarrationSettings settings, string text, string output, CancellationToken ct)
        {
            if (Completed == 1 && BlockSecond) { SecondEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
            ct.ThrowIfCancellationRequested(); TestWorkspace.WriteWave(output, 2); Completed++;
        }
    }
}
