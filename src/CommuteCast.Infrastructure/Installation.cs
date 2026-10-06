using System.Text.Json;
using System.Text.RegularExpressions;

namespace CommuteCast.Infrastructure;

public record InstallationOwner(int FormatVersion, string InstallationId, string WorkspaceRoot);
public record InstallationSnapshot(string BackupId, string ManifestHash);
public record InstallationPrevious(string PackageId, InstallationSnapshot Snapshot);
public record InstallationState(int FormatVersion, string InstallationId, string? CurrentPackageId, string? AppVersion,
    InstallationPrevious? Previous, IReadOnlyList<string> KnownPackages, DateTimeOffset ChangedUtc);
public record InstallationResult(InstallationState State, string? Executable, bool AlreadyCurrent = false);
public enum InstallationCheckpoint { BackupCreated, StageVerified, PackageStaged, Prepared, StateMigrated, StateRestored, BeforeActivation, Activated, BeforeRemoval, ItemRemoved }
public interface IInstallationObserver { Task ReachedAsync(InstallationCheckpoint checkpoint, string? item, CancellationToken ct); }
internal record RemovalScope(string Kind, string Name, bool Directory, IReadOnlyList<ReleaseFile> Files);
internal record InstallationJournal(int FormatVersion, string Id, string Kind, string? OldStateHash, InstallationState Proposed,
    InstallationSnapshot? RecoverySnapshot, IReadOnlyList<RemovalScope> Removal);

/// <summary>Deliberate offline per-user installation. A caller holds the workspace lease and compatibility desktop mutex.</summary>
public sealed class Installation
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly string[] PrivateNames = ["queue.db", "queue.db-wal", "queue.db-shm", "queue.db-journal", "settings.json", "draft.json",
        "provider-lock.local.json", "recovery-kokoro.json", "recovery-piper.json", "jobs", "backups", "schema-backups", "recovery", "audition.wav", "last-shutdown-error.json"];
    private static readonly string[] PrivateDirectories = ["jobs", "backups", "schema-backups", "recovery"];
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "CommuteCast");
    public string Root { get; }
    private readonly ISchemaMigrationObserver? migrationObserver;
    private string OwnerPath => Path.Combine(Root, "installation.owner.json");
    private string StatePath => Path.Combine(Root, "installation.json");
    private string JournalPath => Path.Combine(Root, "deployment.pending.json");
    public Installation(string? root = null, ISchemaMigrationObserver? migrationObserver = null)
    {
        this.migrationObserver = migrationObserver;
        Root = Path.GetFullPath(root ?? DefaultRoot);
        if (Root.StartsWith("\\\\", StringComparison.Ordinal) || Root.TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetPathRoot(Root)!.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            new DriveInfo(Path.GetPathRoot(Root)!).DriveType == DriveType.Network) throw new IOException("Use a dedicated local per-user installation folder, not a volume root or network share.");
        Workspace.RejectReparsePoints(Root);
    }
    private static bool Id(string? id) => Regex.IsMatch(id ?? "", "^[a-f0-9]{32}$", RegexOptions.CultureInvariant);
    private static bool Hash(string? hash) => Regex.IsMatch(hash ?? "", "^[A-F0-9]{64}$", RegexOptions.CultureInvariant);
    private string PackagePath(string id)
    {
        if (!Hash(id)) throw new IOException("A recorded package identity is invalid. Installation files were preserved.");
        var path = Path.Combine(Root, "releases", id); SqliteSchema.RejectLink(path); return path;
    }
    private async Task<T?> ReadAsync<T>(string path, int limit, CancellationToken ct)
    {
        SqliteSchema.RejectLink(path); if (!File.Exists(path)) return default;
        if (new FileInfo(path).Length > limit) throw new IOException("Installation records exceed their safe limit. Preserve files for inspection.");
        try { return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, ct)) ?? throw new JsonException(); }
        catch (JsonException error) { throw new IOException("Installation records are unreadable. Preserve files for inspection.", error); }
    }
    private WorkspaceLease Acquire(WorkspaceLease workspaceLease)
    {
        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
            throw new IOException("This installation package supports Windows x64. No deployment state was changed.");
        workspaceLease.EnsureHeld(); GuardSeparation(workspaceLease.Workspace.Root);
        return WorkspaceLease.Acquire(new Workspace(Root));
    }
    private void GuardSeparation(string workspaceRoot)
    {
        if (workspaceRoot.StartsWith("\\\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(workspaceRoot)!).DriveType == DriveType.Network)
            throw new IOException("Private installation data must use local nonsynced storage.");
        if (Workspace.IsWithin(Root, workspaceRoot) || Workspace.IsWithin(workspaceRoot, Root)) throw new IOException("Installation binaries and private data must have separate, nonoverlapping roots.");
        Workspace.RejectReparsePoints(workspaceRoot); Workspace.RejectReparsePoints(Root);
    }
    private async Task<InstallationOwner> OwnerAsync(WorkspaceLease lease, bool create, CancellationToken ct)
    {
        var owner = await ReadAsync<InstallationOwner>(OwnerPath, 65536, ct);
        if (owner is null)
        {
            if (!create) throw new IOException("This is not an owned CommuteCast installation.");
            if (Directory.EnumerateFileSystemEntries(Root).Any(p => Path.GetFileName(p) != "instance.lease")) throw new IOException("The installation folder contains unrecognized files. Choose a separate empty folder; existing files were preserved.");
            owner = new(1, Guid.NewGuid().ToString("N"), lease.Workspace.Root);
            await Workspace.AtomicWriteAsync(OwnerPath, JsonSerializer.Serialize(owner, Json));
        }
        if (owner.FormatVersion != 1 || !Id(owner.InstallationId) || owner.WorkspaceRoot is null || !Path.IsPathFullyQualified(owner.WorkspaceRoot) ||
            !Path.GetFullPath(owner.WorkspaceRoot).Equals(lease.Workspace.Root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Installation ownership or its bound private workspace differs. Files were preserved.");
        return owner;
    }
    private void CheckState(InstallationState state, InstallationOwner owner)
    {
        if (state.FormatVersion != 1 || state.InstallationId != owner.InstallationId || state.KnownPackages is null || state.KnownPackages.Count > 1000 ||
            state.KnownPackages.Any(p => !Hash(p)) || state.KnownPackages.Distinct(StringComparer.Ordinal).Count() != state.KnownPackages.Count ||
            state.CurrentPackageId is null && state.AppVersion is not null ||
            state.CurrentPackageId is not null && (!Hash(state.CurrentPackageId) || !state.KnownPackages.Contains(state.CurrentPackageId) || string.IsNullOrWhiteSpace(state.AppVersion) || state.AppVersion.Length > 64) ||
            state.Previous is not null && (!Hash(state.Previous.PackageId) || !state.KnownPackages.Contains(state.Previous.PackageId) || !Id(state.Previous.Snapshot?.BackupId) || !Hash(state.Previous.Snapshot?.ManifestHash)))
            throw new IOException("Installation state is incompatible. Files were preserved.");
    }
    private async Task<InstallationState?> StateAsync(InstallationOwner owner, CancellationToken ct)
    { var state = await ReadAsync<InstallationState>(StatePath, 1024 * 1024, ct); if (state is not null) CheckState(state, owner); return state; }
    private static void Compatible(ReleaseManifest package, int schema)
    { if (schema < package.MinimumSchema || schema > package.MaximumSchema) throw new IOException("The selected release does not support the saved queue schema. No activation or reset was performed."); }
    private async Task<InstallationSnapshot> SnapshotAsync(WorkspaceLease lease, CancellationToken ct)
    {
        var path = await WorkspaceBackup.CreateAsync(lease, ct); var manifest = await WorkspaceBackup.ValidateAsync(path, ct);
        return new(manifest.BackupId, await Workspace.HashFileAsync(Path.Combine(path, "manifest.json"), ct));
    }
    private static async Task<(string Path, BackupManifest Manifest)> VerifySnapshotAsync(WorkspaceLease lease, InstallationSnapshot snapshot, CancellationToken ct)
    {
        if (!Id(snapshot.BackupId) || !Hash(snapshot.ManifestHash)) throw new IOException("A rollback snapshot identity is invalid. Files were preserved.");
        var path = Path.Combine(lease.Workspace.Root, "backups", snapshot.BackupId);
        SqliteSchema.RejectLink(Path.Combine(path, "manifest.json"));
        if (await Workspace.HashFileAsync(Path.Combine(path, "manifest.json"), ct) != snapshot.ManifestHash) throw new IOException("The rollback snapshot inventory changed. It was not restored.");
        var manifest = await WorkspaceBackup.ValidateAsync(path, ct);
        if (manifest.BackupId != snapshot.BackupId) throw new IOException("The rollback snapshot identity differs from its recorded folder.");
        return (path, manifest);
    }
    private async Task SaveJournalAsync(InstallationJournal journal)
    {
        var text = JsonSerializer.Serialize(journal, Json);
        if (System.Text.Encoding.UTF8.GetByteCount(text) > 32 * 1048576) throw new IOException("The deployment journal exceeds its bounded limit. No activation or deletion was committed.");
        await Workspace.AtomicWriteAsync(JournalPath, text);
    }
    private static async Task Observe(IInstallationObserver? observer, InstallationCheckpoint checkpoint, CancellationToken ct, string? item = null)
    { if (observer is not null) await observer.ReachedAsync(checkpoint, item, ct); }
    private static IEnumerable<string> Files(string root)
    {
        SqliteSchema.RejectLink(root); if (!Directory.Exists(root)) yield break;
        var pending = new Stack<string>(); pending.Push(root); var count = 0;
        while (pending.Count > 0)
            foreach (var path in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                if (++count > 200000) throw new IOException("Managed storage contains too many entries to inspect safely.");
                Workspace.RejectFileReparsePoint(path);
                if (Directory.Exists(path)) pending.Push(path); else yield return Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            }
    }
    private static async Task CopyAsync(string sourceRoot, string targetRoot, ReleaseFile file, CancellationToken ct)
    {
        var inputPath = OwnedFileRemoval.Resolve(sourceRoot, file.RelativePath); var outputPath = OwnedFileRemoval.Resolve(targetRoot, file.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough);
        if (input.Length != file.Bytes) throw new IOException("The release changed while staging. Existing installation was retained.");
        await input.CopyToAsync(output, ct); output.Flush(true);
    }
    public async Task<InstallationResult> ActivateAsync(WorkspaceLease lease, string source, IInstallationObserver? observer = null, CancellationToken ct = default)
    {
        source = Path.GetFullPath(source);
        ct.ThrowIfCancellationRequested(); using var installationLease = Acquire(lease);
        var package = await ReleasePackage.ValidateAsync(source, ct); var owner = await OwnerAsync(lease, true, ct);
        await RecoverCoreAsync(lease, owner, ct); var old = await StateAsync(owner, ct);
        if (old?.CurrentPackageId == package.PackageId)
        {
            await ReleasePackage.ValidateAsync(PackagePath(package.PackageId), ct);
            var database = Path.Combine(lease.Workspace.Root, "queue.db"); if (File.Exists(database)) Compatible(package, await SqliteSchema.ValidateDatabaseAsync(database, ct));
            return new(old, Path.Combine(PackagePath(package.PackageId), "app", "CommuteCast.Desktop.exe"), true);
        }
        if (old?.CurrentPackageId is not null) await ReleasePackage.ValidateAsync(PackagePath(old.CurrentPackageId), ct);
        var snapshot = await SnapshotAsync(lease, ct); var backup = await VerifySnapshotAsync(lease, snapshot, ct); Compatible(package, backup.Manifest.SchemaVersion);
        await Observe(observer, InstallationCheckpoint.BackupCreated, ct);
        var required = checked(package.Files.Sum(f => f.Bytes) + 64 * 1048576L);
        if (new DriveInfo(Path.GetPathRoot(Root)!).AvailableFreeSpace < required) throw new IOException("There is insufficient free space to stage a verified release. Current files were retained.");
        var destination = PackagePath(package.PackageId);
        if (!Directory.Exists(destination))
        {
            var stage = Path.Combine(Root, "staging", Guid.NewGuid().ToString("N")); SqliteSchema.RejectLink(stage); Directory.CreateDirectory(stage);
            foreach (var file in package.Files) await CopyAsync(source, stage, file, ct);
            var manifestPath = Path.Combine(source, ReleasePackage.ManifestName);
            await CopyAsync(source, stage, new(ReleasePackage.ManifestName, new FileInfo(manifestPath).Length, await Workspace.HashFileAsync(manifestPath, ct)), ct);
            var staged = await ReleasePackage.ValidateAsync(stage, ct);
            if (staged.PackageId != package.PackageId) throw new IOException("The release changed while staging. Current installation was retained.");
            await Observe(observer, InstallationCheckpoint.StageVerified, ct, stage);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); await MoveStageAsync(stage, destination, ct);
        }
        else if ((await ReleasePackage.ValidateAsync(destination, ct)).PackageId != package.PackageId) throw new IOException("The stored release differs from its identity. It was preserved.");
        await Observe(observer, InstallationCheckpoint.PackageStaged, ct);
        var proposed = new InstallationState(1, owner.InstallationId, package.PackageId, package.AppVersion,
            old?.CurrentPackageId is null ? null : new(old.CurrentPackageId, snapshot), (old?.KnownPackages ?? []).Append(package.PackageId).Distinct(StringComparer.Ordinal).ToArray(), DateTimeOffset.UtcNow);
        CheckState(proposed, owner);
        var journal = new InstallationJournal(1, Guid.NewGuid().ToString("N"), "Activate", File.Exists(StatePath) ? await Workspace.HashFileAsync(StatePath, ct) : null, proposed, snapshot, []);
        await SaveJournalAsync(journal); await Observe(observer, InstallationCheckpoint.Prepared, ct);
        if (backup.Manifest.SchemaVersion < SqliteSchema.CurrentVersion && package.MaximumSchema >= SqliteSchema.CurrentVersion) await new SqliteJobStore(lease.Workspace, migrationObserver).LoadAsync(ct);
        await Observe(observer, InstallationCheckpoint.StateMigrated, ct);
        Compatible(package, await SqliteSchema.ValidateDatabaseAsync(Path.Combine(lease.Workspace.Root, "queue.db"), ct));
        await Observe(observer, InstallationCheckpoint.BeforeActivation, ct); ct.ThrowIfCancellationRequested();
        await Workspace.AtomicWriteAsync(StatePath, JsonSerializer.Serialize(proposed, Json));
        await Observe(observer, InstallationCheckpoint.Activated, CancellationToken.None); File.Delete(JournalPath);
        return new(proposed, Path.Combine(destination, "app", "CommuteCast.Desktop.exe"));
    }
    private static async Task MoveStageAsync(string stage, string destination, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested(); SqliteSchema.RejectLink(stage); SqliteSchema.RejectLink(destination);
            try { Directory.Move(stage, destination); return; }
            catch (IOException error) when (attempt < 5 && (error.HResult & 0xFFFF) is 5 or 32 or 33)
            { await Task.Delay(50 << attempt, ct); }
            catch (UnauthorizedAccessException) when (attempt < 5)
            { await Task.Delay(50 << attempt, ct); }
        }
    }
    public async Task<InstallationResult> RollbackAsync(WorkspaceLease lease, IInstallationObserver? observer = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var installationLease = Acquire(lease); var owner = await OwnerAsync(lease, false, ct);
        await RecoverCoreAsync(lease, owner, ct); var old = await StateAsync(owner, ct);
        if (old?.CurrentPackageId is null || old.Previous is null) throw new IOException("No previous release and compatible state snapshot are recorded for rollback.");
        var package = await ReleasePackage.ValidateAsync(PackagePath(old.Previous.PackageId), ct);
        var prior = await VerifySnapshotAsync(lease, old.Previous.Snapshot, ct); Compatible(package, prior.Manifest.SchemaVersion);
        var recovery = await SnapshotAsync(lease, ct); await Observe(observer, InstallationCheckpoint.BackupCreated, ct);
        var proposed = old with { CurrentPackageId = old.Previous.PackageId, AppVersion = package.AppVersion, Previous = new(old.CurrentPackageId, recovery), ChangedUtc = DateTimeOffset.UtcNow };
        var journal = new InstallationJournal(1, Guid.NewGuid().ToString("N"), "Rollback", await Workspace.HashFileAsync(StatePath, ct), proposed, recovery, []);
        await SaveJournalAsync(journal); await Observe(observer, InstallationCheckpoint.Prepared, ct);
        await WorkspaceBackup.RestoreAsync(lease, prior.Path, ct: ct); await Observe(observer, InstallationCheckpoint.StateRestored, ct);
        await Observe(observer, InstallationCheckpoint.BeforeActivation, ct); ct.ThrowIfCancellationRequested();
        await Workspace.AtomicWriteAsync(StatePath, JsonSerializer.Serialize(proposed, Json));
        await Observe(observer, InstallationCheckpoint.Activated, CancellationToken.None); File.Delete(JournalPath);
        return new(proposed, Path.Combine(PackagePath(package.PackageId), "app", "CommuteCast.Desktop.exe"));
    }
    private string ScopeRoot(WorkspaceLease lease, RemovalScope scope)
    {
        if (scope.Kind == "Package") return PackagePath(scope.Name);
        if (scope.Kind == "Private" && PrivateNames.Contains(scope.Name, StringComparer.Ordinal)) return Path.Combine(lease.Workspace.Root, scope.Name);
        throw new IOException("The uninstall journal names an unrecognized removal scope. Files were preserved.");
    }
    private static async Task<RemovalScope> CaptureScopeAsync(string kind, string name, string path, CancellationToken ct)
    {
        SqliteSchema.RejectLink(path); var directory = Directory.Exists(path); var files = new List<ReleaseFile>();
        foreach (var relative in directory ? Files(path) : new[] { Path.GetFileName(path) })
        {
            ct.ThrowIfCancellationRequested(); var candidate = directory ? OwnedFileRemoval.Resolve(path, relative) : path;
            files.Add(new(relative, new FileInfo(candidate).Length, await Workspace.HashFileAsync(candidate, ct)));
            if (files.Count > 100000) throw new IOException("There are too many private files for a bounded uninstall journal.");
        }
        return new(kind, name, directory, files);
    }
    private async Task VerifyRemovalAsync(WorkspaceLease lease, IReadOnlyList<RemovalScope> scopes, CancellationToken ct)
    {
        foreach (var scope in scopes)
        {
            var path = ScopeRoot(lease, scope); SqliteSchema.RejectLink(path);
            if (!File.Exists(path) && !Directory.Exists(path)) continue;
            if (Directory.Exists(path) != scope.Directory) throw new IOException("A removal item's shape changed. Files were preserved.");
            var actual = scope.Directory ? Files(path).ToArray() : new[] { scope.Name };
            if (actual.Any(relative => !scope.Files.Any(f => f.RelativePath == relative))) throw new IOException("Uninstall found unrecorded files. They were preserved for inspection.");
            foreach (var file in scope.Files)
            {
                var candidate = scope.Directory ? OwnedFileRemoval.Resolve(path, file.RelativePath) : path;
                if (File.Exists(candidate) && (new FileInfo(candidate).Length != file.Bytes || await Workspace.HashFileAsync(candidate, ct) != file.Sha256)) throw new IOException("A removal file changed. It was preserved for inspection.");
            }
        }
    }
    private async Task RemoveCoreAsync(WorkspaceLease lease, IReadOnlyList<RemovalScope> scopes, IInstallationObserver? observer, CancellationToken ct)
    {
        await Observe(observer, InstallationCheckpoint.BeforeRemoval, ct); await VerifyRemovalAsync(lease, scopes, ct);
        foreach (var scope in scopes)
        {
            var path = ScopeRoot(lease, scope);
            foreach (var file in scope.Files)
            {
                ct.ThrowIfCancellationRequested();
                await OwnedFileRemoval.DeleteAsync(scope.Directory ? path : lease.Workspace.Root, file, ct: ct);
                await Observe(observer, InstallationCheckpoint.ItemRemoved, ct, scope.Kind + "/" + scope.Name + "/" + file.RelativePath);
            }
            if (scope.Directory) OwnedFileRemoval.RemoveEmptyDirectories(path);
        }
    }
    public async Task<InstallationResult> UninstallAsync(WorkspaceLease lease, bool removePrivateData, IInstallationObserver? observer = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var installationLease = Acquire(lease); var owner = await OwnerAsync(lease, false, ct);
        if (Workspace.IsWithin(Root, AppContext.BaseDirectory)) throw new IOException("Run uninstall from an extracted portable package outside the installation folder.");
        await RecoverCoreAsync(lease, owner, ct); var old = await StateAsync(owner, ct) ?? throw new IOException("No installed release is recorded.");
        var scopes = new List<RemovalScope>();
        foreach (var id in old.KnownPackages)
        {
            var path = PackagePath(id); if (!Directory.Exists(path)) continue;
            var package = await ReleasePackage.ValidateAsync(path, ct); if (package.PackageId != id) throw new IOException("A stored release differs from its recorded identity.");
            scopes.Add(await CaptureScopeAsync("Package", id, path, ct));
        }
        if (removePrivateData)
        {
            await WorkspaceBackup.RecoverInterruptedAsync(lease, ct);
            foreach (var name in PrivateNames)
            {
                var path = Path.Combine(lease.Workspace.Root, name);
                if (File.Exists(path) || Directory.Exists(path))
                {
                    if (Directory.Exists(path) != PrivateDirectories.Contains(name, StringComparer.Ordinal)) throw new IOException("A private removal item's shape is incompatible. Files were preserved.");
                    scopes.Add(await CaptureScopeAsync("Private", name, path, ct));
                }
            }
        }
        if (scopes.Sum(s => s.Files.Count) > 100000) throw new IOException("There are too many recorded files for one bounded uninstall operation.");
        var proposed = old with { CurrentPackageId = null, AppVersion = null, Previous = null, ChangedUtc = DateTimeOffset.UtcNow };
        var journal = new InstallationJournal(1, Guid.NewGuid().ToString("N"), "Uninstall", await Workspace.HashFileAsync(StatePath, ct), proposed, null, scopes);
        await SaveJournalAsync(journal); await Observe(observer, InstallationCheckpoint.Prepared, ct); await VerifyRemovalAsync(lease, scopes, ct);
        ct.ThrowIfCancellationRequested(); await Workspace.AtomicWriteAsync(StatePath, JsonSerializer.Serialize(proposed, Json));
        await Observe(observer, InstallationCheckpoint.Activated, CancellationToken.None);
        await RemoveCoreAsync(lease, scopes, observer, ct); File.Delete(JournalPath); return new(proposed, null);
    }
    private async Task<bool> RecoverCoreAsync(WorkspaceLease lease, InstallationOwner owner, CancellationToken ct)
    {
        await WorkspaceBackup.RecoverInterruptedAsync(lease, ct);
        var journal = await ReadAsync<InstallationJournal>(JournalPath, 32 * 1048576, ct); if (journal is null) return false;
        if (journal.FormatVersion != 1 || !Id(journal.Id) || journal.Kind is not ("Activate" or "Rollback" or "Uninstall") || journal.Proposed is null ||
            journal.OldStateHash is not null && !Hash(journal.OldStateHash) || journal.Removal is null || journal.Removal.Count > PrivateNames.Length + 1000 ||
            journal.Kind != "Uninstall" && (journal.Removal.Count != 0 || journal.RecoverySnapshot is null) || journal.Kind == "Uninstall" && journal.RecoverySnapshot is not null)
            throw new IOException("The deployment journal is incompatible. Files were preserved for inspection.");
        CheckState(journal.Proposed, owner);
        if (journal.Kind == "Uninstall" ? journal.Proposed.CurrentPackageId is not null || journal.Proposed.Previous is not null : journal.Proposed.CurrentPackageId is null ||
            !Id(journal.RecoverySnapshot?.BackupId) || !Hash(journal.RecoverySnapshot?.ManifestHash)) throw new IOException("The deployment intent is incompatible with its state. Files were preserved.");
        if (journal.Removal.Any(s => s is null || s.Files is null || s.Files.Any(f => f is null || f.Bytes < 0 || !Hash(f.Sha256))) || journal.Removal.Sum(s => s.Files.Count) > 100000 ||
            journal.Removal.Select(s => s.Kind + "/" + s.Name).Distinct(StringComparer.Ordinal).Count() != journal.Removal.Count) throw new IOException("The removal journal is incompatible. Files were preserved.");
        foreach (var scope in journal.Removal)
        {
            ScopeRoot(lease, scope);
            if (scope.Kind == "Package" && (!scope.Directory || !journal.Proposed.KnownPackages.Contains(scope.Name)) || scope.Kind == "Private" && scope.Directory != PrivateDirectories.Contains(scope.Name, StringComparer.Ordinal) ||
                !scope.Directory && (scope.Kind != "Private" || scope.Files.Count != 1 || scope.Files[0].RelativePath != scope.Name) ||
                scope.Files.Select(f => f.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != scope.Files.Count) throw new IOException("The removal scope differs from its recorded ownership.");
            foreach (var file in scope.Files) OwnedFileRemoval.Resolve(scope.Directory ? ScopeRoot(lease, scope) : lease.Workspace.Root, file.RelativePath);
        }
        var currentHash = File.Exists(StatePath) ? await Workspace.HashFileAsync(StatePath, ct) : null;
        var committed = currentHash == CommuteCast.Core.Job.Hash(JsonSerializer.Serialize(journal.Proposed, Json));
        if (!committed && currentHash != journal.OldStateHash) throw new IOException("Deployment state changed outside its journal. All files were preserved for inspection.");
        if (committed)
        {
            if (journal.Kind == "Uninstall") await RemoveCoreAsync(lease, journal.Removal, null, ct);
            else if (journal.Proposed.CurrentPackageId is not null) await ReleasePackage.ValidateAsync(PackagePath(journal.Proposed.CurrentPackageId), ct);
        }
        else if (journal.Kind is "Activate" or "Rollback")
        {
            var snapshot = await VerifySnapshotAsync(lease, journal.RecoverySnapshot!, ct); await WorkspaceBackup.RestoreAsync(lease, snapshot.Path, ct: ct);
        }
        File.Delete(JournalPath); return true;
    }
    public async Task<bool> RecoverAsync(WorkspaceLease lease, CancellationToken ct = default)
    { using var installationLease = Acquire(lease); var owner = await OwnerAsync(lease, false, ct); return await RecoverCoreAsync(lease, owner, ct); }
    public Task<InstallationResult> InspectAsync(WorkspaceLease lease, CancellationToken ct = default) => InspectCoreAsync(lease, true, ct);
    // Maintenance needs trusted active binaries even when the private queue needs repair.
    public Task<InstallationResult> InspectForMaintenanceAsync(WorkspaceLease lease, CancellationToken ct = default) => InspectCoreAsync(lease, false, ct);
    private async Task<InstallationResult> InspectCoreAsync(WorkspaceLease lease, bool validateQueue, CancellationToken ct)
    {
        using var installationLease = Acquire(lease); var owner = await OwnerAsync(lease, false, ct);
        if (File.Exists(JournalPath)) throw new IOException("Deployment is unfinished. Close the desktop and run recover-install before launching.");
        var state = await StateAsync(owner, ct) ?? throw new IOException("No release is recorded.");
        if (state.CurrentPackageId is null) return new(state, null);
        var package = await ReleasePackage.ValidateAsync(PackagePath(state.CurrentPackageId), ct);
        if (package.PackageId != state.CurrentPackageId || package.AppVersion != state.AppVersion) throw new IOException("The active release differs from its installation record.");
        var database = Path.Combine(lease.Workspace.Root, "queue.db"); if (validateQueue && File.Exists(database)) Compatible(package, await SqliteSchema.ValidateDatabaseAsync(database, ct));
        return new(state, Path.Combine(PackagePath(state.CurrentPackageId), "app", "CommuteCast.Desktop.exe"));
    }
    public static string? FindRoot(string applicationDirectory)
    {
        var app = new DirectoryInfo(Path.GetFullPath(applicationDirectory)); var package = app.Parent;
        // Windows launches may change path casing. The catalog identity remains canonical uppercase,
        // but recognizing an installed executable must use Windows path semantics.
        return app.Name.Equals("app", StringComparison.OrdinalIgnoreCase) && Hash(package?.Name.ToUpperInvariant()) && package?.Parent?.Name.Equals("releases", StringComparison.OrdinalIgnoreCase) == true ? package.Parent.Parent?.FullName : null;
    }
    public async Task<InstallationOwner> ReadOwnerAsync(CancellationToken ct = default)
    {
        var owner = await ReadAsync<InstallationOwner>(OwnerPath, 65536, ct);
        if (owner is null || owner.FormatVersion != 1 || !Id(owner.InstallationId) || owner.WorkspaceRoot is null || !Path.IsPathFullyQualified(owner.WorkspaceRoot)) throw new IOException("Installation ownership is missing or invalid.");
        GuardSeparation(Path.GetFullPath(owner.WorkspaceRoot)); return owner;
    }
    public bool HasPendingOperation
    { get { SqliteSchema.RejectLink(JournalPath); return File.Exists(JournalPath); } }
}
