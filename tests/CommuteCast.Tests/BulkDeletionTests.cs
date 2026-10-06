using CommuteCast.Core;
using CommuteCast.Infrastructure;
using Microsoft.Data.Sqlite;

namespace CommuteCast.Tests;

public class BulkDeletionTests
{
    [Fact] public async Task SelectedActiveWorkSettlesBeforeAnyItemRemovalAndSerializesRetrySubmissionAndCleanup()
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace); var provider = new SettlingProvider();
        await using var queue = Queue(test, store, provider); queue.Paused = true; await queue.InitializeAsync();
        var old = MakeJob(test, JobStage.Failed); var active = MakeJob(test); var pending = MakeJob(test); var unselected = MakeJob(test);
        await queue.AddAsync(old); await queue.AddAsync(active); await queue.AddAsync(pending); await queue.AddAsync(unselected);
        var protectedOld = await SentinelAsync(test, old); queue.Paused = false;
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); queue.Paused = true;
        var deletion = queue.DeleteManyAsync([old.Id, active.Id, pending.Id], true);
        await provider.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cleanup = queue.CleanCacheAsync(); var retry = queue.RetryAsync(old.Id); var newcomer = MakeJob(test); var submission = queue.AddAsync(newcomer);
        Assert.False(deletion.IsCompleted); Assert.False(cleanup.IsCompleted); Assert.False(retry.IsCompleted); Assert.False(submission.IsCompleted);
        Assert.True(File.Exists(protectedOld)); Assert.All(await store.LoadAsync(), j => Assert.False(j.DeletionRequested));
        provider.Settled.TrySetResult(); var result = await deletion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3, result.Removed); Assert.Equal(0, result.Failed); Assert.False(File.Exists(protectedOld));
        await Assert.ThrowsAsync<ArgumentException>(() => retry); await submission; Assert.Equal(0, (await cleanup).FilesRemoved);
        Assert.Equal(new[] { unselected.Id, newcomer.Id }.Order(), queue.Snapshot().Select(j => j.Id).Order());
        Assert.All(await store.LoadAsync(), j => Assert.False(j.DeletionRequested)); Assert.Equal(1, provider.ReadinessCalls);
    }

    [Theory]
    [InlineData(ExportCheckpoint.CopyProgress, false)] [InlineData(ExportCheckpoint.CopyProgress, true)]
    [InlineData(ExportCheckpoint.BeforeRename, false)] [InlineData(ExportCheckpoint.BeforeRename, true)]
    [InlineData(ExportCheckpoint.Renamed, false)] [InlineData(ExportCheckpoint.Renamed, true)]
    public async Task BulkRemovalFencesAnActiveLastItemAtPublicationAndHonorsExportScope(ExportCheckpoint checkpoint, bool deleteExports)
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace); var observer = new ExportGate(checkpoint);
        await using var queue = new QueueCoordinator(test.Workspace, store, new ImmediateProvider(), new(new()), new(test.Workspace, store, observer)) { Paused = true };
        await queue.InitializeAsync(); var old = MakeJob(test, JobStage.Failed); var active = MakeJob(test); var pending = MakeJob(test);
        await queue.AddAsync(old); await queue.AddAsync(pending); await queue.AddAsync(active);
        var oldFile = await SentinelAsync(test, old); var unrelated = Path.Combine(test.Destination, "unrelated.mp3"); await File.WriteAllTextAsync(unrelated, "Keep unrelated export");
        // Only the selected active item may dispatch before the review is applied.
        await queue.CancelAsync(pending.Id); queue.Paused = false;
        await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var removal = queue.DeleteManyAsync([old.Id, pending.Id, active.Id], deleteExports);
        if (checkpoint == ExportCheckpoint.Renamed)
        { Assert.False(removal.IsCompleted); Assert.True(File.Exists(oldFile)); observer.Release.TrySetResult(); }
        var result = await removal.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(3, result.Removed);
        Assert.Empty(queue.Snapshot()); Assert.Empty(await store.LoadAsync()); Assert.Equal("Keep unrelated export", await File.ReadAllTextAsync(unrelated));
        Assert.Equal(checkpoint == ExportCheckpoint.Renamed && !deleteExports ? 2 : 1, Directory.GetFiles(test.Destination, "*.mp3").Length);
        Assert.Empty(Directory.GetFiles(test.Destination, "*.partial"));
    }

    [Fact] public async Task ActualSqliteFailureRollsBackEveryIntentBeforeFileRemovalAndPreventsAccidentalResume()
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace); var provider = new ImmediateProvider();
        await using var queue = Queue(test, store, provider); queue.Paused = true; await queue.InitializeAsync();
        var first = MakeJob(test); var second = MakeJob(test); await queue.AddAsync(first); await queue.AddAsync(second);
        var firstFile = await SentinelAsync(test, first); var secondFile = await SentinelAsync(test, second);
        await SqlAsync(test, $"CREATE TRIGGER reject_second_deletion BEFORE UPDATE ON jobs WHEN NEW.id='{second.Id}' BEGIN SELECT RAISE(ABORT,'Injected second intent failure'); END;");
        await Assert.ThrowsAsync<IOException>(() => queue.DeleteManyAsync([first.Id, second.Id], false));
        Assert.True(queue.Paused); Assert.NotEmpty(queue.PersistenceError); Assert.True(File.Exists(firstFile)); Assert.True(File.Exists(secondFile));
        Assert.All(await store.LoadAsync(), j => Assert.False(j.DeletionRequested)); Assert.All(queue.Snapshot(), j => Assert.False(j.DeletionRequested));
        queue.Paused = false; await Task.Delay(500); Assert.Equal(0, provider.ReadinessCalls);
        await SqlAsync(test, "DROP TRIGGER reject_second_deletion;");
        var repaired = await queue.DeleteManyAsync([first.Id, second.Id], false); Assert.Equal(2, repaired.Removed); Assert.Equal("", queue.PersistenceError);
        Assert.Empty(await store.LoadAsync());
    }

    [Fact] public async Task EveryIntentIsDurableBeforeFirstRemovalAndLostAcknowledgementRecoversAllItems()
    {
        using var test = new TestWorkspace(); var durable = new SqliteJobStore(test.Workspace); var store = new ObservedStore(durable) { LoseCommitAcknowledgement = true };
        var ids = new List<string>();
        await using (var queue = Queue(test, store, new ImmediateProvider()))
        {
            queue.Paused = true; await queue.InitializeAsync();
            foreach (var _ in Enumerable.Range(0, 3)) { var job = MakeJob(test); ids.Add(job.Id); await queue.AddAsync(job); await SentinelAsync(test, job); }
            await Assert.ThrowsAsync<IOException>(() => queue.DeleteManyAsync(ids, true));
            Assert.True(queue.Paused); Assert.All(await durable.LoadAsync(), j => { Assert.True(j.DeletionRequested); Assert.True(j.DeleteExportRequested); });
            Assert.All(ids, id => Assert.True(Directory.Exists(test.Workspace.JobDirectory(id)))); Assert.Equal(0, store.Removals);
        }
        await using var reopened = Queue(test, durable, new ImmediateProvider()); reopened.Paused = true; await reopened.InitializeAsync();
        Assert.Empty(await durable.LoadAsync()); Assert.Empty(reopened.Snapshot()); Assert.All(ids, id => Assert.False(Directory.Exists(test.Workspace.JobDirectory(id))));
    }

    [Fact] public async Task LockedItemReportsFailureWhileOthersFinishAndRelaunchSettlesItsRecordedScope()
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace); string lockedId; string otherId;
        var unrelated = Path.Combine(test.Workspace.Root, "unrelated.txt"); await File.WriteAllTextAsync(unrelated, "Keep private unrelated file");
        await using (var queue = Queue(test, store, new ImmediateProvider()))
        {
            queue.Paused = true; await queue.InitializeAsync(); var locked = MakeJob(test); var other = MakeJob(test); lockedId = locked.Id; otherId = other.Id;
            await queue.AddAsync(locked); await queue.AddAsync(other); var path = await SentinelAsync(test, locked); await SentinelAsync(test, other);
            using var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var result = await queue.DeleteManyAsync([locked.Id, other.Id], false);
            Assert.Equal(1, result.Removed); Assert.Equal(1, result.Failed); Assert.Equal(locked.Id, result.Items.Single(i => !i.Removed).Id);
            var remaining = Assert.Single(await store.LoadAsync()); Assert.True(remaining.DeletionRequested); Assert.Equal(JobStage.Deleting, remaining.Stage);
            Assert.False(remaining.DeleteExportRequested); Assert.NotEmpty(remaining.Error); Assert.True(File.Exists(path)); Assert.False(Directory.Exists(test.Workspace.JobDirectory(other.Id)));
            Assert.Equal(0, (await queue.CleanCacheAsync()).FilesRemoved);
        }
        await using var reopened = Queue(test, store, new ImmediateProvider()); reopened.Paused = true; await reopened.InitializeAsync();
        Assert.Empty(await store.LoadAsync()); Assert.False(Directory.Exists(test.Workspace.JobDirectory(lockedId))); Assert.False(Directory.Exists(test.Workspace.JobDirectory(otherId)));
        Assert.Equal("Keep private unrelated file", await File.ReadAllTextAsync(unrelated));
    }

    [Fact] public async Task SameProcessRetryUsesRecordedExportConsentAfterLostCommitAcknowledgement()
    {
        using var test = new TestWorkspace(); var durable = new SqliteJobStore(test.Workspace); var store = new ObservedStore(durable) { LoseCommitAcknowledgement = true };
        await using var queue = Queue(test, store, new ImmediateProvider()); queue.Paused = true; await queue.InitializeAsync();
        var job = MakeJob(test, JobStage.Exported); job.ExportCommitted = true; job.ExportName = ExportPublisher.Filename(job); job.ExportHash = Job.Hash("Owned completed export");
        var exported = Path.Combine(test.Destination, job.ExportName); await File.WriteAllTextAsync(exported, "Owned completed export");
        await queue.AddAsync(job); await SentinelAsync(test, job);
        await Assert.ThrowsAsync<IOException>(() => queue.DeleteManyAsync([job.Id], true));
        Assert.False(Assert.Single(queue.Snapshot()).DeleteExportRequested); Assert.True(Assert.Single(await durable.LoadAsync()).DeleteExportRequested);
        store.LoseCommitAcknowledgement = false;
        var retried = await queue.DeleteManyAsync([job.Id], false);
        Assert.Equal(1, retried.Removed); Assert.False(File.Exists(exported)); Assert.Empty(await durable.LoadAsync());
    }

    [Fact] public async Task NativeReviewRereadsLostAcknowledgementScopeWithoutRemovingFiles()
    {
        using var test = new TestWorkspace(); var durable = new SqliteJobStore(test.Workspace); var store = new ObservedStore(durable) { LoseCommitAcknowledgement = true };
        await using var queue = Queue(test, store, new ImmediateProvider()); queue.Paused = true; await queue.InitializeAsync();
        var job = MakeJob(test); await queue.AddAsync(job); var path = await SentinelAsync(test, job);
        await Assert.ThrowsAsync<IOException>(() => queue.DeleteManyAsync([job.Id], true));
        var review = await queue.ReviewDeletionAsync([job.Id]); Assert.Equal(1, review.Items); Assert.Equal(1, review.PendingRequests); Assert.Equal(1, review.RecordedExportRemovals);
        var reviewed = Assert.Single(queue.Snapshot()); Assert.True(reviewed.DeletionRequested); Assert.True(reviewed.DeleteExportRequested);
        Assert.Equal(JobStage.Deleting, reviewed.Stage); Assert.True(File.Exists(path)); Assert.Equal(0, store.Removals);
    }

    [Fact] public async Task UnconfirmedEffectiveScopePausesBeforeRemovalAndKeepsDurableConsent()
    {
        using var test = new TestWorkspace(); var durable = new SqliteJobStore(test.Workspace); var store = new ObservedStore(durable) { ReturnIncorrectScope = true };
        await using var queue = Queue(test, store, new ImmediateProvider()); queue.Paused = true; await queue.InitializeAsync();
        var job = MakeJob(test); await queue.AddAsync(job); var path = await SentinelAsync(test, job);
        await Assert.ThrowsAsync<IOException>(() => queue.DeleteManyAsync([job.Id], true));
        Assert.True(File.Exists(path)); Assert.True(queue.Paused); Assert.NotEmpty(queue.PersistenceError);
        Assert.True(Assert.Single(await durable.LoadAsync()).DeleteExportRequested); Assert.Equal(0, store.Removals);
    }

    [Fact] public async Task AtomicIntentKeepsFrozenRecordsAndEarlierExportRemovalConsent()
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace); var first = MakeJob(test, JobStage.Exported); var second = MakeJob(test, JobStage.Failed);
        first.ExportCommitted = true; first.ExportName = ExportPublisher.Filename(first); first.ExportHash = Job.Hash("Already committed"); first.DeleteExportRequested = true;
        await store.SaveAsync(first); await store.SaveAsync(second); await store.RequestDeletionAsync([first.Id, second.Id], false);
        var saved = await store.LoadAsync(); var exported = saved.Single(j => j.Id == first.Id);
        Assert.True(exported.ExportCommitted); Assert.Equal(first.ExportHash, exported.ExportHash); Assert.True(exported.DeleteExportRequested);
        Assert.All(saved, j => { Assert.True(j.DeletionRequested); Assert.Equal(JobStage.Deleting, j.Stage); });
        Assert.Equal(first.Source, exported.Source); Assert.Equal(System.Text.Json.JsonSerializer.Serialize(first.Prepared), System.Text.Json.JsonSerializer.Serialize(exported.Prepared)); Assert.Equal(first.Settings, exported.Settings); Assert.Equal(first.CreatedUtc, exported.CreatedUtc);
        Assert.False(saved.Single(j => j.Id == second.Id).DeleteExportRequested);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ChangedOrDuplicateSelectionNeverPartiallyRemovesItems(bool duplicate)
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace);
        await using var queue = Queue(test, store, new ImmediateProvider()); queue.Paused = true; await queue.InitializeAsync();
        var job = MakeJob(test); await queue.AddAsync(job); var path = await SentinelAsync(test, job);
        await Assert.ThrowsAsync<ArgumentException>(() => queue.DeleteManyAsync([job.Id, duplicate ? job.Id : Guid.NewGuid().ToString("N")], true));
        Assert.True(File.Exists(path)); Assert.False(Assert.Single(await store.LoadAsync()).DeletionRequested);
        Assert.Empty((await queue.DeleteManyAsync([], true)).Items);
        await Assert.ThrowsAsync<IOException>(() => store.RequestDeletionAsync([job.Id, Guid.NewGuid().ToString("N")], true));
        Assert.False(Assert.Single(await store.LoadAsync()).DeletionRequested);
    }

    private static QueueCoordinator Queue(TestWorkspace test, IJobStore store, ISpeechProvider provider) => new(test.Workspace, store, provider, new(new()), new(test.Workspace, store));
    private static Job MakeJob(TestWorkspace test, JobStage stage = JobStage.Queued)
    {
        const string text = "Synthetic bulk deletion fixture with preserved source and frozen settings.";
        return new() { Title = "Bulk fixture", Source = text, Prepared = TextPreparation.Prepare(text), Destination = test.Destination, Stage = stage, Settings = new("kokoro", "af_heart", 1, false, "", "fixture") };
    }
    private static async Task<string> SentinelAsync(TestWorkspace test, Job job)
    { var directory = test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory); var path = Path.Combine(directory, "source.json"); await File.WriteAllTextAsync(path, "Selected source artifact"); return path; }
    private static async Task SqlAsync(TestWorkspace test, string sql)
    {
        await using var connection = new SqliteConnection(SqliteSchema.ConnectionString(Path.Combine(test.Workspace.Root, "queue.db"))); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
    }
    private class ImmediateProvider : ISpeechProvider
    {
        public int ReadinessCalls { get; protected set; }
        public virtual Task<ProviderInfo> ReadyAsync(string engine, CancellationToken ct) { ReadinessCalls++; return Task.FromResult(new ProviderInfo(engine, "fixture", ["af_heart"], "ready", 0)); }
        public Task SynthesizeAsync(NarrationSettings settings, string text, string output, CancellationToken ct) { ct.ThrowIfCancellationRequested(); TestWorkspace.WriteWave(output, 2); return Task.CompletedTask; }
    }
    private sealed class SettlingProvider : ImmediateProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<ProviderInfo> ReadyAsync(string engine, CancellationToken ct)
        {
            ReadinessCalls++; Entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); await Settled.Task.WaitAsync(TimeSpan.FromSeconds(10)); throw; }
            throw new InvalidOperationException("Expected cancellation.");
        }
    }
    private sealed class ExportGate(ExportCheckpoint point) : IExportObserver
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ReachedAsync(ExportCheckpoint checkpoint, CancellationToken ct)
        { if (checkpoint != point) return Task.CompletedTask; Entered.TrySetResult(); return Release.Task.WaitAsync(ct); }
    }
    private sealed class ObservedStore(IJobStore inner) : IJobStore
    {
        public bool LoseCommitAcknowledgement { get; set; }
        public bool ReturnIncorrectScope { get; set; }
        public int Removals { get; private set; }
        public Task SaveAsync(Job job, CancellationToken ct = default) => inner.SaveAsync(job, ct);
        public async Task<IReadOnlyDictionary<string, bool>> RequestDeletionAsync(IReadOnlyList<string> ids, bool deleteExports, CancellationToken ct = default)
        {
            var scopes = await inner.RequestDeletionAsync(ids, deleteExports, ct);
            if (LoseCommitAcknowledgement) throw new IOException("Lost commit acknowledgement after durable intents.");
            return ReturnIncorrectScope ? ids.ToDictionary(id => id, _ => false) : scopes;
        }
        public Task<IReadOnlyList<Job>> LoadAsync(CancellationToken ct = default) => inner.LoadAsync(ct);
        public Task SaveQueueOrderAsync(IReadOnlyDictionary<string, long> positions, CancellationToken ct = default) => inner.SaveQueueOrderAsync(positions, ct);
        public Task RemoveAsync(string id, CancellationToken ct = default) { Removals++; return inner.RemoveAsync(id, ct); }
    }
}
