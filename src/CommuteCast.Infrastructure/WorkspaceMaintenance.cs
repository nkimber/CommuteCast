using Microsoft.Data.Sqlite;

namespace CommuteCast.Infrastructure;

public sealed class BackupReview
{
    internal BackupReview(string directory, BackupManifest manifest, string manifestHash, long jobs)
    { Directory = directory; Manifest = manifest; ManifestHash = manifestHash; Jobs = jobs; }
    public string Directory { get; }
    public BackupManifest Manifest { get; }
    internal string ManifestHash { get; }
    public long Jobs { get; }
    public string Summary => $"Backup created {Manifest.CreatedUtc.ToLocalTime():MMM d, yyyy · h:mm tt zzz}\n{Jobs:N0} narration records · {Manifest.Files.Count:N0} files · {Manifest.Files.Sum(f => f.Bytes) / 1048576.0:0.0} MiB\nApp {Manifest.AppVersion} · queue schema {Manifest.SchemaVersion}\nBackup ID: {Manifest.BackupId}\n{Directory}";
}

/// <summary>Offline maintenance under the application's still-held workspace lease. No worker may remain active.</summary>
public sealed class WorkspaceMaintenance(WorkspaceLease lease, IRestoreObserver? restoreObserver = null)
{
    private int busy;
    private BackupReview? reviewed;
    private async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) throw new IOException("Another local maintenance operation is still running.");
        try { lease.EnsureHeld(); return await action(); }
        finally { Volatile.Write(ref busy, 0); }
    }
    private static async Task<BackupReview> ReviewCoreAsync(string directory, CancellationToken ct)
    {
        directory = Path.GetFullPath(directory);
        if (directory.StartsWith("\\\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(directory)!).DriveType == DriveType.Network)
            throw new IOException("Choose a completed backup in local nonsynced storage.");
        Workspace.RejectReparsePoints(directory);
        if (Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a completed backup folder. Partial staging folders cannot be selected for restore.");
        var manifestPath = Path.Combine(directory, "manifest.json"); Workspace.RejectFileReparsePoint(manifestPath);
        if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 32 * 1048576) throw new IOException("A bounded, completed backup manifest is required.");
        var hash = await Workspace.HashFileAsync(manifestPath, ct);
        var manifest = await WorkspaceBackup.ValidateAsync(directory, ct);
        await using var database = new SqliteConnection(SqliteSchema.ConnectionString(Path.Combine(directory, "queue.db"), SqliteOpenMode.ReadOnly));
        await database.OpenAsync(ct); await using var count = database.CreateCommand(); count.CommandText = "SELECT COUNT(*) FROM jobs";
        var jobs = Convert.ToInt64(await count.ExecuteScalarAsync(ct));
        if (await Workspace.HashFileAsync(manifestPath, ct) != hash) throw new IOException("The backup changed during review. Choose and verify it again.");
        return new(directory, manifest, hash, jobs);
    }
    public Task<BackupReview> ReviewAsync(string directory, CancellationToken ct = default) => RunAsync(async () =>
    { reviewed = null; return reviewed = await ReviewCoreAsync(directory, ct); });
    public Task<BackupReview> CreateAsync(CancellationToken ct = default) => RunAsync(async () =>
    { reviewed = null; var path = await WorkspaceBackup.CreateAsync(lease, ct); return reviewed = await ReviewCoreAsync(path, ct); });
    public Task<RestoreResult> RestoreAsync(BackupReview review, bool confirmed, CancellationToken ct = default) => RunAsync(async () =>
    {
        if (!confirmed) throw new IOException("Restore requires confirmation that the current local queue, draft, settings and job files will be replaced.");
        if (!ReferenceEquals(review, reviewed)) throw new IOException("Choose and verify this backup again before restoring it.");
        reviewed = null;
        var fresh = await ReviewCoreAsync(review.Directory, ct);
        if (fresh.ManifestHash != review.ManifestHash) throw new IOException("The reviewed backup changed. Choose and verify it again; current local state was preserved.");
        try { return await WorkspaceBackup.RestoreAsync(lease, review.Directory, restoreObserver, ct); }
        catch (Exception failure)
        {
            try { await WorkspaceBackup.RecoverInterruptedAsync(lease, CancellationToken.None); }
            catch (Exception recovery) { throw new IOException("Restore and automatic recovery could not finish. Keep the retained files and resolve recovery before returning to the editor.", new AggregateException(failure, recovery)); }
            throw;
        }
    });
    public Task<bool> RecoverAsync(CancellationToken ct = default) => RunAsync(async () =>
    { reviewed = null; return await WorkspaceBackup.RecoverInterruptedAsync(lease, ct); });
}
