using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CommuteCast.Tests;

public class InstallationTests
{
    [Fact] public async Task PendingPrivateUninstallCannotDiscardNewPreviewOwnershipOnRecovery()
    {
        using var test = new TestWorkspace(); await SeedAsync(test, "Retain recoverable preview");
        var package = await PackageAsync(test, "pending-preview"); using var lease = WorkspaceLease.Acquire(test.Workspace);
        var install = new Installation(Path.Combine(test.Parent, "program")); var active = await install.ActivateAsync(lease, package);
        var ledger = new AuditionOwnershipStore(test.Workspace);
        var journal = new AuditionWriteJournal(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, []);
        await Assert.ThrowsAsync<IOException>(() => install.UninstallAsync(lease, true, new Observer(async (stage, _, _) =>
        {
            if (stage == InstallationCheckpoint.Activated) { await ledger.SaveAsync(journal); throw new IOException("Fixture loss after activation"); }
        })));
        Assert.True(install.HasPendingOperation);
        var database = Path.Combine(test.Workspace.Root, "queue.db"); var before = await Workspace.HashFileAsync(database);
        var error = await Assert.ThrowsAsync<IOException>(() => install.RecoverAsync(lease)); Assert.Contains("previews", error.Message);
        Assert.Equal(before, await Workspace.HashFileAsync(database)); Assert.Single(await ledger.LoadAsync());
        Assert.True(install.HasPendingOperation); Assert.True(File.Exists(active.Executable));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PrivateUninstallPreservesUnsettledPreviewLedgerAndBytes(bool hasFile)
    {
        using var test = new TestWorkspace(); await SeedAsync(test, "Preserve narration");
        var package = await PackageAsync(test, "preview"); using var lease = WorkspaceLease.Acquire(test.Workspace);
        var install = new Installation(Path.Combine(test.Parent, "program")); var active = await install.ActivateAsync(lease, package);
        var journal = new AuditionWriteJournal(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, []);
        var directory = Path.Combine(test.Workspace.Root, "auditions"); Directory.CreateDirectory(directory);
        var name = "audition-" + journal.Id + ".wav";
        if (hasFile)
        {
            await using var held = ExportStagingFile.Create(directory, name);
            await held.Stream.WriteAsync(new byte[] { 1, 2, 3 });
            journal.Artifacts.Add(new(name, "", CreationIdentity: held.Identity));
        }
        var ledger = new AuditionOwnershipStore(test.Workspace); await ledger.SaveAsync(journal);
        var database = Path.Combine(test.Workspace.Root, "queue.db");
        var before = await Workspace.HashFileAsync(database);
        var error = await Assert.ThrowsAsync<IOException>(() => install.UninstallAsync(lease, true)); Assert.Contains("previews", error.Message);
        Assert.Equal(before, await Workspace.HashFileAsync(database)); Assert.Single(await ledger.LoadAsync());
        Assert.False(install.HasPendingOperation); Assert.True(File.Exists(active.Executable));
        if (hasFile) Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(directory, name)));
        // Retain-data uninstall preserves the same ledger and audio without requiring cleanup.
        await install.UninstallAsync(lease, false); Assert.Single(await ledger.LoadAsync());
        Assert.True(File.Exists(database)); if (hasFile) Assert.True(File.Exists(Path.Combine(directory, name)));
    }
    [Fact] public async Task MaintenanceEntryVerifiesActivePackageWhileRetainingCorruptQueueForRepair()
    {
        using var test = new TestWorkspace(); await SeedAsync(test, "Original"); var package = await PackageAsync(test, "repair"); using var lease = WorkspaceLease.Acquire(test.Workspace); var install = new Installation(Path.Combine(test.Parent, "program"));
        var active = await install.ActivateAsync(lease, package); byte[] corrupt = [0, 2, 4, 6]; await File.WriteAllBytesAsync(Path.Combine(test.Workspace.Root, "queue.db"), corrupt);
        await Assert.ThrowsAsync<IOException>(() => install.InspectAsync(lease)); Assert.Equal(active.Executable, (await install.InspectForMaintenanceAsync(lease)).Executable);
        Assert.Equal(corrupt, await File.ReadAllBytesAsync(Path.Combine(test.Workspace.Root, "queue.db")));
        await File.WriteAllTextAsync(active.Executable!, "Changed active binary"); await Assert.ThrowsAsync<IOException>(() => install.InspectForMaintenanceAsync(lease));
    }
    [Fact] public void InstalledRootRecognitionUsesWindowsPathCasing()
    {
        var root = Path.Combine(Path.GetTempPath(), "CommuteCast-Installed");
        var app = Path.Combine(root, "releases", new string('A', 64), "app");
        Assert.Equal(root, Installation.FindRoot(app + Path.DirectorySeparatorChar));
        Assert.Equal(root.ToLowerInvariant(), Installation.FindRoot(app.ToLowerInvariant() + Path.DirectorySeparatorChar));
        Assert.Null(Installation.FindRoot(Path.Combine(root, "portable", "app")));
    }
    [Fact] public async Task FreshInstallUpdateRollbackAndUndoPreserveImmutableQueuedWorkAndArtifacts()
    {
        using var test = new TestWorkspace(); var original = await SeedAsync(test, "Original queued source 😀"); var a = await PackageAsync(test, "a"); var b = await PackageAsync(test, "b");
        using var lease = WorkspaceLease.Acquire(test.Workspace); var install = new Installation(Path.Combine(test.Parent, "program"));
        var first = await install.ActivateAsync(lease, a); Assert.True(File.Exists(first.Executable)); Assert.Null(first.State.Previous);
        var repeated = await install.ActivateAsync(lease, a); Assert.True(repeated.AlreadyCurrent); Assert.Equal(first.State.CurrentPackageId, repeated.State.CurrentPackageId);
        var updated = await install.ActivateAsync(lease, b); Assert.NotEqual(first.State.CurrentPackageId, updated.State.CurrentPackageId); Assert.Equal(first.State.CurrentPackageId, updated.State.Previous!.PackageId);
        var later = await SeedAsync(test, "Queued after update"); var audioHash = await Workspace.HashFileAsync(test.Workspace.ChunkPath(original, 0));
        var rolledBack = await install.RollbackAsync(lease); Assert.Equal(first.State.CurrentPackageId, rolledBack.State.CurrentPackageId);
        var restored = Assert.Single(await new SqliteJobStore(test.Workspace).LoadAsync()); Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(restored));
        Assert.Equal(audioHash, await Workspace.HashFileAsync(test.Workspace.ChunkPath(original, 0))); Assert.False(Directory.Exists(test.Workspace.JobDirectory(later.Id)));
        Assert.Equal("Original queued source 😀", (await new DraftStore(test.Workspace).LoadAsync()).Source);
        var undo = await install.RollbackAsync(lease); Assert.Equal(updated.State.CurrentPackageId, undo.State.CurrentPackageId);
        Assert.Equal(2, (await new SqliteJobStore(test.Workspace).LoadAsync()).Count); Assert.Equal("Queued after update", (await new DraftStore(test.Workspace).LoadAsync()).Source);
        Assert.Equal(undo.State.CurrentPackageId, (await install.InspectAsync(lease)).State.CurrentPackageId); Assert.False(await install.RecoverAsync(lease));
    }
    [Theory] [InlineData(InstallationCheckpoint.Prepared)] [InlineData(InstallationCheckpoint.StateMigrated)] [InlineData(InstallationCheckpoint.BeforeActivation)] [InlineData(InstallationCheckpoint.Activated)]
    public async Task UpgradeInterruptionKeepsOldBeforePointerCommitAndNewAfterIt(InstallationCheckpoint checkpoint)
    {
        using var test = new TestWorkspace(); var original = await SeedAsync(test, "Preserve me"); var a = await PackageAsync(test, "a"); var b = await PackageAsync(test, "b");
        using var lease = WorkspaceLease.Acquire(test.Workspace); var install = new Installation(Path.Combine(test.Parent, "program")); var first = await install.ActivateAsync(lease, a);
        var observer = new Observer((stage, _, token) => { if (stage != checkpoint) return Task.CompletedTask; if (stage == InstallationCheckpoint.Activated) Assert.False(token.CanBeCanceled); throw new IOException("Simulated process loss"); });
        var error = await Assert.ThrowsAsync<IOException>(() => install.ActivateAsync(lease, b, observer));
        Assert.Equal("Simulated process loss", error.Message);
        Assert.True(await install.RecoverAsync(lease)); Assert.False(await install.RecoverAsync(lease));
        var inspected = await install.InspectAsync(lease); Assert.Equal(checkpoint == InstallationCheckpoint.Activated, inspected.State.CurrentPackageId != first.State.CurrentPackageId);
        Assert.Equal(original.Id, Assert.Single(await new SqliteJobStore(test.Workspace).LoadAsync()).Id); Assert.True(File.Exists(test.Workspace.ChunkPath(original, 0)));
    }
    [Theory] [InlineData(InstallationCheckpoint.Prepared)] [InlineData(InstallationCheckpoint.StateRestored)] [InlineData(InstallationCheckpoint.BeforeActivation)] [InlineData(InstallationCheckpoint.Activated)]
    public async Task RollbackInterruptionRecoversTheMatchingBinaryAndQueuePair(InstallationCheckpoint checkpoint)
    {
        using var test = new TestWorkspace(); var original = await SeedAsync(test, "Before update"); var a = await PackageAsync(test, "a"); var b = await PackageAsync(test, "b");
        using var lease = WorkspaceLease.Acquire(test.Workspace); var install = new Installation(Path.Combine(test.Parent, "program")); var first = await install.ActivateAsync(lease, a); var second = await install.ActivateAsync(lease, b);
        var later = await SeedAsync(test, "After update");
        await Assert.ThrowsAsync<IOException>(() => install.RollbackAsync(lease, new Observer((stage, _, _) => stage == checkpoint ? throw new IOException("Stop") : Task.CompletedTask)));
        await install.RecoverAsync(lease); var result = await install.InspectAsync(lease); var jobs = await new SqliteJobStore(test.Workspace).LoadAsync();
        Assert.Equal(checkpoint == InstallationCheckpoint.Activated ? first.State.CurrentPackageId : second.State.CurrentPackageId, result.State.CurrentPackageId);
        Assert.Contains(jobs, j => j.Id == original.Id); Assert.Equal(checkpoint != InstallationCheckpoint.Activated, jobs.Any(j => j.Id == later.Id));
    }
    [Theory] [InlineData(false)] [InlineData(true)] public async Task UninstallScopesKeepExportsUnrelatedFilesAndModels(bool removePrivate)
    {
        using var test = new TestWorkspace(); var job = await SeedAsync(test, "Private queued source"); var a = await PackageAsync(test, "a"); var install = new Installation(Path.Combine(test.Parent, "program"));
        using var lease = WorkspaceLease.Acquire(test.Workspace); var first = await install.ActivateAsync(lease, a);
        await File.WriteAllTextAsync(Path.Combine(test.Workspace.Root, "keep-unrelated.txt"), "Unrelated private-root file");
        Directory.CreateDirectory(Path.Combine(test.Workspace.Root, "provisioning-models")); await File.WriteAllTextAsync(Path.Combine(test.Workspace.Root, "provisioning-models", "keep.txt"), "Separate engine artifact");
        await File.WriteAllTextAsync(Path.Combine(test.Destination, "keep-export.mp3"), "Separate exported sentinel"); await File.WriteAllTextAsync(Path.Combine(install.Root, "keep-program-note.txt"), "Unrelated program-root file");
        var result = await install.UninstallAsync(lease, removePrivate); Assert.Null(result.State.CurrentPackageId); Assert.Null(result.Executable); Assert.False(File.Exists(first.Executable));
        Assert.Equal(!removePrivate, File.Exists(Path.Combine(test.Workspace.Root, "queue.db"))); Assert.Equal(!removePrivate, File.Exists(test.Workspace.ChunkPath(job, 0)));
        Assert.Equal(!removePrivate, Directory.Exists(Path.Combine(test.Workspace.Root, "backups"))); Assert.Equal(!removePrivate, File.Exists(Path.Combine(test.Workspace.Root, "draft.json")));
        Assert.Equal("Unrelated private-root file", await File.ReadAllTextAsync(Path.Combine(test.Workspace.Root, "keep-unrelated.txt")));
        Assert.Equal("Separate engine artifact", await File.ReadAllTextAsync(Path.Combine(test.Workspace.Root, "provisioning-models", "keep.txt")));
        Assert.Equal("Separate exported sentinel", await File.ReadAllTextAsync(Path.Combine(test.Destination, "keep-export.mp3")));
        Assert.Equal("Unrelated program-root file", await File.ReadAllTextAsync(Path.Combine(install.Root, "keep-program-note.txt"))); Assert.Null((await install.InspectAsync(lease)).Executable);
    }
    [Theory] [InlineData(InstallationCheckpoint.Prepared)] [InlineData(InstallationCheckpoint.Activated)] [InlineData(InstallationCheckpoint.ItemRemoved)]
    public async Task InterruptedUninstallIsRetainedBeforeCommitOrContinuesRecordedRemovalAfterCommit(InstallationCheckpoint checkpoint)
    {
        using var test = new TestWorkspace(); await SeedAsync(test, "Private source"); var a = await PackageAsync(test, "a"); var install = new Installation(Path.Combine(test.Parent, "program")); using var lease = WorkspaceLease.Acquire(test.Workspace);
        var first = await install.ActivateAsync(lease, a); var fired = false;
        await Assert.ThrowsAsync<IOException>(() => install.UninstallAsync(lease, true, new Observer((stage, _, _) => { if (stage == checkpoint && !fired) { fired = true; throw new IOException("Stop"); } return Task.CompletedTask; })));
        Assert.True(await install.RecoverAsync(lease)); Assert.False(await install.RecoverAsync(lease));
        Assert.Equal(checkpoint == InstallationCheckpoint.Prepared, File.Exists(first.Executable)); Assert.Equal(checkpoint == InstallationCheckpoint.Prepared, File.Exists(Path.Combine(test.Workspace.Root, "queue.db")));
        Assert.Equal(checkpoint == InstallationCheckpoint.Prepared, (await install.InspectAsync(lease)).Executable is not null);
    }
    [Fact] public async Task ChangedSnapshotOrReleaseNeverActivatesRollback()
    {
        using var test = new TestWorkspace(); await SeedAsync(test, "Original"); var a = await PackageAsync(test, "a"); var b = await PackageAsync(test, "b"); using var lease = WorkspaceLease.Acquire(test.Workspace);
        var install = new Installation(Path.Combine(test.Parent, "program")); await install.ActivateAsync(lease, a); var second = await install.ActivateAsync(lease, b);
        var before = await Workspace.HashFileAsync(Path.Combine(install.Root, "installation.json")); var snapshot = Path.Combine(test.Workspace.Root, "backups", second.State.Previous!.Snapshot.BackupId, "draft.json");
        await File.WriteAllTextAsync(snapshot, "Changed snapshot"); await Assert.ThrowsAsync<IOException>(() => install.RollbackAsync(lease)); Assert.Equal(before, await Workspace.HashFileAsync(Path.Combine(install.Root, "installation.json")));
        await File.WriteAllTextAsync(Path.Combine(install.Root, "releases", second.State.CurrentPackageId!, "unknown.txt"), "Keep unexpected file");
        await Assert.ThrowsAsync<IOException>(() => install.UninstallAsync(lease, true)); Assert.Equal(before, await Workspace.HashFileAsync(Path.Combine(install.Root, "installation.json")));
        Assert.True(File.Exists(Path.Combine(test.Workspace.Root, "queue.db")));
    }
    [Fact] public async Task CancellationBeforeActivationLeavesRecoverableOriginalPair()
    {
        using var test = new TestWorkspace(); var job = await SeedAsync(test, "Keep queued data"); var a = await PackageAsync(test, "a"); var b = await PackageAsync(test, "b"); using var lease = WorkspaceLease.Acquire(test.Workspace);
        var install = new Installation(Path.Combine(test.Parent, "program")); var first = await install.ActivateAsync(lease, a); using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install.ActivateAsync(lease, b, new Observer((stage, _, _) => { if (stage == InstallationCheckpoint.BeforeActivation) cancel.Cancel(); return Task.CompletedTask; }), cancel.Token));
        await install.RecoverAsync(lease); Assert.Equal(first.State.CurrentPackageId, (await install.InspectAsync(lease)).State.CurrentPackageId); Assert.Equal(job.Id, Assert.Single(await new SqliteJobStore(test.Workspace).LoadAsync()).Id);
    }
    [Fact] public async Task ForeignRootWrongBindingAndOverlappingRootsAreRefused()
    {
        using var test = new TestWorkspace(); var a = await PackageAsync(test, "a"); using var lease = WorkspaceLease.Acquire(test.Workspace);
        var foreign = new Installation(Path.Combine(test.Parent, "foreign")); Directory.CreateDirectory(foreign.Root); await File.WriteAllTextAsync(Path.Combine(foreign.Root, "keep.txt"), "Foreign file");
        await Assert.ThrowsAsync<IOException>(() => foreign.ActivateAsync(lease, a)); Assert.Equal("Foreign file", await File.ReadAllTextAsync(Path.Combine(foreign.Root, "keep.txt")));
        var install = new Installation(Path.Combine(test.Parent, "program")); await install.ActivateAsync(lease, a); using var other = new TestWorkspace(); using var otherLease = WorkspaceLease.Acquire(other.Workspace);
        await Assert.ThrowsAsync<IOException>(() => install.UninstallAsync(otherLease, true));
        await Assert.ThrowsAsync<IOException>(() => new Installation(Path.Combine(test.Workspace.Root, "program")).ActivateAsync(lease, a));
    }
    internal static async Task<string> PackageAsync(TestWorkspace test, string revision)
    {
        var root = Path.Combine(test.Parent, "source-package-" + revision);
        foreach (var relative in new[] { "README.md", "THIRD-PARTY-NOTICES.md", "app/CommuteCast.Desktop.runtimeconfig.json", "app/CommuteCast.Maintenance.runtimeconfig.json", "services/compose.yaml", "services/speech/requirements.lock.txt", "services/speech/model-checksums.txt" })
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, "Synthetic package " + revision);
        }
        foreach (var name in new[] { "CommuteCast.Desktop.exe", "CommuteCast.Maintenance.exe", "CommuteCast.Core.dll", "coreclr.dll" }) File.Copy(typeof(Job).Assembly.Location, Path.Combine(root, "app", name));
        await ReleasePackage.SealAsync(root); return root;
    }
    [Fact] public async Task FailedTransactionalMigrationRecoversOldReleaseAndLegacyQueue()
    {
        using var test = new TestWorkspace(); var original = await SeedAsync(test, "Legacy queued source");
        var a = await PackageAsync(test, "a"); var b = await PackageAsync(test, "b");
        using var lease = WorkspaceLease.Acquire(test.Workspace); var root = Path.Combine(test.Parent, "program");
        var first = await new Installation(root).ActivateAsync(lease, a);
        await ExecuteSqlAsync(test, "DROP TABLE hosted_auditions; DROP TABLE audition_ownership; DROP TABLE schema_history; PRAGMA application_id=0; PRAGMA user_version=0;");
        var install = new Installation(root, new MigrationFailure());
        await Assert.ThrowsAsync<IOException>(() => install.ActivateAsync(lease, b));
        Assert.Equal(0, await SqliteSchema.ValidateDatabaseAsync(Path.Combine(test.Workspace.Root, "queue.db")));
        Assert.True(await install.RecoverAsync(lease));
        Assert.Equal(first.State.CurrentPackageId, (await install.InspectAsync(lease)).State.CurrentPackageId);
        Assert.Equal(0, await SqliteSchema.ValidateDatabaseAsync(Path.Combine(test.Workspace.Root, "queue.db")));
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(Assert.Single(await new SqliteJobStore(test.Workspace).LoadAsync())));
        Assert.True(File.Exists(test.Workspace.ChunkPath(original, 0)));
    }
    [Fact] public async Task NewerQueueSchemaRefusesUpdateAndPreservesActivePointer()
    {
        using var test = new TestWorkspace(); await SeedAsync(test, "Future records"); var a = await PackageAsync(test, "a"); var b = await PackageAsync(test, "b");
        using var lease = WorkspaceLease.Acquire(test.Workspace); var install = new Installation(Path.Combine(test.Parent, "program")); await install.ActivateAsync(lease, a);
        var stateHash = await Workspace.HashFileAsync(Path.Combine(install.Root, "installation.json"));
        await ExecuteSqlAsync(test, "PRAGMA user_version=999;"); var database = Path.Combine(test.Workspace.Root, "queue.db"); var databaseHash = await Workspace.HashFileAsync(database);
        await Assert.ThrowsAsync<IOException>(() => install.ActivateAsync(lease, b));
        Assert.Equal(stateHash, await Workspace.HashFileAsync(Path.Combine(install.Root, "installation.json")));
        Assert.Equal(databaseHash, await Workspace.HashFileAsync(database)); Assert.False(install.HasPendingOperation);
    }
    [Fact] public async Task SchemaOneReleaseCannotActivateOverIdentityAwareSchemaTwoState()
    {
        using var test = new TestWorkspace(); await SeedAsync(test, "Identity-aware queued state");
        var current = await PackageAsync(test, "current-schema-two"); var older = await SchemaOnePackageAsync(test);
        using var lease = WorkspaceLease.Acquire(test.Workspace); var install = new Installation(Path.Combine(test.Parent, "program"));
        await install.ActivateAsync(lease, current);
        var pointer = await Workspace.HashFileAsync(Path.Combine(install.Root, "installation.json"));
        var database = Path.Combine(test.Workspace.Root, "queue.db"); var hash = await Workspace.HashFileAsync(database);
        var error = await Assert.ThrowsAsync<IOException>(() => install.ActivateAsync(lease, older));
        Assert.Contains("does not support", error.Message); Assert.Equal(pointer, await Workspace.HashFileAsync(Path.Combine(install.Root, "installation.json")));
        Assert.Equal(hash, await Workspace.HashFileAsync(database)); Assert.False(install.HasPendingOperation);
    }
    [Fact] public async Task RollbackRestoresCompatibleSchemaOneSnapshotAndUndoRestoresSchemaTwo()
    {
        using var test = new TestWorkspace(); var original = await SeedAsync(test, "Earlier frozen schema-one state");
        await ExecuteSqlAsync(test, "DROP TABLE hosted_auditions; DROP TABLE audition_ownership; DELETE FROM schema_history WHERE version>1; PRAGMA user_version=1;");
        var older = await SchemaOnePackageAsync(test); var current = await PackageAsync(test, "current-schema-two");
        using var lease = WorkspaceLease.Acquire(test.Workspace); var install = new Installation(Path.Combine(test.Parent, "program"));
        var oldRelease = await install.ActivateAsync(lease, older);
        var upgraded = await install.ActivateAsync(lease, current);
        Assert.Equal(SqliteSchema.CurrentVersion, await SqliteSchema.ValidateDatabaseAsync(Path.Combine(test.Workspace.Root, "queue.db")));
        var rolledBack = await install.RollbackAsync(lease);
        Assert.Equal(oldRelease.State.CurrentPackageId, rolledBack.State.CurrentPackageId);
        Assert.Equal(1, await SqliteSchema.ValidateDatabaseAsync(Path.Combine(test.Workspace.Root, "queue.db")));
        // Do not open the old snapshot through the newer job store before undo:
        // opening would deliberately upgrade it again.
        Assert.True(File.Exists(test.Workspace.ChunkPath(original, 0)));
        var undone = await install.RollbackAsync(lease);
        Assert.Equal(upgraded.State.CurrentPackageId, undone.State.CurrentPackageId);
        Assert.Equal(SqliteSchema.CurrentVersion, await SqliteSchema.ValidateDatabaseAsync(Path.Combine(test.Workspace.Root, "queue.db")));
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(Assert.Single(await new SqliteJobStore(test.Workspace).LoadAsync())));
    }
    private static async Task<string> SchemaOnePackageAsync(TestWorkspace test)
        => await LegacySchemaPackageAsync(test, 1);
    private static async Task<string> LegacySchemaPackageAsync(TestWorkspace test, int maximumSchema)
    {
        var path = await PackageAsync(test, "older-schema-" + maximumSchema);
        var manifest = (await ReleasePackage.ValidateAsync(path)) with { MaximumSchema = maximumSchema };
        // Produce the documented legacy manifest identity with its older capability.
        var identity = Job.Hash(JsonSerializer.Serialize(new { manifest.AppVersion, manifest.DesktopBuild, manifest.MaintenanceBuild,
            manifest.BundledRuntime, manifest.Target, manifest.MinimumSchema, manifest.MaximumSchema, manifest.ProviderContract, manifest.Files }));
        await File.WriteAllTextAsync(Path.Combine(path, ReleasePackage.ManifestName), JsonSerializer.Serialize(manifest with { PackageId = identity }));
        Assert.Equal(maximumSchema, (await ReleasePackage.ValidateAsync(path)).MaximumSchema); return path;
    }
    [Theory] [InlineData(2)] [InlineData(3)] [InlineData(5)]
    public async Task OlderReleaseCannotActivateOverPreviewOwnershipAwareState(int maximumSchema)
    {
        using var test = new TestWorkspace(); await SeedAsync(test, "Schema-four state");
        var current = await PackageAsync(test, "current-schema-four"); var older = await LegacySchemaPackageAsync(test, maximumSchema);
        using var lease = WorkspaceLease.Acquire(test.Workspace); var install = new Installation(Path.Combine(test.Parent, "program"));
        await install.ActivateAsync(lease, current);
        var pointer = await Workspace.HashFileAsync(Path.Combine(install.Root, "installation.json"));
        var database = Path.Combine(test.Workspace.Root, "queue.db"); var hash = await Workspace.HashFileAsync(database);
        await Assert.ThrowsAsync<IOException>(() => install.ActivateAsync(lease, older));
        Assert.Equal(pointer, await Workspace.HashFileAsync(Path.Combine(install.Root, "installation.json")));
        Assert.Equal(hash, await Workspace.HashFileAsync(database)); Assert.False(install.HasPendingOperation);
    }
    [Theory] [InlineData(InstallationCheckpoint.Prepared)] [InlineData(InstallationCheckpoint.Activated)]
    public async Task UnrecordedFileDuringUninstallIsPreservedAndNeverSwept(InstallationCheckpoint checkpoint)
    {
        using var test = new TestWorkspace(); await SeedAsync(test, "Private records"); var a = await PackageAsync(test, "a");
        using var lease = WorkspaceLease.Acquire(test.Workspace); var install = new Installation(Path.Combine(test.Parent, "program")); var first = await install.ActivateAsync(lease, a);
        var unexpected = Path.Combine(Path.GetDirectoryName(first.Executable)!, "unexpected.txt");
        await Assert.ThrowsAsync<IOException>(() => install.UninstallAsync(lease, true, new Observer(async (stage, _, _) => { if (stage == checkpoint) await File.WriteAllTextAsync(unexpected, "Keep new file"); })));
        if (checkpoint == InstallationCheckpoint.Prepared) Assert.True(await install.RecoverAsync(lease));
        else await Assert.ThrowsAsync<IOException>(() => install.RecoverAsync(lease));
        Assert.Equal("Keep new file", await File.ReadAllTextAsync(unexpected)); Assert.True(File.Exists(first.Executable)); Assert.True(File.Exists(Path.Combine(test.Workspace.Root, "queue.db")));
    }
    [Fact] public async Task ChangedActivationPointerDuringRecoveryIsRefusedWithoutReplacingPrivateState()
    {
        using var test = new TestWorkspace(); await SeedAsync(test, "Original records"); var a = await PackageAsync(test, "a"); var b = await PackageAsync(test, "b");
        using var lease = WorkspaceLease.Acquire(test.Workspace); var install = new Installation(Path.Combine(test.Parent, "program")); await install.ActivateAsync(lease, a);
        await Assert.ThrowsAsync<IOException>(() => install.ActivateAsync(lease, b, new Observer((stage, _, _) => stage == InstallationCheckpoint.Prepared ? throw new IOException("Stop") : Task.CompletedTask)));
        await File.AppendAllTextAsync(Path.Combine(install.Root, "installation.json"), " ");
        await File.WriteAllTextAsync(Path.Combine(test.Workspace.Root, "draft.json"), "Preserve changed state");
        await Assert.ThrowsAsync<IOException>(() => install.RecoverAsync(lease));
        Assert.True(install.HasPendingOperation); Assert.Equal("Preserve changed state", await File.ReadAllTextAsync(Path.Combine(test.Workspace.Root, "draft.json")));
    }
    private static async Task ExecuteSqlAsync(TestWorkspace test, string sql)
    {
        await using var connection = new SqliteConnection(SqliteSchema.ConnectionString(Path.Combine(test.Workspace.Root, "queue.db")));
        await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task StagingMoveWaitsForTransientLockAndPreservesOldReleaseOnPersistentLock(bool persistent)
    {
        using var test = new TestWorkspace(); var original = await SeedAsync(test, "Keep frozen queue"); var a = await PackageAsync(test, "a"); var b = await PackageAsync(test, "b");
        using var lease = WorkspaceLease.Acquire(test.Workspace); var install = new Installation(Path.Combine(test.Parent, "program")); var first = await install.ActivateAsync(lease, a);
        FileStream? locked = null; Task? release = null;
        var observer = new Observer((stage, item, _) => {
            if (stage == InstallationCheckpoint.StageVerified)
            {
                locked = new FileStream(Path.Combine(item!, "README.md"), FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!persistent) release = Task.Run(async () => { await Task.Delay(200); locked.Dispose(); });
            }
            return Task.CompletedTask;
        });
        try
        {
            if (persistent)
            {
                var error = await Record.ExceptionAsync(() => install.ActivateAsync(lease, b, observer));
                Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
                Assert.Equal(first.State.CurrentPackageId, (await install.InspectAsync(lease)).State.CurrentPackageId); Assert.False(install.HasPendingOperation);
            }
            else Assert.NotEqual(first.State.CurrentPackageId, (await install.ActivateAsync(lease, b, observer)).State.CurrentPackageId);
            Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(Assert.Single(await new SqliteJobStore(test.Workspace).LoadAsync())));
        }
        finally { if (release is not null) await release; locked?.Dispose(); }
    }
    private sealed class MigrationFailure : ISchemaMigrationObserver
    { public Task BeforeCommitAsync(int fromVersion, int toVersion, CancellationToken ct) => throw new IOException("Injected migration transaction failure"); }
    internal static async Task<Job> SeedAsync(TestWorkspace test, string source)
    {
        var job = new Job { Source = source, Title = source, Prepared = TextPreparation.Prepare(source), Destination = test.Destination, Stage = JobStage.Queued, Settings = new("piper", "en_US-lessac-medium", 1.1, false, "API=A P I", "frozen-provider-fixture") };
        job.Chunks = Chunker.Split(job.Prepared.Script, 450); Directory.CreateDirectory(test.Workspace.JobDirectory(job.Id)); TestWorkspace.WriteWave(test.Workspace.ChunkPath(job, 0));
        job.Receipts.Add(new(0, await Workspace.HashFileAsync(test.Workspace.ChunkPath(job, 0)), job.Fingerprint, 1)); job.CompletedChunks = 1;
        await new SqliteJobStore(test.Workspace).SaveAsync(job); await test.Workspace.SaveSettingsAsync(new() { QueuePaused = true, Engine = "piper", Voice = "en_US-lessac-medium" });
        await new DraftStore(test.Workspace).SaveAsync(new("Saved draft", source)); await File.WriteAllTextAsync(Path.Combine(test.Workspace.Root, "provider-lock.local.json"), "{\"ImageId\":\"sha256:" + new string('a', 64) + "\",\"Contract\":1}");
        return job;
    }
    private sealed class Observer(Func<InstallationCheckpoint, string?, CancellationToken, Task> reached) : IInstallationObserver
    { public Task ReachedAsync(InstallationCheckpoint checkpoint, string? item, CancellationToken ct) => reached(checkpoint, item, ct); }
}
