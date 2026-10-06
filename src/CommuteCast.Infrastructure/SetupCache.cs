using CommuteCast.Core;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CommuteCast.Infrastructure;

internal record SetupCachePackage(string PackageId, IReadOnlyList<ReleaseFile> Files, ReleaseManifest Manifest);
internal record SetupCacheJournal(int FormatVersion, string InstallationId, string StateHash, string CacheOwnerHash, IReadOnlyList<SetupCachePackage> Packages);
public record SetupCachePreserved(string Name, string Reason);
public sealed class SetupCacheReview
{
    internal SetupCacheReview(string root, string installationId, string stateHash, string ownerHash, IReadOnlyList<SetupCachePackage> packages, IReadOnlyList<SetupCachePreserved> preserved)
    { InstallationRoot = root; InstallationId = installationId; StateHash = stateHash; OwnerHash = ownerHash; Packages = packages; Preserved = preserved; }
    public string InstallationRoot { get; }
    internal string InstallationId { get; }
    internal string StateHash { get; }
    internal string OwnerHash { get; }
    internal IReadOnlyList<SetupCachePackage> Packages { get; }
    public IReadOnlyList<SetupCachePreserved> Preserved { get; }
    public int Copies => Packages.Count;
    public int Files => Packages.Sum(p => p.Files.Count);
    public long Bytes => Packages.Sum(p => p.Files.Sum(f => f.Bytes));
    public string CacheRoot => Path.Combine(Directory.GetParent(InstallationRoot)!.FullName, "CommuteCast-setup-" + InstallationId);
    public IReadOnlyList<string> PackageIds => Packages.Select(p => p.PackageId).ToArray();
    public string Fingerprint => Job.Hash(JsonSerializer.Serialize(new { InstallationRoot, InstallationId, StateHash, OwnerHash, Packages, Preserved }));
}
public record SetupCacheCleanupResult(int Copies, int Files, long Bytes);
public sealed class CachedSetupUse : IDisposable
{
    private IDisposable? held;
    internal CachedSetupUse(string installationRoot, string privateRoot, IDisposable held) { InstallationRoot = installationRoot; PrivateRoot = privateRoot; this.held = held; }
    public string InstallationRoot { get; }
    public string PrivateRoot { get; }
    public void Dispose() => Interlocked.Exchange(ref held, null)?.Dispose();
}

/// <summary>Explicit reviewed cleanup of verified setup distributions, never private narration storage.</summary>
public static class SetupCache
{
    private const string OwnerName = "setup-cache.owner.json", JournalName = "setup-cache.pending.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static bool Hash(string? value) => Regex.IsMatch(value ?? "", "^[A-F0-9]{64}$", RegexOptions.CultureInvariant);
    public static bool HasPending(string root) { var path = Path.Combine(root, JournalName); SqliteSchema.RejectLink(path); return File.Exists(path); }
    internal static async Task<string> PendingHashAsync(string root, CancellationToken ct)
    { if (!HasPending(root)) return ""; var path = Path.Combine(root, JournalName); if (new FileInfo(path).Length > 32 * 1048576) throw new IOException("Setup cache recovery exceeds its safe limit."); return await Workspace.HashFileAsync(path, ct); }
    internal static async Task<(string Root, string OwnerHash)> InspectOwnerAsync(string root, InstallationOwner owner, bool create, CancellationToken ct)
    {
        var cache = LauncherPlan.SetupCacheRoot(root, owner); Workspace.RejectReparsePoints(cache);
        if (File.Exists(cache)) throw new IOException("The setup cache became a file. It was preserved.");
        if (Workspace.IsWithin(cache, owner.WorkspaceRoot) || Workspace.IsWithin(owner.WorkspaceRoot, cache)) throw new IOException("The setup cache overlaps private data.");
        var ownerPath = Path.Combine(cache, OwnerName); SqliteSchema.RejectLink(ownerPath);
        if (!Directory.Exists(cache))
        {
            if (!create) return (cache, "");
            Directory.CreateDirectory(cache);
            if (Directory.EnumerateFileSystemEntries(cache).Any()) throw new IOException("The external setup folder became occupied. Existing entries were preserved.");
            SqliteSchema.RejectLink(ownerPath);
            await using var output = new FileStream(ownerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough);
            await output.WriteAsync(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new SetupCacheOwner(1, owner.InstallationId, Path.GetFullPath(root)))), ct); output.Flush(true);
        }
        if (!File.Exists(ownerPath) || new FileInfo(ownerPath).Length > 65536) throw new IOException("The external setup folder has no bounded installation ownership. Files were preserved.");
        var hash = await Workspace.HashFileAsync(ownerPath, ct); SetupCacheOwner? existing;
        try { existing = JsonSerializer.Deserialize<SetupCacheOwner>(await File.ReadAllTextAsync(ownerPath, ct)); }
        catch (JsonException error) { throw new IOException("External setup ownership is unreadable. Files were preserved.", error); }
        if (existing is null || existing.FormatVersion != 1 || existing.InstallationId != owner.InstallationId || !Path.IsPathFullyQualified(existing.InstallationRoot ?? "") ||
            !Path.GetFullPath(existing.InstallationRoot!).Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) || await Workspace.HashFileAsync(ownerPath, ct) != hash)
            throw new IOException("External setup ownership differs or changed. Files were preserved.");
        return (cache, hash);
    }
    private static string LeasePath(string cache, string id) { if (!Hash(id)) throw new IOException("A setup cache identity is invalid."); var path = Path.Combine(cache, "package-" + id + ".lease"); SqliteSchema.RejectLink(path); return path; }
    internal static IDisposable AcquireUse(string cache, string id)
    {
        var path = LeasePath(cache, id); var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (stream.Length != 0) { stream.Dispose(); throw new IOException("A setup use lease changed. It was preserved."); } return stream;
    }
    private static bool BusyLease(string cache, string id)
    {
        var path = LeasePath(cache, id); if (Directory.Exists(path)) return true; if (!File.Exists(path)) return false;
        try { using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); return held.Length != 0; }
        catch (IOException) { return true; }
    }
    /// <summary>Cached native hosts retain this lease until exit. A portable host returns null.</summary>
    public static async Task<CachedSetupUse?> AcquireHostUseAsync(string applicationDirectory, CancellationToken ct = default)
    {
        var app = new DirectoryInfo(Path.GetFullPath(applicationDirectory)); var package = app.Parent; var cache = package?.Parent;
        if (!app.Name.Equals("app", StringComparison.OrdinalIgnoreCase) || !Hash(package?.Name.ToUpperInvariant()) || cache is null || !cache.Name.StartsWith("CommuteCast-setup-", StringComparison.Ordinal)) return null;
        SqliteSchema.RejectLink(Path.Combine(cache.FullName, OwnerName));
        if (!File.Exists(Path.Combine(cache.FullName, OwnerName)) || new FileInfo(Path.Combine(cache.FullName, OwnerName)).Length > 65536) throw new IOException("Cached setup ownership is missing or unbounded.");
        SetupCacheOwner? binding;
        try { binding = JsonSerializer.Deserialize<SetupCacheOwner>(await File.ReadAllTextAsync(Path.Combine(cache.FullName, OwnerName), ct)); }
        catch (JsonException error) { throw new IOException("Cached setup ownership is unreadable.", error); }
        if (binding?.InstallationRoot is null || !Path.IsPathFullyQualified(binding.InstallationRoot)) throw new IOException("Cached setup binding is invalid.");
        var installation = new Installation(binding.InstallationRoot); var owner = await installation.ReadOwnerAsync(ct);
        var inspected = await InspectOwnerAsync(installation.Root, owner, false, ct);
        if (!inspected.Root.Equals(cache.FullName, StringComparison.OrdinalIgnoreCase)) throw new IOException("Cached setup is outside its owned cache.");
        var held = AcquireUse(cache.FullName, package!.Name.ToUpperInvariant());
        try
        {
            if ((await ReleasePackage.ValidateAsync(package.FullName, ct)).PackageId != package.Name.ToUpperInvariant()) throw new IOException("Cached setup identity differs.");
            return new(installation.Root, owner.WorkspaceRoot, held);
        }
        catch { held.Dispose(); throw; }
    }
    private static bool Resident(string packageRoot, IReadOnlyList<ReleaseFile> files)
    {
        var names = files.Where(f => f.RelativePath.StartsWith("app/", StringComparison.Ordinal) && f.RelativePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).Select(f => Path.GetFileNameWithoutExtension(f.RelativePath)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length > 64) throw new IOException("There are too many setup executable identities to inspect safely.");
        foreach (var name in names)
            foreach (var process in Process.GetProcessesByName(name))
                using (process)
                    try { if (!process.HasExited && (process.MainModule?.FileName is not string path || Workspace.IsWithin(packageRoot, path))) return true; }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception) { return true; } // Uncertain residency preserves the copy.
        return false;
    }
    private static HashSet<string> Directories(IReadOnlyList<ReleaseFile> files)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files) { var path = file.RelativePath; while (path.Contains('/')) { path = path[..path.LastIndexOf('/')]; result.Add(path); } }
        return result;
    }
    private static async Task VerifyPartialAsync(string root, IReadOnlyList<ReleaseFile> files, CancellationToken ct)
    {
        SqliteSchema.RejectLink(root); if (File.Exists(root)) throw new IOException("A setup package became a file. It was preserved."); if (!Directory.Exists(root)) return;
        var expected = files.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase); var directories = Directories(files); var pending = new Stack<string>(); pending.Push(root); var visited = 0;
        while (pending.Count > 0)
            foreach (var path in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                ct.ThrowIfCancellationRequested(); Workspace.RejectFileReparsePoint(path); if (++visited > 20000) throw new IOException("A setup package exceeds its inspection limit.");
                var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                if (Directory.Exists(path)) { if (!directories.Contains(relative)) throw new IOException("A setup package contains an unrecorded directory. It was preserved."); pending.Push(path); }
                else if (!expected.TryGetValue(relative, out var file) || new FileInfo(path).Length != file.Bytes || await Workspace.HashFileAsync(path, ct) != file.Sha256) throw new IOException("A setup package contains changed or unrecorded files. They were preserved.");
            }
    }
    internal static async Task<SetupCacheReview> ReviewAsync(string root, InstallationOwner owner, InstallationState? state, string stateHash, string? sourceRoot, CancellationToken ct)
    {
        var cache = await InspectOwnerAsync(root, owner, false, ct); var packages = new List<SetupCachePackage>(); var preserved = new List<SetupCachePreserved>();
        if (cache.OwnerHash.Length == 0) return new(root, owner.InstallationId, stateHash, "", packages, preserved);
        var launcher = await InstalledLauncher.ReadReceiptAsync(root, owner, ct); var entries = Directory.EnumerateFileSystemEntries(cache.Root).Take(3001).Order(StringComparer.Ordinal).ToArray();
        if (entries.Length > 3000) throw new IOException("The setup cache exceeds its bounded review limit.");
        foreach (var path in entries)
        {
            ct.ThrowIfCancellationRequested(); Workspace.RejectFileReparsePoint(path); var name = Path.GetFileName(path);
            if (name == OwnerName || name.StartsWith("package-", StringComparison.Ordinal) && name.EndsWith(".lease", StringComparison.Ordinal) && Hash(name[8..^6]) && state?.KnownPackages.Contains(name[8..^6]) == true) continue;
            if (!Hash(name) || state?.KnownPackages.Contains(name) != true || !Directory.Exists(path)) { preserved.Add(new(name, "Unrecorded entry; kept for inspection.")); continue; }
            if (state.CurrentPackageId == name || state.CurrentPackageId is not null && launcher?.PackageId == name || sourceRoot is not null && Workspace.IsWithin(path, sourceRoot) || Workspace.IsWithin(path, AppContext.BaseDirectory)) { preserved.Add(new(name, "Current release, required setup kit or executing source.")); continue; }
            if (BusyLease(cache.Root, name)) { preserved.Add(new(name, "Setup copy is in use or its use lease changed.")); continue; }
            try
            {
                var manifest = await ReleasePackage.ValidateAsync(path, ct); if (manifest.PackageId != name) throw new IOException("Cache identity differs.");
                var manifestPath = Path.Combine(path, ReleasePackage.ManifestName); var files = manifest.Files.Append(new ReleaseFile(ReleasePackage.ManifestName, new FileInfo(manifestPath).Length, await Workspace.HashFileAsync(manifestPath, ct))).ToArray();
                await VerifyPartialAsync(path, files, ct);
                if (Resident(path, files)) { preserved.Add(new(name, "A cached executable is running or residency is uncertain.")); continue; }
                packages.Add(new(name, files, manifest));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { preserved.Add(new(name, "Copy is changed, incomplete or inaccessible; kept for inspection.")); }
        }
        if (packages.Sum(p => p.Files.Count) > 100000) throw new IOException("Setup cleanup exceeds the bounded file limit.");
        return new(root, owner.InstallationId, stateHash, cache.OwnerHash, packages, preserved);
    }
    internal static async Task<SetupCacheCleanupResult> CleanAsync(string root, InstallationOwner owner, InstallationState? state, string stateHash, SetupCacheReview review, string? sourceRoot, IInstallationObserver? observer, CancellationToken ct)
    {
        var current = await ReviewAsync(root, owner, state, stateHash, sourceRoot, ct);
        if (review.InstallationRoot != root || current.Fingerprint != review.Fingerprint) throw new IOException("The reviewed setup cleanup scope changed. Review again; no removal was committed.");
        if (current.Copies == 0) return new(0, 0, 0);
        var cache = await InspectOwnerAsync(root, owner, false, ct); var holds = new List<IDisposable>();
        try
        {
            foreach (var package in current.Packages) { holds.Add(AcquireUse(cache.Root, package.PackageId)); await VerifyPartialAsync(Path.Combine(cache.Root, package.PackageId), package.Files, ct); }
            var journal = new SetupCacheJournal(1, owner.InstallationId, stateHash, cache.OwnerHash, current.Packages); var text = JsonSerializer.Serialize(journal, Json);
            if (System.Text.Encoding.UTF8.GetByteCount(text) > 32 * 1048576) throw new IOException("Setup cleanup exceeds its journal limit.");
            await Workspace.AtomicWriteAsync(Path.Combine(root, JournalName), text); await Observe(observer, InstallationCheckpoint.SetupCachePrepared, null, ct);
            await RemoveAsync(cache.Root, journal, observer, ct); File.Delete(Path.Combine(root, JournalName)); return new(current.Copies, current.Files, current.Bytes);
        }
        finally { foreach (var held in holds) held.Dispose(); }
    }
    private static Task Observe(IInstallationObserver? observer, InstallationCheckpoint point, string? item, CancellationToken ct) => observer?.ReachedAsync(point, item, ct) ?? Task.CompletedTask;
    private static async Task RemoveAsync(string cache, SetupCacheJournal journal, IInstallationObserver? observer, CancellationToken ct)
    {
        foreach (var package in journal.Packages) await VerifyPartialAsync(Path.Combine(cache, package.PackageId), package.Files, ct);
        foreach (var package in journal.Packages)
        {
            var root = Path.Combine(cache, package.PackageId);
            if (Resident(root, package.Files)) throw new IOException("A cached executable is running. Close it and recover setup cleanup.");
            await OwnedFileRemoval.DeleteBatchAsync(root, package.Files, file => Observe(observer, InstallationCheckpoint.SetupCacheItemRemoved, package.PackageId + "/" + file, ct), ct);
            foreach (var relative in Directories(package.Files).OrderByDescending(p => p.Count(c => c == '/')))
            { var path = OwnedFileRemoval.Resolve(root, relative); if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path, false); }
            SqliteSchema.RejectLink(root); if (Directory.Exists(root)) { if (Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("Unrecorded setup entries appeared. They were preserved; cleanup remains pending."); Directory.Delete(root, false); }
        }
    }
    internal static async Task<bool> RecoverAsync(string root, InstallationOwner owner, InstallationState? state, string stateHash, CancellationToken ct)
    {
        if (!HasPending(root)) return false; var path = Path.Combine(root, JournalName); if (new FileInfo(path).Length > 32 * 1048576) throw new IOException("Setup cleanup journal exceeds its limit."); SetupCacheJournal? journal;
        try { journal = JsonSerializer.Deserialize<SetupCacheJournal>(await File.ReadAllTextAsync(path, ct)); }
        catch (JsonException error) { throw new IOException("Setup cleanup journal is unreadable. Files were preserved.", error); }
        if (journal is null || journal.FormatVersion != 1 || journal.InstallationId != owner.InstallationId || !Hash(journal.StateHash) || journal.StateHash != stateHash || !Hash(journal.CacheOwnerHash) ||
            journal.Packages is null || journal.Packages.Count is < 1 or > 1000 || journal.Packages.Any(p => p is null || !Hash(p.PackageId) || state?.KnownPackages.Contains(p.PackageId) != true || p.Files is null || p.Files.Count is < 1 or > 10001) ||
            journal.Packages.Select(p => p.PackageId).Distinct().Count() != journal.Packages.Count || journal.Packages.Sum(p => p.Files.Count) > 100000)
            throw new IOException("Setup cleanup ownership or recorded installation state differs. Files were preserved.");
        var cache = await InspectOwnerAsync(root, owner, false, ct); if (cache.OwnerHash != journal.CacheOwnerHash) throw new IOException("Setup cache ownership changed outside cleanup.");
        var launcher = await InstalledLauncher.ReadReceiptAsync(root, owner, ct); var holds = new List<IDisposable>();
        try
        {
            foreach (var package in journal.Packages)
            {
                if (state?.CurrentPackageId == package.PackageId || state?.CurrentPackageId is not null && launcher?.PackageId == package.PackageId || Workspace.IsWithin(Path.Combine(cache.Root, package.PackageId), AppContext.BaseDirectory)) throw new IOException("Cleanup would remove an active setup release. It was preserved.");
                if (package.Files.Any(f => f is null || f.Bytes < 0 || !Hash(f.Sha256)) || package.Files.Select(f => f.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != package.Files.Count || !package.Files.Any(f => f.RelativePath == ReleasePackage.ManifestName)) throw new IOException("Setup cleanup file ownership is invalid.");
                if (package.Manifest is null || package.Manifest.FormatVersion != 1 || package.Manifest.Files is null || package.Manifest.PackageId != package.PackageId ||
                    ReleasePackage.Identity(package.Manifest) != package.PackageId || package.Files.Count != package.Manifest.Files.Count + 1 ||
                    !package.Files.Where(f => f.RelativePath != ReleasePackage.ManifestName).SequenceEqual(package.Manifest.Files))
                    throw new IOException("Setup cleanup inventory differs from its recorded release identity. Unrecognized files were preserved.");
                foreach (var file in package.Files) OwnedFileRemoval.Resolve(Path.Combine(cache.Root, package.PackageId), file.RelativePath);
                holds.Add(AcquireUse(cache.Root, package.PackageId));
            }
            await RemoveAsync(cache.Root, journal, null, ct); File.Delete(path); return true;
        }
        finally { foreach (var held in holds) held.Dispose(); }
    }
}
