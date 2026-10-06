using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public class RecoveryTests
{
    [Fact] public async Task PendingOrderSurvivesRelaunchWithoutChangingCapturedSettings()
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace);
        var first = MakeJob(test); var second = MakeJob(test); var third = MakeJob(test);
        second.Settings = second.Settings with { Speed = 1.2 }; second.Title = "Second";
        await using (var queue = new QueueCoordinator(test.Workspace, store, new FakeProvider(), new(new()), new(test.Workspace, store)) { Paused = true })
        {
            await queue.InitializeAsync(); await queue.AddAsync(first); await queue.AddAsync(second); await queue.AddAsync(third);
            await queue.MovePendingAsync(second.Id, -1);
            Assert.Equal(new[] { second.Id, first.Id, third.Id }, queue.Snapshot().Select(j => j.Id));
        }
        await using var reopened = new QueueCoordinator(test.Workspace, store, new FakeProvider(), new(new()), new(test.Workspace, store)) { Paused = true };
        await reopened.InitializeAsync();
        Assert.Equal(new[] { second.Id, first.Id, third.Id }, reopened.Snapshot().Select(j => j.Id));
        var restored = reopened.Snapshot().First(); Assert.Equal(second.Settings, restored.Settings); Assert.Equal(second.CreatedUtc, restored.CreatedUtc); Assert.Equal(second.Destination, restored.Destination);
    }
    [Fact] public async Task CancelledAndCompletedJobsCannotBeReordered()
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace);
        await using var queue = new QueueCoordinator(test.Workspace, store, new FakeProvider(), new(new()), new(test.Workspace, store)) { Paused = true };
        await queue.InitializeAsync(); var job = MakeJob(test); await queue.AddAsync(job); await queue.CancelAsync(job.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => queue.MovePendingAsync(job.Id, -1));
        await Assert.ThrowsAsync<ArgumentException>(() => queue.MovePendingAsync(job.Id, 0));
    }
    [Fact] public async Task InterruptedDeletionResumesBeforeGeneration()
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace); var provider = new FakeProvider();
        var job = MakeJob(test); job.DeletionRequested = true; job.Stage = JobStage.Deleting;
        Directory.CreateDirectory(test.Workspace.JobDirectory(job.Id));
        await File.WriteAllTextAsync(Path.Combine(test.Workspace.JobDirectory(job.Id), "partial.txt"), "private");
        await store.SaveAsync(job);
        await using var queue = new QueueCoordinator(test.Workspace, store, provider, new(new()), new(test.Workspace, store));
        await queue.InitializeAsync();
        Assert.Empty(queue.Snapshot()); Assert.Empty(await store.LoadAsync()); Assert.False(Directory.Exists(test.Workspace.JobDirectory(job.Id))); Assert.Equal(0, provider.Calls);
    }
    [Fact] public async Task DurableStoreSurvivesReopenAndRecordsFrozenSource()
    {
        using var test = new TestWorkspace();
        var job = new Job { Title = "Saved", Source = "Source that must survive", Prepared = TextPreparation.Prepare("Source that must survive"), Destination = test.Destination };
        await new SqliteJobStore(test.Workspace).SaveAsync(job);
        var restored = Assert.Single(await new SqliteJobStore(test.Workspace).LoadAsync());
        Assert.Equal(job.Source, restored.Source); Assert.Equal(job.CreatedUtc, restored.CreatedUtc); Assert.Equal(job.Fingerprint, restored.Fingerprint);
    }
    [Fact] public async Task PublicationIsIdempotentAndKeepsUnrelatedFiles()
    {
        using var test = new TestWorkspace();
        var store = new SqliteJobStore(test.Workspace); var publisher = new ExportPublisher(test.Workspace, store);
        var job = await GeneratedJobAsync(test);
        var unrelated = Path.Combine(test.Destination, "keep.txt"); await File.WriteAllTextAsync(unrelated, "keep");
        await publisher.PublishAsync(job, default); await publisher.PublishAsync(job, default);
        Assert.Single(Directory.GetFiles(test.Destination, "*.mp3")); Assert.True(job.ExportCommitted); Assert.Equal(JobStage.Exported, job.Stage);
        await publisher.RemoveManagedExportAsync(job, default);
        Assert.True(File.Exists(unrelated)); Assert.Empty(Directory.GetFiles(test.Destination, "*.mp3"));
    }
    [Fact] public async Task CrashAfterRenameReconcilesWithoutDuplicateExport()
    {
        using var test = new TestWorkspace();
        var store = new SqliteJobStore(test.Workspace); var publisher = new ExportPublisher(test.Workspace, store);
        var job = await GeneratedJobAsync(test);
        job.ExportName = ExportPublisher.Filename(job); job.ExportHash = job.FinalHash; job.Stage = JobStage.Exporting;
        await store.SaveAsync(job);
        File.Copy(test.Workspace.FinalPath(job), Path.Combine(test.Destination, job.ExportName));
        var restored = Assert.Single(await store.LoadAsync());
        await publisher.ReconcileAsync(restored, default);
        Assert.True(restored.ExportCommitted); Assert.Equal(JobStage.Exported, restored.Stage); Assert.Single(Directory.GetFiles(test.Destination, "*.mp3"));
    }
    [Fact] public async Task ExportCollisionAndChangedOwnedFilesArePreserved()
    {
        using var test = new TestWorkspace();
        var store = new SqliteJobStore(test.Workspace); var publisher = new ExportPublisher(test.Workspace, store);
        var job = await GeneratedJobAsync(test);
        var final = Path.Combine(test.Destination, ExportPublisher.Filename(job));
        await File.WriteAllTextAsync(final, "unrelated");
        await Assert.ThrowsAsync<IOException>(() => publisher.PublishAsync(job, default));
        Assert.Equal("unrelated", await File.ReadAllTextAsync(final));
        await Assert.ThrowsAsync<IOException>(() => publisher.RemoveManagedExportAsync(job, default));
        Assert.True(File.Exists(final));
    }
    [Fact] public async Task CancelBeforePublicationNeverCreatesFinalFile()
    {
        using var test = new TestWorkspace(); var job = await GeneratedJobAsync(test);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ExportPublisher(test.Workspace, new SqliteJobStore(test.Workspace)).PublishAsync(job, cancel.Token));
        Assert.Empty(Directory.GetFiles(test.Destination, "*.mp3"));
    }
    [Fact] public async Task ExportFailureRetainsCompletePrivateFile()
    {
        using var test = new TestWorkspace(); var job = await GeneratedJobAsync(test);
        job.Destination = Path.Combine(test.Parent, "missing");
        await Assert.ThrowsAsync<IOException>(() => new ExportPublisher(test.Workspace, new SqliteJobStore(test.Workspace)).PublishAsync(job, default));
        Assert.True(File.Exists(test.Workspace.FinalPath(job))); Assert.False(job.ExportCommitted);
    }
    [Fact] public async Task WrongModelIdentityStopsBeforeSynthesis()
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace); var provider = new FakeProvider("different");
        await using var queue = new QueueCoordinator(test.Workspace, store, provider, new(new()), new(test.Workspace, store));
        await queue.InitializeAsync();
        var job = MakeJob(test);
        await queue.AddAsync(job);
        await WaitForAsync(() => queue.Snapshot().Any(j => j.Stage == JobStage.Failed));
        Assert.Equal(0, provider.Calls); Assert.Empty(Directory.GetFiles(test.Destination, "*.mp3"));
    }
    [Fact] public async Task CancelledQueuedJobNeverDispatchesAndCanRetry()
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace); var provider = new FakeProvider();
        await using var queue = new QueueCoordinator(test.Workspace, store, provider, new(new()), new(test.Workspace, store)) { Paused = true };
        await queue.InitializeAsync(); var job = MakeJob(test); await queue.AddAsync(job); await queue.CancelAsync(job.Id);
        Assert.Equal(JobStage.Cancelled, Assert.Single(queue.Snapshot()).Stage); Assert.Equal(0, provider.Calls);
        await queue.RetryAsync(job.Id); Assert.Equal(JobStage.Queued, Assert.Single(queue.Snapshot()).Stage);
    }
    [Fact] public async Task ReopenedInFlightJobRequeuesBeforeDispatch()
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace); var job = MakeJob(test); job.Stage = JobStage.Synthesizing;
        await store.SaveAsync(job);
        await using var queue = new QueueCoordinator(test.Workspace, store, new FakeProvider(), new(new()), new(test.Workspace, store)) { Paused = true };
        await queue.InitializeAsync(); Assert.Equal(JobStage.Queued, Assert.Single(queue.Snapshot()).Stage);
    }
    [Fact] public async Task PipelineProducesSingleDecodableMp3AndExportOnlyRetrySkipsSynthesis()
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace); var provider = new FakeProvider();
        await using var queue = new QueueCoordinator(test.Workspace, store, provider, new(new()), new(test.Workspace, store));
        await queue.InitializeAsync(); var job = MakeJob(test); job.Destination = Path.Combine(test.Parent, "missing");
        await queue.AddAsync(job); await WaitForAsync(() => queue.Snapshot().Any(j => j.Stage == JobStage.Failed));
        var before = Assert.Single(queue.Snapshot()); Assert.NotEmpty(before.FinalHash); Assert.True(File.Exists(test.Workspace.FinalPath(job)));
        var calls = provider.Calls;
        await queue.RetryAsync(job.Id, test.Destination); await WaitForAsync(() => queue.Snapshot().Any(j => j.Stage == JobStage.Exported));
        Assert.Equal(calls, provider.Calls); Assert.Single(Directory.GetFiles(test.Destination, "*.mp3"));
    }
    [Fact] public async Task CorruptCachedChunkIsRegeneratedOnRetry()
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace); var provider = new FakeProvider();
        await using var queue = new QueueCoordinator(test.Workspace, store, provider, new(new()), new(test.Workspace, store));
        await queue.InitializeAsync(); var job = MakeJob(test); job.Destination = Path.Combine(test.Parent, "missing");
        await queue.AddAsync(job); await WaitForAsync(() => queue.Snapshot().Any(j => j.Stage == JobStage.Failed));
        var calls = provider.Calls; await File.WriteAllTextAsync(test.Workspace.ChunkPath(job, 0), "corrupt");
        await queue.RetryAsync(job.Id, test.Destination); await WaitForAsync(() => queue.Snapshot().Any(j => j.Stage == JobStage.Exported));
        Assert.Equal(calls + 1, provider.Calls);
    }
    private static Job MakeJob(TestWorkspace test) => new() { Title = "Pipeline test", Source = "A short test narration for a complete audio pipeline.", Prepared = TextPreparation.Prepare("A short test narration for a complete audio pipeline."), Settings = new("kokoro", "af_heart", 1, false, "", "fake"), Destination = test.Destination };
    private static async Task<Job> GeneratedJobAsync(TestWorkspace test)
    {
        var job = MakeJob(test); Directory.CreateDirectory(test.Workspace.JobDirectory(job.Id));
        await File.WriteAllBytesAsync(test.Workspace.FinalPath(job), [1, 2, 3, 4, 5]);
        job.FinalHash = await Workspace.HashFileAsync(test.Workspace.FinalPath(job)); return job;
    }
    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        while (!condition()) await Task.Delay(100, deadline.Token);
    }
    private sealed class FakeProvider(string fingerprint = "fake") : ISpeechProvider
    {
        public int Calls { get; private set; }
        public Task<ProviderInfo> ReadyAsync(string engine, CancellationToken ct) => Task.FromResult(new ProviderInfo(engine, fingerprint, ["af_heart"], "ready", 0));
        public Task SynthesizeAsync(NarrationSettings settings, string text, string output, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Calls++; TestWorkspace.WriteWave(output, 2); return Task.CompletedTask; }
    }
}
