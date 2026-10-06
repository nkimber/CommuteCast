using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public class StorageBudgetTests
{
    [Fact] public async Task MultiplePendingJobsReserveFutureAudioAndCountExistingFilesOnce()
    {
        using var test = new TestWorkspace(); var a = new Job { Prepared = TextPreparation.Prepare(new string('x', 6000)) }; var b = new Job { Prepared = a.Prepared };
        Directory.CreateDirectory(test.Workspace.JobDirectory(a.Id)); await File.WriteAllBytesAsync(test.Workspace.ChunkPath(a, 0), new byte[1048576]);
        var usage = await new CacheMaintenance(test.Workspace).MeasureAsync([a, b], null, default);
        Assert.Equal(StorageBudget.Estimate(a.Prepared.Script) * 2 - 1048576, usage.ReservedBytes);
        Assert.Throws<IOException>(() => StorageBudget.EnsureFits(usage, "next", 512, long.MaxValue));
        b.Stage = JobStage.Cancelled;
        var after = await new CacheMaintenance(test.Workspace).MeasureAsync([a, b], null, default);
        Assert.Equal(StorageBudget.Estimate(a.Prepared.Script) - 1048576, after.ReservedBytes);
        StorageBudget.EnsureFits(after, "next", 512, long.MaxValue);
        Assert.Throws<IOException>(() => StorageBudget.EnsureFits(after, "next", 512, 100));
    }
    [Fact] public void EmptyDestinationHasActionableSetupError()
    {
        using var test = new TestWorkspace(); var error = Assert.Throws<ArgumentException>(() => test.Workspace.GuardLocalDestination(""));
        Assert.Contains("Choose an output folder", error.Message); Assert.Contains("draft is retained", error.Message);
    }
    [Fact] public async Task QueueAdmissionRefusesOversizedReservationsBeforeDurableAcknowledgement()
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace);
        using var provider = new LocalSpeechProvider(test.Workspace);
        await using var queue = new QueueCoordinator(test.Workspace, store, provider, new(new()), new(test.Workspace, store)) { Paused = true, PrivateStorageLimitMiB = 128 };
        await queue.InitializeAsync();
        var job = new Job { Prepared = TextPreparation.Prepare(new string('x', 6000)) };
        await Assert.ThrowsAsync<IOException>(() => queue.AddAsync(job)); Assert.Empty(queue.Snapshot()); Assert.Empty(await store.LoadAsync());
        queue.PrivateStorageLimitMiB = 0; await queue.AddAsync(job); await queue.CancelAsync(job.Id);
        queue.PrivateStorageLimitMiB = 128;
        await Assert.ThrowsAsync<IOException>(() => queue.RetryAsync(job.Id)); Assert.Equal(JobStage.Cancelled, Assert.Single(queue.Snapshot()).Stage);
    }
}
