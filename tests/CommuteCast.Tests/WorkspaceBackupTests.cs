using CommuteCast.Core;
using CommuteCast.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace CommuteCast.Tests;

public class WorkspaceBackupTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RestoreDirectoryLocksAreBoundedAndPreserveRecoverableOriginalState(bool persistent)
    {
        using var source = new TestWorkspace(); using var target = new TestWorkspace();
        var incoming = await InstallationTests.SeedAsync(source, "Incoming source"); var original = await InstallationTests.SeedAsync(target, "Original source");
        var originalHash = await Workspace.HashFileAsync(target.Workspace.ChunkPath(original, 0));
        using var sourceLease = WorkspaceLease.Acquire(source.Workspace); var backup = await WorkspaceBackup.CreateAsync(sourceLease);
        using var targetLease = WorkspaceLease.Acquire(target.Workspace); FileStream? locked = null; Task? release = null;
        var observer = new Observer((checkpoint, _, _) =>
        {
            if (checkpoint != RestoreCheckpoint.OldMoved) return Task.CompletedTask;
            var attempt = Assert.Single(Directory.GetDirectories(Path.Combine(target.Workspace.Root, "recovery", "restores")));
            locked = new FileStream(Path.Combine(attempt, "incoming", "jobs", incoming.Id, "chunk-00000.wav"), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!persistent) release = Task.Run(async () => { await Task.Delay(200); locked.Dispose(); });
            return Task.CompletedTask;
        });
        try
        {
            if (persistent)
            {
                var error = await Record.ExceptionAsync(() => WorkspaceBackup.RestoreAsync(targetLease, backup, observer));
                Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
                locked?.Dispose(); Assert.True(await WorkspaceBackup.RecoverInterruptedAsync(targetLease));
                Assert.Equal(original.Id, Assert.Single(await new SqliteJobStore(target.Workspace).LoadAsync()).Id);
                Assert.Equal(originalHash, await Workspace.HashFileAsync(target.Workspace.ChunkPath(original, 0)));
            }
            else
            {
                await WorkspaceBackup.RestoreAsync(targetLease, backup, observer);
                Assert.Equal(incoming.Id, Assert.Single(await new SqliteJobStore(target.Workspace).LoadAsync()).Id);
                Assert.False(await WorkspaceBackup.RecoverInterruptedAsync(targetLease));
            }
        }
        finally { locked?.Dispose(); if (release is not null) await release; }
    }
    [Fact] public async Task FullBackupAndRestorePreserveSourceSettingsDraftReceiptsAndEveryAudioByte()
    {
        using var source = new TestWorkspace(); using var target = new TestWorkspace(); var original = await SeedAsync(source, "Original source 😀"); var later = await SeedAsync(target, "Later source");
        var draftStore = new DraftStore(source.Workspace); var draft = await draftStore.LoadAsync();
        var brief = new NarrationBrief { Topic = "The history of railways", SourceMaterial = "Private reference notes 😀" };
        await draftStore.SaveAsync(draft with { PromptDraft = new(brief, NarrationPrompt.Build(brief, new(2026, 10, 10)) + "\nEdited prompt", brief, NarrationPrompt.TemplateVersion) });
        var unrelated = Path.Combine(target.Workspace.Root, "keep-unrelated.txt"); await File.WriteAllTextAsync(unrelated, "Keep unrelated local file");
        var exported = Path.Combine(target.Destination, "keep-exported.mp3"); await File.WriteAllTextAsync(exported, "Keep separate export");
        using var sourceLease = WorkspaceLease.Acquire(source.Workspace); var backup = await WorkspaceBackup.CreateAsync(sourceLease); var manifest = await WorkspaceBackup.ValidateAsync(backup);
        Assert.Contains(manifest.Files, f => f.RelativePath == "draft.json"); Assert.Contains(manifest.Files, f => f.RelativePath == "settings.json"); Assert.Contains(manifest.Files, f => f.RelativePath.EndsWith("complete.mp3"));
        using var targetLease = WorkspaceLease.Acquire(target.Workspace); var result = await WorkspaceBackup.RestoreAsync(targetLease, backup);
        var restored = Assert.Single(await new SqliteJobStore(target.Workspace).LoadAsync()); Assert.Equal(original.Id, restored.Id); Assert.Equal(original.Source, restored.Source); Assert.Equal(original.Settings, restored.Settings); Assert.Equal(original.CreatedUtc, restored.CreatedUtc); Assert.Equal(original.Receipts, restored.Receipts);
        Assert.Equal(await Workspace.HashFileAsync(source.Workspace.FinalPath(original)), await Workspace.HashFileAsync(target.Workspace.FinalPath(original)));
        Assert.Equal(await new DraftStore(source.Workspace).LoadAsync(), await new DraftStore(target.Workspace).LoadAsync()); Assert.Equal("piper", (await target.Workspace.LoadSettingsAsync()).Engine);
        Assert.True(File.Exists(Path.Combine(result.PreviousState, "queue.db"))); Assert.True(File.Exists(Path.Combine(result.PreviousState, "jobs", later.Id, "complete.mp3")));
        Assert.False(Directory.Exists(target.Workspace.JobDirectory(later.Id))); Assert.Equal("Keep separate export", await File.ReadAllTextAsync(exported)); Assert.Equal("Keep unrelated local file", await File.ReadAllTextAsync(unrelated));
        Assert.False(await WorkspaceBackup.RecoverInterruptedAsync(targetLease));
    }
    [Theory]
    [InlineData(RestoreCheckpoint.Prepared, null)] [InlineData(RestoreCheckpoint.OldItemMoved, "queue.db")]
    [InlineData(RestoreCheckpoint.OldItemMoved, "settings.json")] [InlineData(RestoreCheckpoint.OldItemMoved, "jobs")]
    [InlineData(RestoreCheckpoint.OldMoved, null)] [InlineData(RestoreCheckpoint.NewItemMoved, "queue.db")]
    [InlineData(RestoreCheckpoint.NewItemMoved, "settings.json")] [InlineData(RestoreCheckpoint.NewItemMoved, "jobs")]
    [InlineData(RestoreCheckpoint.NewMoved, null)]
    public async Task InterruptedRestoreRollsBackExactOriginalStateAndIsIdempotent(RestoreCheckpoint checkpoint, string? boundaryItem)
    {
        using var source = new TestWorkspace(); using var target = new TestWorkspace(); await SeedAsync(source, "Incoming source"); var original = await SeedAsync(target, "Original retained source");
        using var sourceLease = WorkspaceLease.Acquire(source.Workspace); var backup = await WorkspaceBackup.CreateAsync(sourceLease); using var targetLease = WorkspaceLease.Acquire(target.Workspace);
        var beforeDatabase = await Workspace.HashFileAsync(Path.Combine(target.Workspace.Root, "queue.db")); var beforeAudio = await Workspace.HashFileAsync(target.Workspace.FinalPath(original)); var beforeDraft = await new DraftStore(target.Workspace).LoadAsync();
        var observer = new Observer((stage, item, _) => stage == checkpoint && item == boundaryItem ? throw new IOException("Simulated process loss boundary") : Task.CompletedTask);
        await Assert.ThrowsAsync<IOException>(() => WorkspaceBackup.RestoreAsync(targetLease, backup, observer));
        Assert.True(File.Exists(Path.Combine(target.Workspace.Root, "recovery", "restore.pending.json")));
        Assert.True(await WorkspaceBackup.RecoverInterruptedAsync(targetLease)); Assert.False(await WorkspaceBackup.RecoverInterruptedAsync(targetLease));
        Assert.Equal(beforeDatabase, await Workspace.HashFileAsync(Path.Combine(target.Workspace.Root, "queue.db"))); Assert.Equal(beforeAudio, await Workspace.HashFileAsync(target.Workspace.FinalPath(original))); Assert.Equal(beforeDraft, await new DraftStore(target.Workspace).LoadAsync());
        Assert.Equal(original.Id, Assert.Single(await new SqliteJobStore(target.Workspace).LoadAsync()).Id);
    }
    [Fact] public async Task FailureAfterDurableCommitKeepsRestoredState()
    {
        using var source = new TestWorkspace(); using var target = new TestWorkspace(); var incoming = await SeedAsync(source, "Incoming committed source"); await SeedAsync(target, "Old source");
        using var sourceLease = WorkspaceLease.Acquire(source.Workspace); var backup = await WorkspaceBackup.CreateAsync(sourceLease); using var targetLease = WorkspaceLease.Acquire(target.Workspace);
        var observer = new Observer((stage, _, token) => { if (stage == RestoreCheckpoint.Committed) { Assert.False(token.CanBeCanceled); throw new IOException("Process loss after commit"); } return Task.CompletedTask; });
        await Assert.ThrowsAsync<IOException>(() => WorkspaceBackup.RestoreAsync(targetLease, backup, observer)); await WorkspaceBackup.RecoverInterruptedAsync(targetLease);
        Assert.Equal(incoming.Id, Assert.Single(await new SqliteJobStore(target.Workspace).LoadAsync()).Id);
    }
    [Theory] [InlineData(RestoreCheckpoint.Prepared)] [InlineData(RestoreCheckpoint.OldMoved)] [InlineData(RestoreCheckpoint.NewMoved)]
    public async Task CancelledRestoreRetainsRecoverableOriginalState(RestoreCheckpoint checkpoint)
    {
        using var source = new TestWorkspace(); using var target = new TestWorkspace(); await SeedAsync(source, "Incoming"); var original = await SeedAsync(target, "Original");
        using var sourceLease = WorkspaceLease.Acquire(source.Workspace); var backup = await WorkspaceBackup.CreateAsync(sourceLease); using var targetLease = WorkspaceLease.Acquire(target.Workspace); using var cancel = new CancellationTokenSource();
        var observer = new Observer((stage, _, _) => { if (stage == checkpoint) cancel.Cancel(); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorkspaceBackup.RestoreAsync(targetLease, backup, observer, cancel.Token));
        await WorkspaceBackup.RecoverInterruptedAsync(targetLease); Assert.Equal(original.Id, Assert.Single(await new SqliteJobStore(target.Workspace).LoadAsync()).Id);
    }
    [Theory] [InlineData("changed")] [InlineData("missing")] [InlineData("unlisted")] [InlineData("schema")] [InlineData("duplicate")] [InlineData("absolute")] [InlineData("traversal")] [InlineData("device")]
    public async Task InvalidBackupNeverReplacesAnyCurrentState(string defect)
    {
        using var source = new TestWorkspace(); using var target = new TestWorkspace(); await SeedAsync(source, "Incoming"); var original = await SeedAsync(target, "Original");
        using var sourceLease = WorkspaceLease.Acquire(source.Workspace); var backup = await WorkspaceBackup.CreateAsync(sourceLease); var manifest = await WorkspaceBackup.ValidateAsync(backup); var manifestPath = Path.Combine(backup, "manifest.json");
        switch (defect)
        {
            case "changed": await File.WriteAllTextAsync(Path.Combine(backup, "draft.json"), "Altered"); break;
            case "missing": File.Delete(Path.Combine(backup, "draft.json")); break;
            case "unlisted": await File.WriteAllTextAsync(Path.Combine(backup, "unlisted.txt"), "Unexpected"); break;
            case "schema": manifest = manifest with { SchemaVersion = 99 }; break;
            case "duplicate": manifest = manifest with { Files = manifest.Files.Append(manifest.Files[0]).ToList() }; break;
            default:
                var unsafePath = defect switch { "absolute" => "C:/Windows/unsafe", "traversal" => "../escape", _ => "jobs/" + original.Id + "/CON.mp3" };
                manifest = manifest with { Files = manifest.Files.Append(new(unsafePath, 0, Job.Hash(""))).ToList() }; break;
        }
        if (defect is not ("changed" or "missing" or "unlisted")) await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));
        var before = await Workspace.HashFileAsync(Path.Combine(target.Workspace.Root, "queue.db")); using var targetLease = WorkspaceLease.Acquire(target.Workspace);
        await Assert.ThrowsAsync<IOException>(() => WorkspaceBackup.RestoreAsync(targetLease, backup));
        Assert.Equal(before, await Workspace.HashFileAsync(Path.Combine(target.Workspace.Root, "queue.db"))); Assert.Equal(original.Id, Assert.Single(await new SqliteJobStore(target.Workspace).LoadAsync()).Id);
        Assert.False(File.Exists(Path.Combine(target.Workspace.Root, "recovery", "restore.pending.json")));
    }
    [Fact] public async Task DataChangedAfterInstallationCannotCrossRestoreCommit()
    {
        using var source = new TestWorkspace(); using var target = new TestWorkspace(); var incoming = await SeedAsync(source, "Incoming"); var original = await SeedAsync(target, "Original");
        using var sourceLease = WorkspaceLease.Acquire(source.Workspace); var backup = await WorkspaceBackup.CreateAsync(sourceLease); using var targetLease = WorkspaceLease.Acquire(target.Workspace);
        var observer = new Observer(async (stage, _, _) => { if (stage == RestoreCheckpoint.NewMoved) await File.WriteAllTextAsync(target.Workspace.FinalPath(incoming), "Changed incoming audio"); });
        await Assert.ThrowsAsync<IOException>(() => WorkspaceBackup.RestoreAsync(targetLease, backup, observer)); await WorkspaceBackup.RecoverInterruptedAsync(targetLease);
        Assert.Equal(original.Id, Assert.Single(await new SqliteJobStore(target.Workspace).LoadAsync()).Id);
    }
    [Fact] public async Task ChangedPreviousStateIsPreservedAndRecoveryRefusesToOverwriteCurrentState()
    {
        using var source = new TestWorkspace(); using var target = new TestWorkspace(); await SeedAsync(source, "Incoming"); await SeedAsync(target, "Original");
        using var sourceLease = WorkspaceLease.Acquire(source.Workspace); var backup = await WorkspaceBackup.CreateAsync(sourceLease); using var targetLease = WorkspaceLease.Acquire(target.Workspace);
        await Assert.ThrowsAsync<IOException>(() => WorkspaceBackup.RestoreAsync(targetLease, backup, new Observer((stage, _, _) => stage == RestoreCheckpoint.OldMoved ? throw new IOException("Stop") : Task.CompletedTask)));
        var previousDb = Assert.Single(Directory.GetFiles(Path.Combine(target.Workspace.Root, "recovery", "restores"), "queue.db", SearchOption.AllDirectories), p => p.Contains("previous"));
        await File.WriteAllTextAsync(previousDb, "Changed previous database"); await Assert.ThrowsAsync<IOException>(() => WorkspaceBackup.RecoverInterruptedAsync(targetLease));
        Assert.Equal("Changed previous database", await File.ReadAllTextAsync(previousDb)); Assert.True(File.Exists(Path.Combine(target.Workspace.Root, "recovery", "restore.pending.json")));
    }
    [Fact] public async Task RestorePreservesCorruptCurrentDatabaseForInspection()
    {
        using var source = new TestWorkspace(); using var target = new TestWorkspace(); var incoming = await SeedAsync(source, "Incoming good source"); await SeedAsync(target, "Old source");
        var corrupt = new byte[] { 0, 1, 2, 3 }; await File.WriteAllBytesAsync(Path.Combine(target.Workspace.Root, "queue.db"), corrupt);
        using var sourceLease = WorkspaceLease.Acquire(source.Workspace); var backup = await WorkspaceBackup.CreateAsync(sourceLease); using var targetLease = WorkspaceLease.Acquire(target.Workspace);
        var result = await WorkspaceBackup.RestoreAsync(targetLease, backup); Assert.Equal(corrupt, await File.ReadAllBytesAsync(Path.Combine(result.PreviousState, "queue.db")));
        Assert.Equal(incoming.Id, Assert.Single(await new SqliteJobStore(target.Workspace).LoadAsync()).Id);
    }
    [Fact] public async Task BackupExcludesHistoricalCopiesAndModelsButIncludesCurrentState()
    {
        using var test = new TestWorkspace(); await SeedAsync(test, "Current source");
        foreach (var name in new[] { "schema-backups", "provisioning-models", "recovery" }) { Directory.CreateDirectory(Path.Combine(test.Workspace.Root, name)); await File.WriteAllTextAsync(Path.Combine(test.Workspace.Root, name, "historical.txt"), "Excluded historical or installation artifact"); }
        using var lease = WorkspaceLease.Acquire(test.Workspace); var first = await WorkspaceBackup.CreateAsync(lease); var second = await WorkspaceBackup.CreateAsync(lease);
        Assert.Equal((await WorkspaceBackup.ValidateAsync(first)).Files.Count, (await WorkspaceBackup.ValidateAsync(second)).Files.Count);
        Assert.DoesNotContain((await WorkspaceBackup.ValidateAsync(second)).Files, f => f.RelativePath.StartsWith("backups/") || f.RelativePath.StartsWith("recovery/") || f.RelativePath.StartsWith("provisioning-models/"));
    }
    [Fact] public async Task LeaseMustBeHeldAndPartialBackupsCannotBeValidated()
    {
        using var test = new TestWorkspace(); var lease = WorkspaceLease.Acquire(test.Workspace); lease.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => WorkspaceBackup.CreateAsync(lease));
        var partial = Path.Combine(test.Workspace.Root, "partial"); Directory.CreateDirectory(partial); await File.WriteAllTextAsync(Path.Combine(partial, "settings.json"), "{}");
        await Assert.ThrowsAsync<IOException>(() => WorkspaceBackup.ValidateAsync(partial));
    }
    [Fact] public async Task CancelledBackupDoesNotPublishACompletedManifest()
    {
        using var test = new TestWorkspace(); await SeedAsync(test, "Source"); using var lease = WorkspaceLease.Acquire(test.Workspace); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorkspaceBackup.CreateAsync(lease, cancel.Token));
        Assert.False(Directory.Exists(Path.Combine(test.Workspace.Root, "backups")));
    }
    [Fact] public async Task AtomicStateWritesPreserveUnknownTemporaryFiles()
    {
        using var test = new TestWorkspace(); var path = Path.Combine(test.Workspace.Root, "state.json");
        await File.WriteAllTextAsync(path + ".tmp", "Unrecognized old temporary content");
        await Workspace.AtomicWriteAsync(path, "Complete new state");
        Assert.Equal("Complete new state", await File.ReadAllTextAsync(path));
        Assert.Equal("Unrecognized old temporary content", await File.ReadAllTextAsync(path + ".tmp"));
        Assert.Empty(Directory.EnumerateFiles(test.Workspace.Root, "state.json.tmp-*"));
    }
    [Fact] public async Task ConcurrentAtomicWritesPublishOneCompleteSnapshotWithoutSharingAStagingFile()
    {
        using var test = new TestWorkspace(); var path = Path.Combine(test.Workspace.Root, "state.json");
        var snapshots = Enumerable.Range(0, 20).Select(i => JsonSerializer.Serialize(new { id = i, content = new string((char)('a' + i), 100000) })).ToArray();
        await Task.WhenAll(snapshots.Select(snapshot => Workspace.AtomicWriteAsync(path, snapshot)));
        Assert.Contains(await File.ReadAllTextAsync(path), snapshots);
        Assert.Empty(Directory.EnumerateFiles(test.Workspace.Root, "state.json.tmp-*"));
    }
    [Fact] public async Task TemporaryDestinationReadLockAllowsBoundedAtomicReplacementAfterRelease()
    {
        using var test = new TestWorkspace(); var path = Path.Combine(test.Workspace.Root, "state.json"); await File.WriteAllTextAsync(path, "Original durable state");
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var pending = Workspace.AtomicWriteAsync(path, "Complete replacement"); await Task.Delay(200);
        Assert.False(pending.IsCompleted); Assert.Equal("Original durable state", await File.ReadAllTextAsync(path));
        reader.Dispose(); await pending.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal("Complete replacement", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(test.Workspace.Root, "state.json.tmp-*"));
    }
    [Fact] public async Task UnreleasedDestinationLockFailsWithoutChangingOriginalOrLeavingOwnedStaging()
    {
        using var test = new TestWorkspace(); var path = Path.Combine(test.Workspace.Root, "state.json"); await File.WriteAllTextAsync(path, "Original durable state");
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var error = await Record.ExceptionAsync(() => Workspace.AtomicWriteAsync(path, "Blocked replacement"));
        Assert.True(error is IOException or UnauthorizedAccessException); Assert.Equal("Original durable state", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(test.Workspace.Root, "state.json.tmp-*"));
    }
    private static async Task<Job> SeedAsync(TestWorkspace test, string source)
    {
        var job = new Job { Title = source, Source = source, Prepared = TextPreparation.Prepare(source), Destination = test.Destination, Stage = JobStage.Failed, Settings = new("piper", "en_US-lessac-medium", 1.1, false, "API=A P I", "immutable-fixture") };
        job.Chunks = Chunker.Split(job.Prepared.Script, 450); Directory.CreateDirectory(test.Workspace.JobDirectory(job.Id)); TestWorkspace.WriteWave(test.Workspace.ChunkPath(job, 0), 2);
        job.Receipts.Add(new(0, await Workspace.HashFileAsync(test.Workspace.ChunkPath(job, 0)), job.Fingerprint, 2)); job.CompletedChunks = 1;
        await new AudioPipeline(new()).AssembleAsync(job, test.Workspace.JobDirectory(job.Id), default); job.FinalHash = await Workspace.HashFileAsync(test.Workspace.FinalPath(job));
        await new SqliteJobStore(test.Workspace).SaveAsync(job); await test.Workspace.SaveSettingsAsync(new() { Engine = "piper", Voice = "en_US-lessac-medium", QueuePaused = true });
        await new DraftStore(test.Workspace).SaveAsync(new("Draft — " + source, "Preserved draft — " + source));
        return job;
    }
    private sealed class Observer(Func<RestoreCheckpoint, string?, CancellationToken, Task> reached) : IRestoreObserver
    { public Task ReachedAsync(RestoreCheckpoint checkpoint, string? item, CancellationToken ct) => reached(checkpoint, item, ct); }
}
