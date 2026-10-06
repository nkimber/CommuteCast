using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CommuteCast.Infrastructure;

public record BackupFile(string RelativePath, long Bytes, string Sha256);
public record BackupManifest(int FormatVersion, string BackupId, string AppVersion, int SchemaVersion, DateTimeOffset CreatedUtc, IReadOnlyList<BackupFile> Files);
public enum RestoreCheckpoint { Prepared, OldItemMoved, OldMoved, NewItemMoved, NewMoved, Committed }
public interface IRestoreObserver { Task ReachedAsync(RestoreCheckpoint checkpoint, string? item, CancellationToken ct); }
public enum RestoreRecoveryCheckpoint { IncomingRetained, OriginalRestored, CommittedStateRetained }
public interface IRestoreRecoveryObserver { Task ReachedAsync(RestoreRecoveryCheckpoint checkpoint, string? item, CancellationToken ct); }
public record RestoreResult(string RestoreId, string PreviousState);
internal record RestoreJournal(int FormatVersion, string Id, string Phase, IReadOnlyDictionary<string, bool> HadOriginal, IReadOnlyDictionary<string, string?> OriginalDigests);

/// <summary>Offline local-state maintenance. The caller must acquire the workspace lease and stop its own workers.</summary>
public static class WorkspaceBackup
{
    private static readonly string[] RootFiles = ["settings.json", "draft.json", "provider-lock.local.json", "recovery-kokoro.json", "recovery-piper.json"];
    private static readonly string[] ManagedRoots = ["queue.db", "queue.db-wal", "queue.db-shm", "queue.db-journal", "settings.json", "draft.json", "provider-lock.local.json", "recovery-kokoro.json", "recovery-piper.json", "jobs"];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private const int MaximumFiles = 100000;
    private static string RecoveryRoot(Workspace workspace) => Path.Combine(workspace.Root, "recovery");
    private static string JournalPath(Workspace workspace) => Path.Combine(RecoveryRoot(workspace), "restore.pending.json");
    private static string AttemptRoot(Workspace workspace, string id)
    {
        if (!Regex.IsMatch(id, "^[a-f0-9]{32}$", RegexOptions.CultureInvariant)) throw new IOException("The restore journal identity is invalid. Local files were preserved.");
        return Path.Combine(RecoveryRoot(workspace), "restores", id);
    }
    private static void GuardTree(string directory, CancellationToken ct = default)
    {
        Workspace.RejectReparsePoints(directory);
        if (!Directory.Exists(directory)) return;
        var pending = new Stack<string>(); pending.Push(directory); var count = 0;
        while (pending.Count > 0)
            foreach (var path in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                ct.ThrowIfCancellationRequested();
                if (++count > MaximumFiles * 2) throw new IOException("Managed storage contains too many entries to inspect safely. Existing files were preserved.");
                Workspace.RejectFileReparsePoint(path);
                if (Directory.Exists(path)) pending.Push(path);
            }
    }
    private static void GuardState(Workspace workspace)
    {
        foreach (var name in ManagedRoots)
        {
            var path = Path.Combine(workspace.Root, name); SqliteSchema.RejectLink(path);
            if (name == "jobs")
            {
                if (File.Exists(path)) throw new IOException("Private job storage has an incompatible shape. Files were preserved.");
                GuardTree(path);
            }
            else if (Directory.Exists(path)) throw new IOException("A managed state file is a directory. Files were preserved.");
        }
    }
    private static bool ValidRelativePath(string relative)
    {
        if (relative == "queue.db" || RootFiles.Contains(relative, StringComparer.Ordinal)) return true;
        var parts = relative.Split('/');
        if (parts.Length != 3 || parts[0] != "jobs" || !Regex.IsMatch(parts[1], "^[a-fA-F0-9]{32}$", RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(parts[2], "^[A-Za-z0-9_-][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant) || parts[2].EndsWith('.')) return false;
        return !Regex.IsMatch(parts[2].Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
    private static string FilePath(string directory, string relative)
    {
        if (!ValidRelativePath(relative)) throw new IOException("The backup contains an unsafe or unrecognized relative path. Nothing was restored.");
        var path = Path.GetFullPath(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!Workspace.IsWithin(directory, path)) throw new IOException("The backup path escapes managed storage. Nothing was restored.");
        SqliteSchema.RejectLink(path); return path;
    }
    private static IEnumerable<(string Relative, string Path)> StateFiles(Workspace workspace)
    {
        foreach (var name in RootFiles)
        {
            var path = Path.Combine(workspace.Root, name); if (File.Exists(path)) yield return (name, path);
        }
        var jobs = Path.Combine(workspace.Root, "jobs");
        if (!Directory.Exists(jobs)) yield break;
        foreach (var directory in Directory.EnumerateDirectories(jobs))
        {
            var id = Path.GetFileName(directory);
            if (!Regex.IsMatch(id, "^[a-fA-F0-9]{32}$", RegexOptions.CultureInvariant) || Directory.EnumerateDirectories(directory).Any()) throw new IOException("Private job storage contains an unrecognized directory. Backup stopped without dropping it.");
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                var relative = "jobs/" + id + "/" + Path.GetFileName(path);
                if (!ValidRelativePath(relative)) throw new IOException("Private job storage contains an unsupported filename. Backup stopped without dropping it.");
                yield return (relative, path);
            }
        }
        if (Directory.EnumerateFiles(jobs).Any()) throw new IOException("Private job storage contains unrecognized top-level files. Backup stopped without dropping them.");
    }
    private static async Task<BackupFile> CopyAsync(string inputPath, string outputPath, string relative, CancellationToken ct)
    {
        SqliteSchema.RejectLink(inputPath); SqliteSchema.RejectLink(outputPath); Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[81920]; long bytes = 0; int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0) { hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), ct); bytes += read; }
        output.Flush(true); return new(relative, bytes, Convert.ToHexString(hash.GetHashAndReset()));
    }
    public static async Task<string> CreateAsync(WorkspaceLease lease, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lease.EnsureHeld(); var workspace = lease.Workspace;
        await RecoverInterruptedAsync(lease, ct); GuardState(workspace);
        var database = Path.Combine(workspace.Root, "queue.db");
        if (!File.Exists(database)) await new SqliteJobStore(workspace).LoadAsync(ct);
        var files = new List<(string Relative, string Path)>();
        foreach (var file in StateFiles(workspace))
        {
            ct.ThrowIfCancellationRequested(); files.Add(file);
            if (files.Count >= MaximumFiles) throw new IOException("The backup contains too many files. Clean eligible cache or remove older narrations explicitly before retrying.");
        }
        var required = files.Sum(f => new FileInfo(f.Path).Length) + new FileInfo(database).Length + (File.Exists(database + "-wal") ? new FileInfo(database + "-wal").Length : 0);
        if (new DriveInfo(Path.GetPathRoot(workspace.Root)!).AvailableFreeSpace < required + 64 * 1048576L) throw new IOException("There is insufficient free space for a verified local backup. Existing data was retained.");
        var parent = Path.Combine(workspace.Root, "backups"); Workspace.RejectReparsePoints(parent); Directory.CreateDirectory(parent);
        var id = Guid.NewGuid().ToString("N"); var stage = Path.Combine(parent, id + ".partial"); var final = Path.Combine(parent, id);
        if (Directory.Exists(stage) || Directory.Exists(final)) throw new IOException("Backup identity collision. Existing files were preserved.");
        Directory.CreateDirectory(stage);
        try
        {
            await using (var source = new SqliteConnection(SqliteSchema.ConnectionString(database, SqliteOpenMode.ReadOnly)))
            { await source.OpenAsync(ct); await SqliteSchema.SnapshotAsync(source, Path.Combine(stage, "queue.db"), ct); }
            var receipts = new List<BackupFile> { new("queue.db", new FileInfo(Path.Combine(stage, "queue.db")).Length, await Workspace.HashFileAsync(Path.Combine(stage, "queue.db"), ct)) };
            foreach (var file in files) { ct.ThrowIfCancellationRequested(); receipts.Add(await CopyAsync(file.Path, FilePath(stage, file.Relative), file.Relative, ct)); }
            var schema = await SqliteSchema.ValidateDatabaseAsync(Path.Combine(stage, "queue.db"), ct);
            var manifest = new BackupManifest(1, id, SqliteSchema.AppVersion, schema, DateTimeOffset.UtcNow, receipts);
            await Workspace.AtomicWriteAsync(Path.Combine(stage, "manifest.json"), JsonSerializer.Serialize(manifest, Json));
            await ValidateAsync(stage, ct); await PublishBackupAsync(stage, final, ct); return final;
        }
        catch
        {
            // The random directory was created exclusively for this invocation. Keep unsafe/locked remnants for inspection.
            try { if (Workspace.IsWithin(parent, stage)) { GuardTree(stage); Directory.Delete(stage, true); } } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
    private static async Task PublishBackupAsync(string stage, string final, CancellationToken ct)
    {
        // Windows scanners can briefly retain a handle after validation closes it.
        // Retry only the same exclusive rename, never overwrite or publish unchecked data.
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested(); SqliteSchema.RejectLink(stage); SqliteSchema.RejectLink(final);
            try { Directory.Move(stage, final); return; }
            catch (IOException error) when (OperatingSystem.IsWindows() && attempt < 3 && !Exists(final) && (error.HResult & 0xffff) is 5 or 32)
            { await Task.Delay(100 * (attempt + 1), ct); }
        }
    }
    public static async Task<BackupManifest> ValidateAsync(string directory, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); directory = Path.GetFullPath(directory); GuardTree(directory, ct);
        var manifestPath = Path.Combine(directory, "manifest.json"); SqliteSchema.RejectLink(manifestPath);
        if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 32 * 1048576) throw new IOException("A bounded, completed backup manifest is required. Partial backups cannot be restored.");
        BackupManifest manifest;
        try { manifest = JsonSerializer.Deserialize<BackupManifest>(await File.ReadAllTextAsync(manifestPath, ct)) ?? throw new JsonException(); }
        catch (JsonException error) { throw new IOException("The backup manifest is unreadable. Nothing was restored.", error); }
        if (manifest.FormatVersion != 1 || !Regex.IsMatch(manifest.BackupId ?? "", "^[a-f0-9]{32}$", RegexOptions.CultureInvariant) || manifest.Files is null || manifest.Files.Count is < 1 or > MaximumFiles ||
            manifest.SchemaVersion is < 0 or > SqliteSchema.CurrentVersion || manifest.Files.Any(f => f is null || f.Bytes < 0 || !Regex.IsMatch(f.Sha256 ?? "", "^[A-F0-9]{64}$", RegexOptions.CultureInvariant) || !ValidRelativePath(f.RelativePath ?? "")) ||
            manifest.Files.Select(f => f.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count || !manifest.Files.Any(f => f.RelativePath == "queue.db"))
            throw new IOException("The backup format, schema or file manifest is incompatible. Nothing was restored.");
        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested(); var path = FilePath(directory, file.RelativePath);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Bytes || await Workspace.HashFileAsync(path, ct) != file.Sha256) throw new IOException("A backup file is missing or changed. Nothing was restored.");
        }
        var actual = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(directory, p).Replace(Path.DirectorySeparatorChar, '/')).Where(p => p != "manifest.json").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actual.SetEquals(manifest.Files.Select(f => f.RelativePath))) throw new IOException("The backup contains unlisted files. Nothing was restored.");
        if (await SqliteSchema.ValidateDatabaseAsync(Path.Combine(directory, "queue.db"), ct) != manifest.SchemaVersion) throw new IOException("The backup schema differs from its manifest. Nothing was restored.");
        return manifest;
    }
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    private static async Task<string?> DigestAsync(string path, CancellationToken ct)
    {
        if (!Exists(path)) return null;
        if (File.Exists(path)) return await Workspace.HashFileAsync(path, ct);
        GuardTree(path); var entries = new List<(string Path, string Hash)>();
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested(); entries.Add((Path.GetRelativePath(path, file), await Workspace.HashFileAsync(file, ct)));
            if (entries.Count > MaximumFiles) throw new IOException("There are too many private files to record safe restore recovery. No state was replaced.");
        }
        return CommuteCast.Core.Job.Hash(JsonSerializer.Serialize(entries.Select(e => new { e.Path, e.Hash })));
    }
    private static async Task MoveAsync(string from, string to, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested(); SqliteSchema.RejectLink(from); SqliteSchema.RejectLink(to);
            if (Exists(to)) throw new IOException("Restore recovery would overwrite an existing file. All state was preserved for inspection.");
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            try { if (Directory.Exists(from)) { GuardTree(from); Directory.Move(from, to); } else File.Move(from, to, false); return; }
            catch (IOException error) when (attempt < 5 && (error.HResult & 0xFFFF) is 5 or 32 or 33) { await Task.Delay(50 << attempt, ct); }
            catch (UnauthorizedAccessException) when (attempt < 5) { await Task.Delay(50 << attempt, ct); }
        }
    }
    public static async Task<RestoreResult> RestoreAsync(WorkspaceLease lease, string backup, IRestoreObserver? observer = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lease.EnsureHeld(); var workspace = lease.Workspace; await RecoverInterruptedAsync(lease, ct);
        var manifest = await ValidateAsync(backup, ct); GuardState(workspace);
        var id = Guid.NewGuid().ToString("N"); var attempt = AttemptRoot(workspace, id); GuardTree(attempt);
        var incoming = Path.Combine(attempt, "incoming"); var previous = Path.Combine(attempt, "previous"); Directory.CreateDirectory(incoming); Directory.CreateDirectory(previous);
        foreach (var file in manifest.Files)
        {
            var receipt = await CopyAsync(FilePath(backup, file.RelativePath), FilePath(incoming, file.RelativePath), file.RelativePath, ct);
            if (receipt.Bytes != file.Bytes || receipt.Sha256 != file.Sha256) throw new IOException("The backup changed while preparing restore. Current local state was not replaced.");
        }
        await Workspace.AtomicWriteAsync(Path.Combine(incoming, "manifest.json"), JsonSerializer.Serialize(manifest, Json));
        await ValidateAsync(incoming, ct); ct.ThrowIfCancellationRequested();
        var digests = new Dictionary<string, string?>();
        foreach (var name in ManagedRoots) digests[name] = await DigestAsync(Path.Combine(workspace.Root, name), ct);
        var journal = new RestoreJournal(1, id, "Prepared", ManagedRoots.ToDictionary(n => n, n => digests[n] is not null, StringComparer.Ordinal), digests);
        await Workspace.AtomicWriteAsync(JournalPath(workspace), JsonSerializer.Serialize(journal, Json));
        if (observer is not null) await observer.ReachedAsync(RestoreCheckpoint.Prepared, null, ct);
        foreach (var name in ManagedRoots)
        {
            ct.ThrowIfCancellationRequested(); var original = Path.Combine(workspace.Root, name);
            if (Exists(original)) await MoveAsync(original, Path.Combine(previous, name), ct);
            if (observer is not null) await observer.ReachedAsync(RestoreCheckpoint.OldItemMoved, name, ct);
        }
        journal = journal with { Phase = "OldMoved" }; await Workspace.AtomicWriteAsync(JournalPath(workspace), JsonSerializer.Serialize(journal, Json));
        if (observer is not null) await observer.ReachedAsync(RestoreCheckpoint.OldMoved, null, ct);
        foreach (var name in ManagedRoots)
        {
            ct.ThrowIfCancellationRequested(); var staged = Path.Combine(incoming, name);
            if (Exists(staged)) await MoveAsync(staged, Path.Combine(workspace.Root, name), ct);
            if (observer is not null) await observer.ReachedAsync(RestoreCheckpoint.NewItemMoved, name, ct);
        }
        if (observer is not null) await observer.ReachedAsync(RestoreCheckpoint.NewMoved, null, ct);
        foreach (var file in manifest.Files)
        {
            var restored = FilePath(workspace.Root, file.RelativePath);
            if (!File.Exists(restored) || new FileInfo(restored).Length != file.Bytes || await Workspace.HashFileAsync(restored, ct) != file.Sha256) throw new IOException("Prepared restore data changed before commit. Original state is retained for recovery.");
        }
        var restoredFiles = StateFiles(workspace).Select(f => f.Relative).Append("queue.db").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!restoredFiles.SetEquals(manifest.Files.Select(f => f.RelativePath))) throw new IOException("Prepared restore contains unrecorded state. Original state is retained for recovery.");
        await SqliteSchema.ValidateDatabaseAsync(Path.Combine(workspace.Root, "queue.db"), ct); ct.ThrowIfCancellationRequested();
        journal = journal with { Phase = "Committed" }; await Workspace.AtomicWriteAsync(JournalPath(workspace), JsonSerializer.Serialize(journal, Json));
        if (observer is not null) await observer.ReachedAsync(RestoreCheckpoint.Committed, null, CancellationToken.None);
        File.Delete(JournalPath(workspace)); return new(id, previous);
    }
    public static async Task<bool> RecoverInterruptedAsync(WorkspaceLease lease, CancellationToken ct = default, IRestoreRecoveryObserver? observer = null)
    {
        ct.ThrowIfCancellationRequested();
        lease.EnsureHeld(); var workspace = lease.Workspace; var path = JournalPath(workspace); SqliteSchema.RejectLink(path);
        if (!File.Exists(path)) return false;
        if (new FileInfo(path).Length > 65536) throw new IOException("The restore journal is invalid. Local state was preserved for inspection.");
        RestoreJournal journal;
        try { journal = JsonSerializer.Deserialize<RestoreJournal>(await File.ReadAllTextAsync(path, ct)) ?? throw new JsonException(); }
        catch (JsonException error) { throw new IOException("The restore journal is unreadable. Local state was preserved for inspection.", error); }
        if (journal.FormatVersion != 1 || journal.Phase is not ("Prepared" or "OldMoved" or "Committed") || journal.HadOriginal is null || !journal.HadOriginal.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(ManagedRoots) ||
            journal.OriginalDigests is null || !journal.OriginalDigests.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(ManagedRoots) ||
            ManagedRoots.Any(n => journal.HadOriginal[n] ? !Regex.IsMatch(journal.OriginalDigests[n] ?? "", "^[A-F0-9]{64}$", RegexOptions.CultureInvariant) : journal.OriginalDigests[n] is not null))
            throw new IOException("The restore journal is incompatible. Local state was preserved for inspection.");
        var attempt = AttemptRoot(workspace, journal.Id); GuardTree(attempt); GuardState(workspace);
        if (journal.Phase != "Committed")
        {
            var previous = Path.Combine(attempt, "previous"); var failed = Path.Combine(attempt, "rolled-back-incoming");
            foreach (var name in ManagedRoots)
            {
                ct.ThrowIfCancellationRequested(); var current = Path.Combine(workspace.Root, name); var old = Path.Combine(previous, name);
                if (Exists(old))
                {
                    if (await DigestAsync(old, ct) != journal.OriginalDigests[name]) throw new IOException("Original recovery state changed. All remaining files were preserved for inspection.");
                    if (Exists(current))
                    {
                        await MoveAsync(current, Path.Combine(failed, name), ct);
                        if (observer is not null) await observer.ReachedAsync(RestoreRecoveryCheckpoint.IncomingRetained, name, ct);
                    }
                    await MoveAsync(old, current, ct);
                    if (observer is not null) await observer.ReachedAsync(RestoreRecoveryCheckpoint.OriginalRestored, name, ct);
                }
                else if (!journal.HadOriginal[name] && Exists(current))
                {
                    await MoveAsync(current, Path.Combine(failed, name), ct);
                    if (observer is not null) await observer.ReachedAsync(RestoreRecoveryCheckpoint.IncomingRetained, name, ct);
                }
                else if (journal.HadOriginal[name] && (!Exists(current) || await DigestAsync(current, ct) != journal.OriginalDigests[name])) throw new IOException("Restore recovery cannot verify an original state item. All remaining files were preserved for inspection.");
            }
        }
        else if (observer is not null) await observer.ReachedAsync(RestoreRecoveryCheckpoint.CommittedStateRetained, null, ct);
        File.Delete(path); return true;
    }
}
