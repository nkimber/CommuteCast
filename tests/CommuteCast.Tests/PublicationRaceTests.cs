using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public class PublicationRaceTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task DurableCancellationOrDeletionFencesRenameWithoutTokenCancellation(bool deletion)
    {
        using var test = new TestWorkspace(); var job = await GeneratedAsync(test); var store = new SqliteJobStore(test.Workspace);
        var observer = new Observer((stage, _) => { if (stage == ExportCheckpoint.BeforeRename) { if (deletion) job.DeletionRequested = true; else job.CancellationRequested = true; } return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ExportPublisher(test.Workspace, store, observer).PublishAsync(job, default));
        Assert.False(job.ExportCommitted); Assert.Empty(Directory.GetFiles(test.Destination));
    }
    [Theory]
    [InlineData(ExportCheckpoint.IntentSaved)] [InlineData(ExportCheckpoint.CopyStarted)]
    [InlineData(ExportCheckpoint.CopyProgress)] [InlineData(ExportCheckpoint.CopyFlushed)]
    [InlineData(ExportCheckpoint.CopyVerified)] [InlineData(ExportCheckpoint.BeforeRename)]
    public async Task CancellationBeforeRenameRetainsPrivateAudioAndCleansOwnedStaging(ExportCheckpoint checkpoint)
    {
        using var test = new TestWorkspace(); var job = await GeneratedAsync(test); using var cancel = new CancellationTokenSource();
        var store = new SqliteJobStore(test.Workspace);
        var observer = new Observer((stage, _) => { if (stage == checkpoint) cancel.Cancel(); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ExportPublisher(test.Workspace, store, observer).PublishAsync(job, cancel.Token));
        Assert.False(job.ExportCommitted); Assert.Empty(Directory.GetFiles(test.Destination));
        Assert.Equal(job.FinalHash, await Workspace.HashFileAsync(test.Workspace.FinalPath(job)));
        Assert.Equal(JobStage.Exporting, Assert.Single(await store.LoadAsync()).Stage);
        await new ExportPublisher(test.Workspace, store).PublishAsync(job, default);
        Assert.Single(Directory.GetFiles(test.Destination, "*.mp3")); Assert.True(job.ExportCommitted);
    }
    [Theory]
    [InlineData(ExportCheckpoint.IntentSaved)] [InlineData(ExportCheckpoint.CopyStarted)]
    [InlineData(ExportCheckpoint.CopyProgress)] [InlineData(ExportCheckpoint.CopyFlushed)]
    [InlineData(ExportCheckpoint.CopyVerified)] [InlineData(ExportCheckpoint.BeforeRename)]
    public async Task FaultBeforeRenameNeverPublishesAndCanRetryWithoutRegeneration(ExportCheckpoint checkpoint)
    {
        using var test = new TestWorkspace(); var job = await GeneratedAsync(test); var store = new SqliteJobStore(test.Workspace);
        var observer = new Observer((stage, _) => stage == checkpoint ? throw new IOException("Injected destination failure") : Task.CompletedTask);
        await Assert.ThrowsAsync<IOException>(() => new ExportPublisher(test.Workspace, store, observer).PublishAsync(job, default));
        Assert.False(job.ExportCommitted); Assert.Empty(Directory.GetFiles(test.Destination)); Assert.True(File.Exists(test.Workspace.FinalPath(job)));
        await new ExportPublisher(test.Workspace, store).PublishAsync(job, default); Assert.Single(Directory.GetFiles(test.Destination, "*.mp3"));
    }
    [Theory] [InlineData(ExportCheckpoint.Renamed)] [InlineData(ExportCheckpoint.Committed)]
    public async Task CancellationAfterRenameCannotUndoPublication(ExportCheckpoint checkpoint)
    {
        using var test = new TestWorkspace(); var job = await GeneratedAsync(test); using var cancel = new CancellationTokenSource(); var store = new SqliteJobStore(test.Workspace);
        var observer = new Observer((stage, token) => { if (stage == checkpoint) { Assert.False(token.CanBeCanceled); cancel.Cancel(); } return Task.CompletedTask; });
        await new ExportPublisher(test.Workspace, store, observer).PublishAsync(job, cancel.Token);
        Assert.True(job.ExportCommitted); Assert.True(Assert.Single(await store.LoadAsync()).ExportCommitted); Assert.Single(Directory.GetFiles(test.Destination, "*.mp3"));
    }
    [Theory] [InlineData(ExportCheckpoint.Renamed)] [InlineData(ExportCheckpoint.Committed)]
    public async Task FaultAfterRenameReconcilesOneExportFromDurableJournal(ExportCheckpoint checkpoint)
    {
        using var test = new TestWorkspace(); var job = await GeneratedAsync(test); var store = new SqliteJobStore(test.Workspace);
        var observer = new Observer((stage, _) => stage == checkpoint ? throw new IOException("Injected post-rename failure") : Task.CompletedTask);
        await Assert.ThrowsAsync<IOException>(() => new ExportPublisher(test.Workspace, store, observer).PublishAsync(job, default));
        Assert.True(job.ExportCommitted); Assert.Single(Directory.GetFiles(test.Destination, "*.mp3"));
        var restored = Assert.Single(await store.LoadAsync()); await new ExportPublisher(test.Workspace, store).ReconcileAsync(restored, default);
        Assert.True(restored.ExportCommitted); Assert.Equal(JobStage.Exported, restored.Stage);
        await new ExportPublisher(test.Workspace, store).PublishAsync(restored, default); Assert.Single(Directory.GetFiles(test.Destination, "*.mp3"));
    }
    [Fact] public async Task FailedCommittedCheckpointRecoversWithoutChangingAudio()
    {
        using var test = new TestWorkspace(); var job = await GeneratedAsync(test); var durable = new SqliteJobStore(test.Workspace); var store = new CommitFailureStore(durable);
        await Assert.ThrowsAsync<IOException>(() => new ExportPublisher(test.Workspace, store).PublishAsync(job, default));
        Assert.True(job.ExportCommitted); var restored = Assert.Single(await durable.LoadAsync()); Assert.False(restored.ExportCommitted);
        await new ExportPublisher(test.Workspace, durable).ReconcileAsync(restored, default);
        Assert.Equal(job.FinalHash, await Workspace.HashFileAsync(Assert.Single(Directory.GetFiles(test.Destination, "*.mp3")))); Assert.True(restored.ExportCommitted);
    }
    [Fact] public async Task FailedOwnershipCheckpointCleansCreatedFileAndDoesNotClaimACollision()
    {
        using var test = new TestWorkspace(); var job = await GeneratedAsync(test); var durable = new SqliteJobStore(test.Workspace);
        await Assert.ThrowsAsync<IOException>(() => new ExportPublisher(test.Workspace, new CommitFailureStore(durable, true)).PublishAsync(job, default));
        Assert.False(Assert.Single(await durable.LoadAsync()).ExportStagingOwned); Assert.False(job.ExportStagingOwned); Assert.Empty(Directory.GetFiles(test.Destination));
        await new ExportPublisher(test.Workspace, durable).PublishAsync(job, default); Assert.True(job.ExportCommitted);
    }
    [Fact] public async Task ChangedStagingCollisionIsNeverTruncatedOrDeleted()
    {
        using var test = new TestWorkspace(); var job = await GeneratedAsync(test); var path = Partial(test, job); await File.WriteAllTextAsync(path, "Unrelated staging collision");
        var publisher = new ExportPublisher(test.Workspace, new SqliteJobStore(test.Workspace));
        await Assert.ThrowsAsync<IOException>(() => publisher.PublishAsync(job, default)); Assert.Equal("Unrelated staging collision", await File.ReadAllTextAsync(path));
        await Assert.ThrowsAsync<IOException>(() => publisher.RemoveManagedExportAsync(job, default)); Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(test.Destination, "*.mp3"));
    }
    [Fact] public async Task FullyCopiedStagingAfterProcessLossPromotesWithoutOverwriting()
    {
        using var test = new TestWorkspace(); var job = await GeneratedAsync(test); File.Copy(test.Workspace.FinalPath(job), Partial(test, job));
        await new ExportPublisher(test.Workspace, new SqliteJobStore(test.Workspace)).PublishAsync(job, default);
        Assert.True(job.ExportCommitted); Assert.False(File.Exists(Partial(test, job))); Assert.Single(Directory.GetFiles(test.Destination, "*.mp3"));
    }
    [Fact] public async Task JournaledExclusiveStagingAfterInterruptedCopyIsRecreated()
    {
        using var test = new TestWorkspace(); var job = await GeneratedAsync(test); var store = new SqliteJobStore(test.Workspace);
        job.ExportName = ExportPublisher.Filename(job); job.ExportHash = job.FinalHash; job.ExportStagingOwned = true; job.Stage = JobStage.Exporting;
        await store.SaveAsync(job); await File.WriteAllTextAsync(Partial(test, job), "Interrupted owned copy");
        var restored = Assert.Single(await store.LoadAsync()); await new ExportPublisher(test.Workspace, store).PublishAsync(restored, default);
        Assert.True(restored.ExportCommitted); Assert.False(restored.ExportStagingOwned); Assert.Equal(job.FinalHash, await Workspace.HashFileAsync(Assert.Single(Directory.GetFiles(test.Destination, "*.mp3"))));
    }
    [Fact] public async Task SavedIntentAloneCannotAuthorizeDeletingAStagingCollision()
    {
        using var test = new TestWorkspace(); var job = await GeneratedAsync(test); var store = new SqliteJobStore(test.Workspace);
        var observer = new Observer(async (stage, _) => { if (stage == ExportCheckpoint.IntentSaved) await File.WriteAllTextAsync(Partial(test, job), "Collision before exclusive creation"); });
        await Assert.ThrowsAsync<IOException>(() => new ExportPublisher(test.Workspace, store, observer).PublishAsync(job, default));
        Assert.False(job.ExportStagingOwned); var restored = Assert.Single(await store.LoadAsync());
        await Assert.ThrowsAsync<IOException>(() => new ExportPublisher(test.Workspace, store).PublishAsync(restored, default));
        Assert.Equal("Collision before exclusive creation", await File.ReadAllTextAsync(Partial(test, job)));
    }
    [Fact] public async Task ReconciliationCleansKnownPartialAlongsideVerifiedFinal()
    {
        using var test = new TestWorkspace(); var job = await GeneratedAsync(test); var store = new SqliteJobStore(test.Workspace);
        job.ExportName = ExportPublisher.Filename(job); job.ExportHash = job.FinalHash; job.ExportStagingOwned = true;
        File.Copy(test.Workspace.FinalPath(job), Path.Combine(test.Destination, job.ExportName)); await File.WriteAllTextAsync(Partial(test, job), "Known leftover");
        await new ExportPublisher(test.Workspace, store).ReconcileAsync(job, default);
        Assert.True(job.ExportCommitted); Assert.False(File.Exists(Partial(test, job))); Assert.False(job.ExportStagingOwned);
    }
    [Fact] public async Task VerifiedFinalIsReportedEvenWhenUnrecognizedStagingIsPreserved()
    {
        using var test = new TestWorkspace(); var job = await GeneratedAsync(test); var store = new SqliteJobStore(test.Workspace);
        job.ExportName = ExportPublisher.Filename(job); job.ExportHash = job.FinalHash;
        File.Copy(test.Workspace.FinalPath(job), Path.Combine(test.Destination, job.ExportName)); await File.WriteAllTextAsync(Partial(test, job), "Unknown leftover");
        await new ExportPublisher(test.Workspace, store).ReconcileAsync(job, default);
        Assert.True(job.ExportCommitted); Assert.Equal("Unknown leftover", await File.ReadAllTextAsync(Partial(test, job))); Assert.Contains("preserved", job.Error);
    }
    [Fact] public async Task StagingChangedAfterVerificationCannotBecomeFinalAudio()
    {
        using var test = new TestWorkspace(); var job = await GeneratedAsync(test); var store = new SqliteJobStore(test.Workspace);
        var observer = new Observer(async (stage, _) => { if (stage == ExportCheckpoint.BeforeRename) await File.WriteAllTextAsync(Partial(test, job), "Changed verified staging"); });
        await Assert.ThrowsAsync<IOException>(() => new ExportPublisher(test.Workspace, store, observer).PublishAsync(job, default));
        Assert.False(job.ExportCommitted); Assert.Empty(Directory.GetFiles(test.Destination, "*.mp3")); Assert.Equal("Changed verified staging", await File.ReadAllTextAsync(Partial(test, job)));
        await new ExportPublisher(test.Workspace, store).PublishAsync(job, default); Assert.True(job.ExportCommitted);
    }
    [Fact] public async Task ValidatedSourceRemainsLockedThroughoutDestinationCopy()
    {
        using var test = new TestWorkspace(); var job = await GeneratedAsync(test); var attempted = false;
        var observer = new Observer((stage, _) => { if (stage == ExportCheckpoint.CopyProgress) { attempted = true; Assert.Throws<IOException>(() => File.WriteAllText(test.Workspace.FinalPath(job), "Changed source")); } return Task.CompletedTask; });
        await new ExportPublisher(test.Workspace, new SqliteJobStore(test.Workspace), observer).PublishAsync(job, default);
        Assert.True(attempted); Assert.Equal(job.FinalHash, await Workspace.HashFileAsync(Assert.Single(Directory.GetFiles(test.Destination, "*.mp3"))));
    }
    [Fact] public async Task FinalCollisionIntroducedImmediatelyBeforeRenameIsPreserved()
    {
        using var test = new TestWorkspace(); var job = await GeneratedAsync(test); var final = Path.Combine(test.Destination, ExportPublisher.Filename(job));
        var observer = new Observer(async (stage, _) => { if (stage == ExportCheckpoint.BeforeRename) await File.WriteAllTextAsync(final, "New unrelated final"); });
        await Assert.ThrowsAsync<IOException>(() => new ExportPublisher(test.Workspace, new SqliteJobStore(test.Workspace), observer).PublishAsync(job, default));
        Assert.False(job.ExportCommitted); Assert.Equal("New unrelated final", await File.ReadAllTextAsync(final)); Assert.False(File.Exists(Partial(test, job)));
    }
    [Fact] public async Task SeparateConcurrentJobsWithSameTitleAndTimePublishDistinctFiles()
    {
        using var test = new TestWorkspace(); var first = await GeneratedAsync(test); var second = await GeneratedAsync(test); second.CreatedUtc = first.CreatedUtc;
        var publisher = new ExportPublisher(test.Workspace, new SqliteJobStore(test.Workspace)); await Task.WhenAll(publisher.PublishAsync(first, default), publisher.PublishAsync(second, default));
        Assert.Equal(2, Directory.GetFiles(test.Destination, "*.mp3").Length); Assert.True(first.ExportCommitted && second.ExportCommitted);
    }
    private static string Partial(TestWorkspace test, Job job) => Path.Combine(test.Destination, $".commutecast-{job.Id}.partial");
    private static async Task<Job> GeneratedAsync(TestWorkspace test)
    {
        var job = new Job { Title = "Publication fixture", Destination = test.Destination };
        Directory.CreateDirectory(test.Workspace.JobDirectory(job.Id)); await File.WriteAllBytesAsync(test.Workspace.FinalPath(job), Enumerable.Range(0, 180000).Select(i => (byte)(i % 251)).ToArray());
        job.FinalHash = await Workspace.HashFileAsync(test.Workspace.FinalPath(job)); return job;
    }
    private sealed class Observer(Func<ExportCheckpoint, CancellationToken, Task> reached) : IExportObserver
    {
        public Task ReachedAsync(ExportCheckpoint checkpoint, CancellationToken ct) => reached(checkpoint, ct);
    }
    private sealed class CommitFailureStore(IJobStore inner, bool ownership = false) : IJobStore
    {
        public Task SaveAsync(Job job, CancellationToken ct = default) => (ownership ? job.ExportStagingOwned : job.ExportCommitted) ? throw new IOException("Injected checkpoint failure") : inner.SaveAsync(job, ct);
        public Task<IReadOnlyList<Job>> LoadAsync(CancellationToken ct = default) => inner.LoadAsync(ct);
        public Task<IReadOnlyDictionary<string, bool>> RequestDeletionAsync(IReadOnlyList<string> ids, bool deleteExports, CancellationToken ct = default) => inner.RequestDeletionAsync(ids, deleteExports, ct);
        public Task SaveQueueOrderAsync(IReadOnlyDictionary<string, long> positions, CancellationToken ct = default) => inner.SaveQueueOrderAsync(positions, ct);
        public Task RemoveAsync(string id, CancellationToken ct = default) => inner.RemoveAsync(id, ct);
    }
}
