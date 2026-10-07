using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public class PrivateWriteTests
{
    [Theory]
    [InlineData("write")]
    [InlineData("cancel")]
    [InlineData("checkpoint")]
    [InlineData("after-save")]
    public async Task HandledFailureRemovesOnlyTheHeldCreationAndAllowsDurableRetry(string failure)
    {
        using var test = new TestWorkspace(); var job = new Job(); var store = new SqliteJobStore(test.Workspace);
        var directory = test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "unknown.txt"), "Preserve");
        using var cancellation = new CancellationTokenSource();
        var checkpoints = 0;
        var operation = PrivateJobFiles.WriteRecordedAsync(job, directory, "assembled.wav", async output =>
        {
            await output.WriteAsync(new byte[] { 1, 2, 3 });
            if (failure == "write") throw new IOException("Injected write failure");
            if (failure == "cancel") cancellation.Cancel();
        }, async () =>
        {
            if (++checkpoints == 1) { await store.SaveAsync(job); return; }
            if (failure == "after-save") await store.SaveAsync(job);
            throw new IOException("Injected checkpoint failure");
        }, cancellation.Token);
        if (failure == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        else await Assert.ThrowsAsync<IOException>(() => operation);
        Assert.False(File.Exists(Path.Combine(directory, "assembled.wav")));
        Assert.DoesNotContain(job.PrivateArtifacts, r => r.RelativePath == "assembled.wav");
        Assert.Equal("Preserve", await File.ReadAllTextAsync(Path.Combine(directory, "unknown.txt")));
        var retry = Assert.Single(await store.LoadAsync());
        await PrivateJobFiles.WriteRecordedAsync(retry, directory, "assembled.wav",
            stream => stream.WriteAsync(new byte[] { 4, 5, 6 }).AsTask(), () => store.SaveAsync(retry), default);
        Assert.Equal(await Workspace.HashFileAsync(Path.Combine(directory, "assembled.wav")),
            PrivateJobFiles.Inventory(Assert.Single(await store.LoadAsync()))["assembled.wav"]);
    }

    [Fact] public async Task ReceiptIsDurableBeforeTheCreationHandleCanBeReplaced()
    {
        using var test = new TestWorkspace(); var job = new Job(); var store = new SqliteJobStore(test.Workspace);
        var directory = test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "assembled.wav");
        await PrivateJobFiles.WriteRecordedAsync(job, directory, "assembled.wav",
            stream => stream.WriteAsync(new byte[] { 7, 8, 9 }).AsTask(), async () =>
            {
                Assert.Throws<IOException>(() => File.Delete(path));
                Assert.Throws<IOException>(() => File.WriteAllBytes(path, new byte[] { 7, 8, 9 }));
                await store.SaveAsync(job);
            }, default);
        await PrivateJobFiles.RemoveAsync(Assert.Single(await store.LoadAsync()), directory);
        Assert.False(Directory.Exists(directory));
    }

    [Fact] public async Task UnknownOutputIsPreservedBeforeTheWriterRuns()
    {
        using var test = new TestWorkspace(); var job = new Job();
        var directory = test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "assembled.wav"); await File.WriteAllTextAsync(path, "Unknown");
        await Assert.ThrowsAsync<IOException>(() => PrivateJobFiles.WriteRecordedAsync(job, directory, "assembled.wav",
            _ => throw new InvalidOperationException("Must not write"), () => throw new InvalidOperationException("Must not save"), default));
        Assert.Equal("Unknown", await File.ReadAllTextAsync(path)); Assert.Empty(job.PrivateArtifacts);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task IncompleteCreationReconcilesExactIdentityOrMissingFileAndAllowsRetry(bool missing)
    {
        using var test = new TestWorkspace(); var job = new Job(); var store = new SqliteJobStore(test.Workspace);
        var directory = test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        await using (var held = ExportStagingFile.Create(directory, "assembled.wav"))
        {
            job.PrivateArtifacts.Add(new("assembled.wav", "", CreationIdentity: held.Identity));
            await store.SaveAsync(job);
            await held.Stream.WriteAsync(new byte[] { 1, 2, 3 }); held.Stream.Flush(true);
            if (missing) held.Delete();
        }
        var reopened = Assert.Single(await store.LoadAsync());
        Assert.Empty(PrivateJobFiles.Inventory(reopened));
        await PrivateJobFiles.ReconcilePromotionsAsync(reopened, directory, () => store.SaveAsync(reopened), default);
        Assert.False(File.Exists(Path.Combine(directory, "assembled.wav")));
        Assert.Empty(Assert.Single(await store.LoadAsync()).PrivateArtifacts);
        await PrivateJobFiles.WriteRecordedAsync(reopened, directory, "assembled.wav", s => s.WriteAsync(new byte[] { 4, 5, 6 }).AsTask(), () => store.SaveAsync(reopened), default);
        Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(Path.Combine(directory, "assembled.wav")));
    }

    [Fact] public async Task ReplacedIncompleteCreationIsPreservedByRecoveryPreparationAndDeletion()
    {
        using var test = new TestWorkspace(); var job = new Job(); var store = new SqliteJobStore(test.Workspace);
        var directory = test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        await using (var held = ExportStagingFile.Create(directory, "assembled.wav"))
        {
            job.PrivateArtifacts.Add(new("assembled.wav", "", CreationIdentity: held.Identity));
            await store.SaveAsync(job); held.Rename("original-retained.wav");
        }
        var path = Path.Combine(directory, "assembled.wav"); await File.WriteAllTextAsync(path, "Unknown replacement");
        var reopened = Assert.Single(await store.LoadAsync());
        await Assert.ThrowsAsync<IOException>(() => PrivateJobFiles.ReconcilePromotionsAsync(reopened, directory, () => store.SaveAsync(reopened), default));
        await Assert.ThrowsAsync<IOException>(() => PrivateJobFiles.PrepareOutputAsync(reopened, directory, "assembled.wav", default));
        await Assert.ThrowsAsync<IOException>(() => PrivateJobFiles.RemoveAsync(reopened, directory));
        Assert.Equal("Unknown replacement", await File.ReadAllTextAsync(path));
        Assert.NotNull(Assert.Single(Assert.Single(await store.LoadAsync()).PrivateArtifacts).CreationIdentity);
    }

    [Fact] public async Task CreationCheckpointIsDurableBeforeWriterStartsAndPartialIsNotReusable()
    {
        using var test = new TestWorkspace(); var job = new Job(); var store = new SqliteJobStore(test.Workspace);
        var directory = test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        await PrivateJobFiles.WriteRecordedAsync(job, directory, "assembled.wav", async output =>
        {
            var saved = Assert.Single(await store.LoadAsync()); var receipt = Assert.Single(saved.PrivateArtifacts);
            Assert.Equal("", receipt.Hash); Assert.NotNull(receipt.CreationIdentity); Assert.Empty(PrivateJobFiles.Inventory(saved));
            await output.WriteAsync(new byte[] { 1, 2, 3 });
        }, () => store.SaveAsync(job), default);
        var completed = Assert.Single(Assert.Single(await store.LoadAsync()).PrivateArtifacts);
        Assert.Null(completed.CreationIdentity); Assert.Equal(await Workspace.HashFileAsync(Path.Combine(directory, "assembled.wav")), completed.Hash);
    }

    [Fact] public async Task FailedCreationCheckpointNeverInvokesWriterAndRemovesExactCreatedFile()
    {
        using var test = new TestWorkspace(); var job = new Job();
        var directory = test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        await Assert.ThrowsAsync<IOException>(() => PrivateJobFiles.WriteRecordedAsync(job, directory, "assembled.wav",
            _ => throw new InvalidOperationException("Writer must not start"), () => throw new IOException("Checkpoint failed"), default));
        Assert.Empty(job.PrivateArtifacts); Assert.False(File.Exists(Path.Combine(directory, "assembled.wav")));
    }

    [Theory] [InlineData("hash")] [InlineData("both-identities")] [InlineData("bad-id")] [InlineData("path")]
    public void InvalidCreationReceiptIsRefusedBeforeItCanAuthorizeRemoval(string defect)
    {
        var identity = new ExportStagingIdentity(1, 1, new string('A', 32), 1);
        var job = new Job();
        job.PrivateArtifacts.Add(new(defect == "path" ? "../foreign.wav" : "assembled.wav", defect == "hash" ? new string('A', 64) : "",
            defect == "both-identities" ? identity : null, defect == "bad-id" ? identity with { FileId = "bad" } : identity));
        Assert.Throws<IOException>(() => PrivateJobFiles.Inventory(job));
    }

    [Fact] public async Task BackupAndCacheRefuseUnreconciledCreationAndPreserveItsBytes()
    {
        using var test = new TestWorkspace(); var job = new Job { Stage = JobStage.Failed }; var store = new SqliteJobStore(test.Workspace);
        var directory = test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        await using (var held = ExportStagingFile.Create(directory, "assembled.wav"))
        {
            job.PrivateArtifacts.Add(new("assembled.wav", "", CreationIdentity: held.Identity));
            await store.SaveAsync(job); await held.Stream.WriteAsync(new byte[] { 1, 2, 3 }); held.Stream.Flush(true);
        }
        File.SetLastWriteTimeUtc(Path.Combine(directory, "assembled.wav"), DateTime.UtcNow.AddDays(-30));
        using var lease = WorkspaceLease.Acquire(test.Workspace);
        await Assert.ThrowsAsync<IOException>(() => WorkspaceBackup.CreateAsync(lease));
        Assert.Equal(0, (await new CacheMaintenance(test.Workspace).CleanAsync([job], null, 1, 1, default)).FilesRemoved);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(directory, "assembled.wav")));
        await PrivateJobFiles.ReconcilePromotionsAsync(job, directory, () => store.SaveAsync(job), default);
        var backup = await WorkspaceBackup.CreateAsync(lease); Assert.True(Directory.Exists(backup));
    }
}
