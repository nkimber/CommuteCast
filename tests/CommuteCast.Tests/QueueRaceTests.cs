using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public class QueueRaceTests
{
    [Theory] [InlineData(JobStage.Queued)] [InlineData(JobStage.Synthesizing)] [InlineData(JobStage.Exporting)]
    public async Task InterruptedDurableCancellationDoesNotResumeOnRelaunch(JobStage stage)
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace); var provider = new GatedProvider(false) { Enabled = false }; var job = MakeJob(test);
        job.Stage = stage; job.CancellationRequested = true; await store.SaveAsync(job);
        await using var reopened = new QueueCoordinator(test.Workspace, store, provider, new(new()), new(test.Workspace, store));
        await reopened.InitializeAsync(); await Task.Delay(450);
        var restored = Assert.Single(await store.LoadAsync()); Assert.Equal(JobStage.Cancelled, restored.Stage); Assert.True(restored.CancellationRequested); Assert.Equal(0, provider.Syntheses);
        await reopened.RetryAsync(job.Id); await WaitUntilAsync(() => reopened.Snapshot().Single().Stage == JobStage.Exported);
        Assert.False(Assert.Single(await store.LoadAsync()).CancellationRequested);
    }
    [Fact] public async Task CancellationPersistenceFailurePausesDispatchAndReportsAnUnacknowledgedRequest()
    {
        using var test = new TestWorkspace(); var durable = new SqliteJobStore(test.Workspace); var store = new CancellationFailureStore(durable); var job = MakeJob(test);
        await using var queue = new QueueCoordinator(test.Workspace, store, new GatedProvider(false) { Enabled = false }, new(new()), new(test.Workspace, store)) { Paused = true };
        await queue.InitializeAsync(); await queue.AddAsync(job); store.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => queue.CancelAsync(job.Id));
        Assert.True(queue.Paused); Assert.NotEmpty(queue.PersistenceError); Assert.False(Assert.Single(await durable.LoadAsync()).CancellationRequested);
        Assert.True(Assert.Single(queue.Snapshot()).CancellationRequested); Assert.Empty(Directory.GetFiles(test.Destination));
        store.Fail = false; await queue.CancelAsync(job.Id);
        Assert.True(Assert.Single(await durable.LoadAsync()).CancellationRequested); Assert.Equal(JobStage.Cancelled, Assert.Single(await durable.LoadAsync()).Stage);
    }
    [Fact] public async Task VerifiedPublicationWinsOverInterruptedCancellationOnRelaunch()
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace); var job = MakeJob(test); var provider = new GatedProvider(false) { Enabled = false };
        job.ExportName = ExportPublisher.Filename(job); job.ExportHash = Job.Hash("A completed exported file"); job.Stage = JobStage.Exporting; job.CancellationRequested = true;
        await File.WriteAllTextAsync(Path.Combine(test.Destination, job.ExportName), "A completed exported file"); await store.SaveAsync(job);
        await using var queue = new QueueCoordinator(test.Workspace, store, provider, new(new()), new(test.Workspace, store)); await queue.InitializeAsync();
        var restored = Assert.Single(queue.Snapshot()); Assert.True(restored.ExportCommitted); Assert.False(restored.CancellationRequested); Assert.Equal(JobStage.Exported, restored.Stage); Assert.Equal(0, provider.Syntheses);
    }
    [Fact] public async Task ExplicitDestinationRetryCleansKnownOldStagingAndClearsCancellation()
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace); var job = MakeJob(test);
        job.Stage = JobStage.Failed; job.FinalHash = Job.Hash("Final fixture"); job.ExportHash = job.FinalHash; job.ExportName = ExportPublisher.Filename(job); job.ExportStagingOwned = true;
        var oldPartial = Path.Combine(test.Destination, $".commutecast-{job.Id}.partial"); await File.WriteAllTextAsync(oldPartial, "Interrupted known staging"); await store.SaveAsync(job);
        var destination = Path.Combine(test.Parent, "replacement"); Directory.CreateDirectory(destination);
        await using var queue = new QueueCoordinator(test.Workspace, store, new GatedProvider(false) { Enabled = false }, new(new()), new(test.Workspace, store)) { Paused = true };
        await queue.InitializeAsync(); await queue.RetryAsync(job.Id, destination);
        Assert.False(File.Exists(oldPartial)); var queued = Assert.Single(await store.LoadAsync()); Assert.Equal(destination, queued.Destination);
        Assert.False(queued.ExportStagingOwned); Assert.False(queued.CancellationRequested); Assert.Equal("", queued.ExportName); Assert.Equal(job.Source, queued.Source); Assert.Equal(job.Settings, queued.Settings);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CancellationDuringReadinessOrSecondChunkSettlesBeforeRetry(bool secondChunk)
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace); var provider = new GatedProvider(secondChunk); var job = MakeJob(test);
        await using var queue = new QueueCoordinator(test.Workspace, store, provider, new(new()), new(test.Workspace, store));
        await queue.InitializeAsync(); await queue.AddAsync(job); await provider.Gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await queue.CancelAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(10));
        var cancelled = Assert.Single(queue.Snapshot()); Assert.Equal(JobStage.Cancelled, cancelled.Stage); Assert.False(cancelled.ExportCommitted);
        Assert.Equal(secondChunk ? 1 : 0, cancelled.Receipts.Count); Assert.Empty(Directory.GetFiles(test.Destination, "*.mp3"));
        var calls = provider.Syntheses; await Task.Delay(450); Assert.Equal(calls, provider.Syntheses);
        provider.Enabled = false; await queue.RetryAsync(job.Id); await WaitUntilAsync(() => queue.Snapshot().Single().Stage == JobStage.Exported);
        Assert.Equal(cancelled.Chunks.Count - cancelled.Receipts.Count, provider.Syntheses - calls); Assert.Single(Directory.GetFiles(test.Destination, "*.mp3"));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task DeleteDuringReadinessOrSecondChunkCannotResurrectTheNarration(bool secondChunk)
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace); var provider = new GatedProvider(secondChunk); var job = MakeJob(test);
        await using (var queue = new QueueCoordinator(test.Workspace, store, provider, new(new()), new(test.Workspace, store)))
        {
            await queue.InitializeAsync(); await queue.AddAsync(job); await provider.Gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await queue.DeleteAsync(job.Id, true).WaitAsync(TimeSpan.FromSeconds(10)); Assert.Empty(queue.Snapshot()); Assert.Empty(await store.LoadAsync());
            Assert.False(Directory.Exists(test.Workspace.JobDirectory(job.Id))); Assert.Empty(Directory.GetFiles(test.Destination));
        }
        var calls = provider.Syntheses; await using var reopened = new QueueCoordinator(test.Workspace, store, provider, new(new()), new(test.Workspace, store));
        await reopened.InitializeAsync(); await Task.Delay(450); Assert.Empty(reopened.Snapshot()); Assert.Equal(calls, provider.Syntheses);
    }
    [Theory]
    [InlineData(ExportCheckpoint.CopyProgress)] [InlineData(ExportCheckpoint.BeforeRename)] [InlineData(ExportCheckpoint.Renamed)]
    public async Task QueueCancellationHonorsPublicationCommitPoint(ExportCheckpoint checkpoint)
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace); var observer = new GatedExport(checkpoint); var job = MakeJob(test);
        await using var queue = new QueueCoordinator(test.Workspace, store, new GatedProvider(false) { Enabled = false }, new(new()), new(test.Workspace, store, observer));
        await queue.InitializeAsync(); await queue.AddAsync(job); await observer.Gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var cancellation = queue.CancelAsync(job.Id);
        if (checkpoint == ExportCheckpoint.Renamed) { Assert.False(cancellation.IsCompleted); observer.Gate.Release.TrySetResult(); }
        await cancellation.WaitAsync(TimeSpan.FromSeconds(10));
        var saved = Assert.Single(await store.LoadAsync()); Assert.Equal(checkpoint == ExportCheckpoint.Renamed ? JobStage.Exported : JobStage.Cancelled, saved.Stage);
        Assert.Equal(checkpoint == ExportCheckpoint.Renamed, saved.ExportCommitted); Assert.Equal(checkpoint == ExportCheckpoint.Renamed ? 1 : 0, Directory.GetFiles(test.Destination, "*.mp3").Length);
    }
    [Theory]
    [InlineData(ExportCheckpoint.CopyProgress, true)] [InlineData(ExportCheckpoint.BeforeRename, true)]
    [InlineData(ExportCheckpoint.Renamed, true)] [InlineData(ExportCheckpoint.Renamed, false)]
    public async Task DeleteFencesPublicationAndHonorsExportRemovalScope(ExportCheckpoint checkpoint, bool removeExport)
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace); var observer = new GatedExport(checkpoint); var job = MakeJob(test);
        var unrelated = Path.Combine(test.Destination, "unrelated.mp3"); await File.WriteAllTextAsync(unrelated, "Keep this file");
        await using var queue = new QueueCoordinator(test.Workspace, store, new GatedProvider(false) { Enabled = false }, new(new()), new(test.Workspace, store, observer));
        await queue.InitializeAsync(); await queue.AddAsync(job); await observer.Gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var deletion = queue.DeleteAsync(job.Id, removeExport);
        if (checkpoint == ExportCheckpoint.Renamed) { Assert.False(deletion.IsCompleted); observer.Gate.Release.TrySetResult(); }
        await deletion.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Empty(queue.Snapshot()); Assert.Empty(await store.LoadAsync());
        Assert.False(Directory.Exists(test.Workspace.JobDirectory(job.Id))); Assert.Equal("Keep this file", await File.ReadAllTextAsync(unrelated));
        Assert.Equal(checkpoint == ExportCheckpoint.Renamed && !removeExport ? 2 : 1, Directory.GetFiles(test.Destination, "*.mp3").Length);
        Assert.Empty(Directory.GetFiles(test.Destination, "*.partial"));
    }
    private static Job MakeJob(TestWorkspace test)
    {
        var text = string.Join("\n\n", Enumerable.Range(0, 3).Select(i => $"Marker {i}. " + new string('x', 410) + "."));
        return new() { Title = "Queue race fixture", Source = text, Prepared = TextPreparation.Prepare(text), Settings = new("kokoro", "af_heart", 1, false, "", "fixture"), Destination = test.Destination };
    }
    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25)); while (!predicate()) await Task.Delay(50, deadline.Token);
    }
    private sealed class Gate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task WaitAsync(CancellationToken ct) { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); }
    }
    private sealed class GatedExport(ExportCheckpoint checkpoint) : IExportObserver
    {
        public Gate Gate { get; } = new();
        public Task ReachedAsync(ExportCheckpoint stage, CancellationToken ct) => stage == checkpoint ? Gate.WaitAsync(ct) : Task.CompletedTask;
    }
    private sealed class GatedProvider(bool secondChunk) : ISpeechProvider
    {
        public Gate Gate { get; } = new(); public bool Enabled { get; set; } = true; public int Syntheses { get; private set; }
        public async Task<ProviderInfo> ReadyAsync(string engine, CancellationToken ct)
        {
            if (Enabled && !secondChunk) await Gate.WaitAsync(ct);
            return new(engine, "fixture", ["af_heart"], "ready", 0);
        }
        public async Task SynthesizeAsync(NarrationSettings settings, string text, string output, CancellationToken ct)
        {
            if (Enabled && secondChunk && Syntheses == 1) await Gate.WaitAsync(ct);
            ct.ThrowIfCancellationRequested(); Syntheses++; TestWorkspace.WriteWave(output, 2);
        }
    }
    private sealed class CancellationFailureStore(IJobStore inner) : IJobStore
    {
        public bool Fail { get; set; }
        public Task SaveAsync(Job job, CancellationToken ct = default) => Fail && job.CancellationRequested ? throw new IOException("Injected cancellation persistence failure") : inner.SaveAsync(job, ct);
        public Task<IReadOnlyList<Job>> LoadAsync(CancellationToken ct = default) => inner.LoadAsync(ct);
        public Task SaveQueueOrderAsync(IReadOnlyDictionary<string, long> positions, CancellationToken ct = default) => inner.SaveQueueOrderAsync(positions, ct);
        public Task RemoveAsync(string id, CancellationToken ct = default) => inner.RemoveAsync(id, ct);
    }
}
