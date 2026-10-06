using CommuteCast.Core;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace CommuteCast.Infrastructure;

public enum DeploymentAction { Install, Rollback, UninstallRetain, UninstallRemove, Recover }
public sealed class DeploymentReview
{
    internal DeploymentReview(string packageRoot, ReleaseManifest package, string installRoot, string privateRoot, InstallationOverview overview, PrivateState state)
    { PackageRoot = packageRoot; Package = package; InstallRoot = installRoot; PrivateRoot = privateRoot; Overview = overview; State = state; }
    public string PackageRoot { get; }
    public ReleaseManifest Package { get; }
    public string InstallRoot { get; }
    public string PrivateRoot { get; }
    public InstallationOverview Overview { get; }
    internal PrivateState State { get; }
    public int LocalFiles => State.Files;
    public long LocalBytes => State.Bytes;
    public long? Narrations => State.Narrations;
    public string QueueStatus => State.QueueStatus;
    public string Summary => $"Package {Package.AppVersion} · {Package.DesktopBuild}\nPackage ID: {Package.PackageId}\n{Package.Files.Count:N0} verified package files · {Package.Files.Sum(f => f.Bytes) / 1048576.0:0.0} MiB · schema {Package.MinimumSchema}–{Package.MaximumSchema}\n\nBinaries: {InstallRoot}\nPrivate local data: {PrivateRoot}\n" +
        (Overview.State?.CurrentPackageId is null ? "No active installed release." : "Current release: " + Overview.State.CurrentPackageId) +
        $"\n{(Narrations.HasValue ? $"{Narrations:N0} narration records" : "Narration count unavailable")} · {LocalFiles:N0} managed private files · {LocalBytes / 1048576.0:0.0} MiB\n{QueueStatus}\n" +
        (Overview.PendingRecovery ? "Pending " + Overview.PendingOperation + " deployment: recovery is required before install, rollback, removal or launch.\nRecovery journal: " + Overview.PendingHash : Overview.State?.Previous is null ? "No previous release snapshot is recorded." : "Rollback release: " + Overview.State.Previous.PackageId + "\nRollback snapshot: " + Overview.State.Previous.Snapshot.BackupId);
}
internal record PrivateState(string Fingerprint, int Files, long Bytes, long? Narrations, string QueueStatus);
public record DeploymentOutcome(InstallationResult? Installation, SetupReport? Setup, bool Recovered);

/// <summary>A reviewed, single-use offline deployment action. Preview/checks do not create private state.</summary>
public sealed class DeploymentSession(string packageRoot, string? installRoot = null, string? privateRoot = null, ISetupRuntime? setupRuntime = null)
{
    private readonly string source = Path.GetFullPath(packageRoot);
    private readonly Installation installation = new(installRoot);
    private DeploymentReview? reviewed;
    private int busy;
    public static string DefaultPrivateRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CommuteCast");
    private async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) throw new IOException("Another setup action is still running.");
        try { return await action(); } finally { Volatile.Write(ref busy, 0); }
    }
    private string ResolvePrivateRoot(InstallationOverview overview)
    {
        var root = Path.GetFullPath(overview.Owner?.WorkspaceRoot ?? privateRoot ?? DefaultPrivateRoot);
        if (privateRoot is not null && !Path.GetFullPath(privateRoot).Equals(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("The requested private folder differs from this installation's recorded binding.");
        if (root.StartsWith("\\\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(root)!).DriveType == DriveType.Network ||
            root.TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetPathRoot(root)!.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            Workspace.IsWithin(root, installation.Root) || Workspace.IsWithin(installation.Root, root) || Workspace.IsWithin(root, source) || Workspace.IsWithin(source, root))
            throw new IOException("Setup needs separate dedicated local binary, package and nonsynced private folders.");
        Workspace.RejectReparsePoints(root); return root;
    }
    private static async Task<PrivateState> InspectPrivateAsync(string root, CancellationToken ct)
    {
        Workspace.RejectReparsePoints(root); var entries = new List<object>(); int files = 0, visited = 0; long bytes = 0;
        async Task Inspect(string path)
        {
            ct.ThrowIfCancellationRequested(); SqliteSchema.RejectLink(path);
            if (++visited > 200000) throw new IOException("Managed private state exceeds the setup inspection limit. Existing files were preserved.");
            var relative = Path.GetRelativePath(root, path);
            if (Directory.Exists(path))
            {
                entries.Add(new { relative, directory = true });
                foreach (var child in Directory.EnumerateFileSystemEntries(path).Order(StringComparer.Ordinal)) await Inspect(child);
            }
            else if (File.Exists(path))
            {
                if (++files > 100000) throw new IOException("There are too many managed private files to review setup safely.");
                var size = new FileInfo(path).Length; bytes = checked(bytes + size); entries.Add(new { relative, size, hash = await Workspace.HashFileAsync(path, ct) });
            }
            else entries.Add(new { relative, missing = true });
        }
        foreach (var name in Installation.ManagedPrivateNames) await Inspect(Path.Combine(root, name));
        long? narrations = 0; var status = "No queue database is present."; var databasePath = Path.Combine(root, "queue.db");
        if (File.Exists(databasePath))
        {
            try
            {
                // Immutable inspection avoids SQLite creating WAL/SHM files. Hold the database
                // against writes and refuse journals: their uncheckpointed rows cannot be counted.
                using var held = new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (File.Exists(databasePath + "-wal") || File.Exists(databasePath + "-journal")) throw new IOException("Pending database journal");
                var uri = new Uri(databasePath).AbsoluteUri + "?immutable=1";
                await using var database = new SqliteConnection(SqliteSchema.ConnectionString(uri, SqliteOpenMode.ReadOnly)); await database.OpenAsync(ct);
                var schema = await SqliteSchema.ValidateDatabaseConnectionAsync(database, ct);
                await using var command = database.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM jobs"; narrations = Convert.ToInt64(await command.ExecuteScalarAsync(ct)); status = "Queue schema " + schema + " verified.";
            }
            catch (Exception error) when (error is IOException or SqliteException or JsonException or NotSupportedException)
            { narrations = null; status = "Queue verification unavailable (journal, lock, or incompatible data). Preserve files and use local maintenance or a recorded compatible rollback; install will not reset it."; }
        }
        return new(Job.Hash(JsonSerializer.Serialize(entries)), files, bytes, narrations, status);
    }
    internal static async Task VerifyPrivateReviewAsync(WorkspaceLease lease, string? fingerprint, CancellationToken ct)
    {
        if (fingerprint is not null && (await InspectPrivateAsync(lease.Workspace.Root, ct)).Fingerprint != fingerprint)
            throw new IOException("The reviewed private removal scope changed. Review it again before uninstalling; no removal was committed.");
    }
    public Task<DeploymentReview> ReviewAsync(CancellationToken ct = default) => RunAsync(async () =>
    {
        reviewed = null; var package = await ReleasePackage.ValidateAsync(source, ct); var overview = await installation.ReadOverviewAsync(ct); var root = ResolvePrivateRoot(overview);
        var state = await InspectPrivateAsync(root, ct); return reviewed = new(source, package, installation.Root, root, overview, state);
    });
    private void RequireReview(DeploymentReview review)
    { if (!ReferenceEquals(review, reviewed)) throw new IOException("Review this package and local installation again before choosing an action."); }
    public Task<SetupReport> CheckSetupAsync(DeploymentReview review, CancellationToken ct = default) => RunAsync(async () =>
    {
        RequireReview(review); var settings = await new Workspace(review.PrivateRoot, false).LoadSettingsAsync();
        return await new SetupDiagnostics(setupRuntime ?? new SetupRuntime(review.PrivateRoot)).CheckAsync(settings, ct);
    });
    public Task<DeploymentOutcome> ApplyAsync(DeploymentReview review, DeploymentAction action, bool confirmed, CancellationToken ct = default) => RunAsync(async () =>
    {
        if (!confirmed) throw new IOException("Confirm the reviewed setup action before changing binaries or local state.");
        RequireReview(review); if (!Enum.IsDefined(action)) throw new ArgumentException("Choose a supported setup action.");
        if (action is DeploymentAction.UninstallRetain or DeploymentAction.UninstallRemove && Workspace.IsWithin(installation.Root, source))
            throw new IOException("Run uninstall from a complete portable setup package outside the installation folder.");
        if (review.Overview.PendingRecovery && action != DeploymentAction.Recover) throw new IOException("Recover the pending deployment and review again before another setup action.");
        reviewed = null; ct.ThrowIfCancellationRequested();
        using var lease = WorkspaceLease.Acquire(new Workspace(review.PrivateRoot));
        var overview = await installation.ReadOverviewAsync(ct); var currentRoot = ResolvePrivateRoot(overview);
        if (currentRoot != review.PrivateRoot || overview.Owner != review.Overview.Owner || overview.StateHash != review.Overview.StateHash || overview.LauncherHash != review.Overview.LauncherHash || overview.RegistrationHash != review.Overview.RegistrationHash || overview.PendingHash != review.Overview.PendingHash || overview.PendingRecovery != review.Overview.PendingRecovery ||
            (await InspectPrivateAsync(currentRoot, ct)).Fingerprint != review.State.Fingerprint || (await ReleasePackage.ValidateAsync(source, ct)).PackageId != review.Package.PackageId)
            throw new IOException("The reviewed package, installation or private data changed. Nothing was applied; review it again.");
        try
        {
            if (action == DeploymentAction.Recover) return new DeploymentOutcome(null, null, await installation.RecoverAsync(lease, ct));
            SetupReport? setup = null;
            if (action == DeploymentAction.Install)
            {
                var database = Path.Combine(currentRoot, "queue.db");
                if (File.Exists(database)) await SqliteSchema.ValidateDatabaseAsync(database, ct);
                setup = await new SetupDiagnostics(setupRuntime ?? new SetupRuntime(currentRoot)).CheckAsync(await lease.Workspace.LoadSettingsAsync(), ct);
            }
            var result = action switch
            {
                DeploymentAction.Install => await installation.ActivateAsync(lease, source, ct: ct),
                DeploymentAction.Rollback => await installation.RollbackAsync(lease, ct: ct),
                DeploymentAction.UninstallRetain => await installation.UninstallAsync(lease, false, ct: ct, reviewedPrivateFingerprint: review.State.Fingerprint),
                DeploymentAction.UninstallRemove => await installation.UninstallAsync(lease, true, ct: ct, reviewedPrivateFingerprint: review.State.Fingerprint),
                _ => throw new ArgumentException("Choose a supported setup action.")
            };
            return new DeploymentOutcome(result, setup, false);
        }
        catch (Exception failure)
        {
            if (File.Exists(Path.Combine(installation.Root, "installation.owner.json")))
                try { await installation.RecoverAsync(lease, CancellationToken.None); }
                catch (Exception recovery) { throw new IOException("Setup and automatic recovery could not finish. Keep the recorded files and use deployment recovery before launching.", new AggregateException(failure, recovery)); }
            throw;
        }
    });
    public Task<string> FindLaunchAsync(DeploymentReview review, CancellationToken ct = default) => RunAsync(async () =>
    {
        RequireReview(review); using var lease = WorkspaceLease.Acquire(new Workspace(review.PrivateRoot));
        return (await installation.InspectAsync(lease, ct)).Executable ?? throw new IOException("No active release is installed. Install a verified package first.");
    });
}
