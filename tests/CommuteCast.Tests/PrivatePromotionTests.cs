using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public class PrivatePromotionTests
{
    [Fact] public async Task DownloadPromotionPreservesIdenticalReplacementOfPendingSource()
    {
        using var test = new TestWorkspace(); var job = new Job(); var store = new SqliteJobStore(test.Workspace);
        var directory = test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        const string source = "inference.partial.wav.attempt-original.partial";
        await using (var held = ExportStagingFile.Create(directory, source))
        {
            await held.Stream.WriteAsync(new byte[] { 1, 2, 3 });
            job.PrivateArtifacts.Add(new(source, "", CreationIdentity: held.Identity)); await store.SaveAsync(job);
            await Assert.ThrowsAsync<IOException>(() => PrivateJobFiles.CompleteAndMoveCreatedAsync(job, directory, source, "inference.partial.wav", held, async () =>
            {
                await store.SaveAsync(job); throw new IOException("Simulate loss after durable rename intent");
            }, default));
        }
        File.Move(Path.Combine(directory, source), Path.Combine(directory, "preserved-original.partial"));
        await File.WriteAllBytesAsync(Path.Combine(directory, source), new byte[] { 1, 2, 3 });
        var reopened = Assert.Single(await store.LoadAsync()); Assert.Equal(2, reopened.PrivateArtifacts.Count);
        await Assert.ThrowsAsync<IOException>(() => PrivateJobFiles.ReconcilePromotionsAsync(reopened, directory, () => store.SaveAsync(reopened), default));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(directory, source)));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(directory, "preserved-original.partial")));
    }

    [Fact] public async Task DownloadPromotionRefusesHeldHandleAtDifferentNameBeforeCheckpoint()
    {
        using var test = new TestWorkspace(); var job = new Job(); var directory = test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        await using var held = ExportStagingFile.Create(directory, "actual.partial");
        job.PrivateArtifacts.Add(new("claimed.partial", "", CreationIdentity: held.Identity));
        await Assert.ThrowsAsync<IOException>(() => PrivateJobFiles.CompleteAndMoveCreatedAsync(job, directory, "claimed.partial", "target.wav", held,
            () => throw new InvalidOperationException("Must not checkpoint"), default));
        Assert.True(File.Exists(Path.Combine(directory, "actual.partial"))); Assert.False(File.Exists(Path.Combine(directory, "target.wav")));
    }

    [Theory]
    [InlineData("normalized.partial.wav", "chunk-00000.wav", 1)]
    [InlineData("normalized.partial.wav", "chunk-00000.wav", 2)]
    [InlineData("encoded.partial.mp3", "complete.mp3", 1)]
    [InlineData("encoded.partial.mp3", "complete.mp3", 2)]
    public async Task FailedCheckpointLeavesDurableOwnershipOnEitherSideOfRename(string source, string destination, int failure)
    {
        using var test = new TestWorkspace(); var job = new Job(); var store = new SqliteJobStore(test.Workspace);
        var directory = test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, source), "Completed verified audio");
        await PrivateJobFiles.RecordAsync(job, directory, source, default); await store.SaveAsync(job);
        var calls = 0;
        await Assert.ThrowsAsync<IOException>(() => PrivateJobFiles.MoveRecordedAsync(job, directory, source, destination, async () =>
        {
            if (++calls == failure) throw new IOException("Injected checkpoint failure");
            await store.SaveAsync(job);
        }, default));
        var reopened = Assert.Single(await store.LoadAsync());
        Assert.True(File.Exists(Path.Combine(directory, failure == 1 ? source : destination)));
        Assert.False(File.Exists(Path.Combine(directory, failure == 1 ? destination : source)));
        Assert.Equal(Job.Hash("Completed verified audio"), PrivateJobFiles.Inventory(reopened)[failure == 1 ? source : destination]);
        // The last committed state removes precisely the original bytes after either interruption.
        await PrivateJobFiles.RemoveAsync(reopened, directory); Assert.False(Directory.Exists(directory));
    }

    [Fact] public async Task CheckpointCannotReplaceTheHeldSourceAndCancellationLeavesSourceRecoverable()
    {
        using var test = new TestWorkspace(); var job = new Job(); var store = new SqliteJobStore(test.Workspace);
        var directory = test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "normalized.partial.wav"); await File.WriteAllTextAsync(path, "Owned source");
        await PrivateJobFiles.RecordAsync(job, directory, "normalized.partial.wav", default);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PrivateJobFiles.MoveRecordedAsync(job, directory, "normalized.partial.wav", "chunk-00000.wav", async () =>
        {
            Assert.Throws<IOException>(() => File.WriteAllText(path, "Replacement"));
            await store.SaveAsync(job); cancellation.Cancel();
        }, cancellation.Token));
        Assert.Equal("Owned source", await File.ReadAllTextAsync(path)); Assert.False(File.Exists(Path.Combine(directory, "chunk-00000.wav")));
        await PrivateJobFiles.RemoveAsync(Assert.Single(await store.LoadAsync()), directory);
        Assert.False(Directory.Exists(directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownDestinationAndChangedSourceArePreservedWithoutCheckpoint(bool changedSource)
    {
        using var test = new TestWorkspace(); var job = new Job(); var directory = test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "encoded.partial.mp3"); var target = Path.Combine(directory, "complete.mp3");
        await File.WriteAllTextAsync(source, "Owned source"); await PrivateJobFiles.RecordAsync(job, directory, "encoded.partial.mp3", default);
        if (changedSource) await File.WriteAllTextAsync(source, "Changed source");
        else await File.WriteAllTextAsync(target, "Unknown output");
        await Assert.ThrowsAsync<IOException>(() => PrivateJobFiles.MoveRecordedAsync(job, directory, "encoded.partial.mp3", "complete.mp3", () => throw new InvalidOperationException("Must not checkpoint"), default));
        Assert.Equal(changedSource ? "Changed source" : "Owned source", await File.ReadAllTextAsync(source));
        if (!changedSource) Assert.Equal("Unknown output", await File.ReadAllTextAsync(target));
        Assert.DoesNotContain(job.PrivateArtifacts, r => r.RelativePath == "complete.mp3");
    }

    [Fact] public async Task IdenticalUnknownFileRacingTheRenameIsNeverAdoptedOrRemoved()
    {
        using var test = new TestWorkspace(); var job = new Job(); var store = new SqliteJobStore(test.Workspace);
        var directory = test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "encoded.partial.mp3"); var target = Path.Combine(directory, "complete.mp3");
        await File.WriteAllTextAsync(source, "Same bytes"); await PrivateJobFiles.RecordAsync(job, directory, "encoded.partial.mp3", default);
        await Assert.ThrowsAsync<IOException>(() => PrivateJobFiles.MoveRecordedAsync(job, directory, "encoded.partial.mp3", "complete.mp3", async () =>
        {
            await store.SaveAsync(job); await File.WriteAllTextAsync(target, "Same bytes");
        }, default));
        var reopened = Assert.Single(await store.LoadAsync());
        await Assert.ThrowsAsync<IOException>(() => PrivateJobFiles.ReconcilePromotionsAsync(reopened, directory, () => store.SaveAsync(reopened), default));
        await Assert.ThrowsAsync<IOException>(() => PrivateJobFiles.PrepareOutputAsync(reopened, directory, "complete.mp3", default));
        await Assert.ThrowsAsync<IOException>(() => PrivateJobFiles.RemoveAsync(reopened, directory));
        Assert.Equal("Same bytes", await File.ReadAllTextAsync(target));
        Assert.Equal(0, (await new CacheMaintenance(test.Workspace).CleanAsync([reopened], null, 1, 7, default)).FilesRemoved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupReconcilesBeforeAndAfterRenameAndThenBackupRestoresCompletedOwnership(bool moved)
    {
        using var test = new TestWorkspace(); var job = new Job { Stage = JobStage.Failed }; var store = new SqliteJobStore(test.Workspace);
        var directory = test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "encoded.partial.mp3"); await File.WriteAllTextAsync(source, "Known complete bytes");
        await PrivateJobFiles.RecordAsync(job, directory, "encoded.partial.mp3", default); await store.SaveAsync(job);
        var calls = 0;
        await Assert.ThrowsAsync<IOException>(() => PrivateJobFiles.MoveRecordedAsync(job, directory, "encoded.partial.mp3", "complete.mp3", async () =>
        {
            if (++calls == 1) await store.SaveAsync(job);
            if (calls == (moved ? 2 : 1)) throw new IOException("Crash boundary");
        }, default));
        using var lease = WorkspaceLease.Acquire(test.Workspace);
        await Assert.ThrowsAsync<IOException>(() => WorkspaceBackup.CreateAsync(lease));
        await using (var queue = new QueueCoordinator(test.Workspace, store, new NeverProvider(), new(new()), new(test.Workspace, store)) { Paused = true }) await queue.InitializeAsync();
        var settled = Assert.Single(await store.LoadAsync()); Assert.DoesNotContain(settled.PrivateArtifacts, r => r.PromotionIdentity is not null);
        var backup = await WorkspaceBackup.CreateAsync(lease); await WorkspaceBackup.RestoreAsync(lease, backup);
        await PrivateJobFiles.RemoveAsync(Assert.Single(await store.LoadAsync()), directory); Assert.False(Directory.Exists(directory));
    }
    private sealed class NeverProvider : ISpeechProvider
    {
        public Task<ProviderInfo> ReadyAsync(string engine, CancellationToken ct) => throw new InvalidOperationException();
        public Task SynthesizeAsync(NarrationSettings settings, string text, string output, CancellationToken ct) => throw new InvalidOperationException();
    }

    [Fact] public async Task QueueRetryCannotReuseAnIdenticalReplacementAsACompletedChunk()
    {
        using var test = new TestWorkspace(); var text = "Keep this frozen narration.";
        var job = new Job { Source = text, Prepared = TextPreparation.Prepare(text), Stage = JobStage.Failed, Destination = test.Destination,
            Settings = new("kokoro", "af_heart", 1, false, "", "fixture") };
        job.Chunks = Chunker.Split(job.Prepared.Script, 450);
        var directory = test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "normalized.partial.wav"); var target = test.Workspace.ChunkPath(job, 0);
        TestWorkspace.WriteWave(source, 1); await PrivateJobFiles.RecordAsync(job, directory, "normalized.partial.wav", default);
        var store = new SqliteJobStore(test.Workspace);
        await Assert.ThrowsAsync<IOException>(() => PrivateJobFiles.MoveRecordedAsync(job, directory, "normalized.partial.wav", "chunk-00000.wav", async () =>
        {
            job.Receipts.Add(new(0, job.PrivateArtifacts.Single(r => r.RelativePath == "normalized.partial.wav").Hash, job.Fingerprint, 1));
            await store.SaveAsync(job); TestWorkspace.WriteWave(target, 1);
        }, default));
        var targetHash = await Workspace.HashFileAsync(target);
        await using var queue = new QueueCoordinator(test.Workspace, store, new NeverProvider(), new(new()), new(test.Workspace, store));
        await queue.InitializeAsync(); Assert.Equal(JobStage.Failed, Assert.Single(queue.Snapshot()).Stage);
        await queue.RetryAsync(job.Id);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (queue.Snapshot().Single().Stage != JobStage.Failed) await Task.Delay(25, deadline.Token);
        Assert.Contains("replaced or changed", queue.Snapshot().Single().Error);
        Assert.Equal(targetHash, await Workspace.HashFileAsync(target)); Assert.Empty(Directory.GetFiles(test.Destination));
    }
}
