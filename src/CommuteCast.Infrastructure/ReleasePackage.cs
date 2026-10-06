using CommuteCast.Core;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CommuteCast.Infrastructure;

public record ReleaseFile(string RelativePath, long Bytes, string Sha256);
public record ReleaseManifest(int FormatVersion, string PackageId, string AppVersion, string DesktopBuild,
    string MaintenanceBuild, string BundledRuntime, string Target, int MinimumSchema, int MaximumSchema,
    int ProviderContract, DateTimeOffset CreatedUtc, IReadOnlyList<ReleaseFile> Files);

/// <summary>Package inventory/integrity for deliberate updates. Checksums do not establish publisher trust or signing approval.</summary>
public static class ReleasePackage
{
    public const string ManifestName = "release-manifest.json";
    private const int MaximumFiles = 10000;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly string[] Required = ["README.md", "THIRD-PARTY-NOTICES.md", "app/CommuteCast.Desktop.exe", "app/CommuteCast.Maintenance.exe",
        "app/CommuteCast.Core.dll", "app/coreclr.dll", "app/CommuteCast.Desktop.runtimeconfig.json", "app/CommuteCast.Maintenance.runtimeconfig.json",
        "services/compose.yaml", "services/speech/requirements.lock.txt", "services/speech/model-checksums.txt"];

    private static bool Allowed(string path)
    {
        if (path is "README.md" or "THIRD-PARTY-NOTICES.md") return true;
        var segments = path.Split('/');
        if (segments.Length < 2 || segments[0] is not ("app" or "scripts" or "services" or "documents")) return false;
        foreach (var segment in segments)
            if (!Regex.IsMatch(segment, "^[A-Za-z0-9_-][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant) || segment.EndsWith('.') ||
                Regex.IsMatch(segment.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                new[] { "__pycache__", "jobs", "backups", "schema-backups", "recovery", "provisioning-models", "diagnostics", "auditions" }.Contains(segment, StringComparer.OrdinalIgnoreCase)) return false;
        var name = segments[^1]; var extension = Path.GetExtension(name);
        return !name.EndsWith(".local.json", StringComparison.OrdinalIgnoreCase) &&
            !new[] { "draft.json", "settings.json", "instance.lease", "recovery-kokoro.json", "recovery-piper.json" }.Contains(name, StringComparer.OrdinalIgnoreCase) &&
            !new[] { ".db", ".db-wal", ".db-shm", ".db-journal", ".wav", ".mp3", ".onnx", ".bin", ".pyc", ".user" }.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }
    private static string Resolve(string root, string relative)
    {
        if (!Allowed(relative)) throw new IOException("The release contains an unsafe or private-state path. No package was approved.");
        var result = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!Workspace.IsWithin(root, result)) throw new IOException("The release path escapes its package.");
        SqliteSchema.RejectLink(result); return result;
    }
    private static IReadOnlyList<string> Inventory(string root, CancellationToken ct)
    {
        Workspace.RejectReparsePoints(root);
        if (!Directory.Exists(root)) throw new IOException("Choose an existing complete portable package folder.");
        var pending = new Stack<string>(); pending.Push(root); var files = new List<string>(); var entries = 0;
        while (pending.Count > 0)
            foreach (var path in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                ct.ThrowIfCancellationRequested(); Workspace.RejectFileReparsePoint(path);
                if (++entries > MaximumFiles * 2) throw new IOException("The release contains too many entries to inspect safely.");
                if (Directory.Exists(path)) { pending.Push(path); continue; }
                var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                if (relative == ManifestName) continue;
                Resolve(root, relative); files.Add(relative);
                if (files.Count > MaximumFiles) throw new IOException("The release contains too many files.");
            }
        if (files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count || !Required.All(files.Contains)) throw new IOException("Required application/runtime/service/notices files are missing or ambiguous.");
        return files.Order(StringComparer.Ordinal).ToArray();
    }
    private static string Build(string root, string relative) => FileVersionInfo.GetVersionInfo(Resolve(root, relative)).ProductVersion ?? "unavailable";
    private static string Identity(ReleaseManifest manifest) => Job.Hash(JsonSerializer.Serialize(new
    {
        manifest.AppVersion, manifest.DesktopBuild, manifest.MaintenanceBuild, manifest.BundledRuntime, manifest.Target,
        manifest.MinimumSchema, manifest.MaximumSchema, manifest.ProviderContract, manifest.Files
    }));
    public static async Task<ReleaseManifest> SealAsync(string root, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); root = Path.GetFullPath(root);
        var inventory = Inventory(root, ct); var files = new List<ReleaseFile>();
        foreach (var relative in inventory)
        {
            var path = Resolve(root, relative);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            files.Add(new(relative, input.Length, Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(input, ct))));
        }
        var manifest = new ReleaseManifest(1, "", SqliteSchema.AppVersion, Build(root, "app/CommuteCast.Desktop.exe"),
            Build(root, "app/CommuteCast.Maintenance.exe"), Build(root, "app/coreclr.dll"), "win-x64", 0, SqliteSchema.CurrentVersion, 1, DateTimeOffset.UtcNow, files);
        if (manifest.DesktopBuild.Split('+')[0] != manifest.AppVersion || manifest.MaintenanceBuild.Split('+')[0] != manifest.AppVersion)
            throw new IOException("The application and maintenance binary versions differ from this packaging tool. Rebuild the entire package.");
        if (inventory.Contains("app/CommuteCast.Setup.exe") && Build(root, "app/CommuteCast.Setup.exe") != manifest.DesktopBuild)
            throw new IOException("The native setup entry point differs from the desktop build. Rebuild the entire package.");
        manifest = manifest with { PackageId = Identity(manifest) };
        ct.ThrowIfCancellationRequested(); await Workspace.AtomicWriteAsync(Path.Combine(root, ManifestName), JsonSerializer.Serialize(manifest, Json));
        return await ValidateAsync(root, ct);
    }
    public static async Task<ReleaseManifest> ValidateAsync(string root, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); root = Path.GetFullPath(root); var actual = Inventory(root, ct);
        var path = Path.Combine(root, ManifestName); SqliteSchema.RejectLink(path);
        if (!File.Exists(path) || new FileInfo(path).Length > 8 * 1048576) throw new IOException("A bounded release inventory is required. Rebuild or obtain a verified complete package.");
        ReleaseManifest manifest;
        try { manifest = JsonSerializer.Deserialize<ReleaseManifest>(await File.ReadAllTextAsync(path, ct)) ?? throw new JsonException(); }
        catch (JsonException error) { throw new IOException("The release inventory is unreadable. No package was approved.", error); }
        if (manifest.FormatVersion != 1 || !Regex.IsMatch(manifest.PackageId ?? "", "^[A-F0-9]{64}$", RegexOptions.CultureInvariant) ||
            manifest.Target != "win-x64" || manifest.MinimumSchema < 0 || manifest.MaximumSchema < manifest.MinimumSchema || manifest.MaximumSchema > SqliteSchema.CurrentVersion || manifest.ProviderContract != 1 ||
            string.IsNullOrWhiteSpace(manifest.AppVersion) || manifest.AppVersion.Length > 64 || manifest.DesktopBuild is null || manifest.MaintenanceBuild is null || manifest.BundledRuntime is null ||
            manifest.DesktopBuild.Length > 512 || manifest.MaintenanceBuild.Length > 512 || manifest.BundledRuntime.Length > 512 ||
            manifest.DesktopBuild.Split('+')[0] != manifest.AppVersion || manifest.MaintenanceBuild.Split('+')[0] != manifest.AppVersion ||
            manifest.Files is null || manifest.Files.Count is < 1 or > MaximumFiles ||
            manifest.Files.Any(f => f is null || f.Bytes < 0 || !Allowed(f.RelativePath ?? "") || !Regex.IsMatch(f.Sha256 ?? "", "^[A-F0-9]{64}$", RegexOptions.CultureInvariant)) ||
            !manifest.Files.Select(f => f.RelativePath).SequenceEqual(actual, StringComparer.Ordinal) || manifest.PackageId != Identity(manifest))
            throw new IOException("The release inventory, compatibility or identity is invalid. No package was approved.");
        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested(); var candidate = Resolve(root, file.RelativePath);
            await using var input = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            if (input.Length != file.Bytes || Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(input, ct)) != file.Sha256)
                throw new IOException("A release file is missing or changed. Obtain a complete verified package before updating.");
        }
        if (Build(root, "app/CommuteCast.Desktop.exe") != manifest.DesktopBuild || Build(root, "app/CommuteCast.Maintenance.exe") != manifest.MaintenanceBuild || Build(root, "app/coreclr.dll") != manifest.BundledRuntime)
            throw new IOException("The binary build inventory differs from its declaration.");
        return manifest;
    }
}
