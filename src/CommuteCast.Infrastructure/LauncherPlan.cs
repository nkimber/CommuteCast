using System.Text.Json;

namespace CommuteCast.Infrastructure;

public record InstalledLaunchPlan(string Executable, IReadOnlyList<string> Arguments, string PackageId, bool ExternalSetup);
internal record SetupCacheOwner(int FormatVersion, string InstallationId, string InstallationRoot);

public static class LauncherPlan
{
    public static string SetupCacheRoot(string root, InstallationOwner owner) => Path.Combine(Directory.GetParent(Path.GetFullPath(root))!.FullName, "CommuteCast-setup-" + owner.InstallationId);
    public static async Task<InstalledLaunchPlan> CreateAsync(string root, bool setup = false, bool maintenance = false, CancellationToken ct = default)
    {
        if (setup && maintenance) throw new ArgumentException("Choose setup or local maintenance.");
        var installation = new Installation(root); var owner = await installation.ReadOwnerAsync(ct);
        using var lease = WorkspaceLease.Acquire(new Workspace(owner.WorkspaceRoot, false));
        if (!setup)
        {
            await InstalledLauncher.VerifyAsync(installation.Root, owner, ct);
            var active = maintenance ? await installation.InspectForMaintenanceAsync(lease, ct) : await installation.InspectAsync(lease, ct);
            return new(active.Executable ?? throw new IOException("CommuteCast is uninstalled. Use a verified setup package to reinstall."), maintenance ? ["--maintenance"] : [], active.State.CurrentPackageId!, false);
        }
        var id = await InstalledLauncher.VerifyForSetupAsync(root, owner, ct);
        var overview = await installation.ReadOverviewAsync(ct);
        if (overview.State?.KnownPackages.Contains(id) != true) throw new IOException("The setup release is not recorded by this installation.");
        var cache = SetupCacheRoot(root, owner); Workspace.RejectReparsePoints(cache);
        if (Workspace.IsWithin(cache, owner.WorkspaceRoot) || Workspace.IsWithin(owner.WorkspaceRoot, cache)) throw new IOException("The setup cache overlaps private data. Existing files were preserved.");
        var ownerPath = Path.Combine(cache, "setup-cache.owner.json"); SqliteSchema.RejectLink(ownerPath);
        var expected = new SetupCacheOwner(1, owner.InstallationId, installation.Root);
        if (Directory.Exists(cache))
        {
            if (!File.Exists(ownerPath) || new FileInfo(ownerPath).Length > 65536) throw new IOException("The external setup folder is not owned by this installation.");
            SetupCacheOwner? existing;
            try { existing = JsonSerializer.Deserialize<SetupCacheOwner>(await File.ReadAllTextAsync(ownerPath, ct)); }
            catch (JsonException error) { throw new IOException("External setup ownership is unreadable. Files were preserved.", error); }
            if (existing is null || existing.FormatVersion != expected.FormatVersion || existing.InstallationId != expected.InstallationId ||
                existing.InstallationRoot is null || !Path.IsPathFullyQualified(existing.InstallationRoot) || !Path.GetFullPath(existing.InstallationRoot).Equals(installation.Root, StringComparison.OrdinalIgnoreCase))
                throw new IOException("External setup ownership differs. Files were preserved.");
        }
        else { Directory.CreateDirectory(cache); await Workspace.AtomicWriteAsync(ownerPath, JsonSerializer.Serialize(expected)); }
        var packageRoot = Path.Combine(cache, id); SqliteSchema.RejectLink(packageRoot);
        ReleaseManifest package;
        if (Directory.Exists(packageRoot)) package = await ReleasePackage.ValidateAsync(packageRoot, ct);
        else
        {
            var source = Path.Combine(root, "releases", id); package = await ReleasePackage.ValidateAsync(source, ct);
            var required = checked(package.Files.Sum(f => f.Bytes) + 64 * 1048576L);
            if (new DriveInfo(Path.GetPathRoot(cache)!).AvailableFreeSpace < required) throw new IOException("There is insufficient local space for a verified external setup copy.");
            var stage = Path.Combine(cache, "stage-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
            foreach (var file in package.Files)
            {
                var input = Path.Combine(source, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)); var output = Path.Combine(stage, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(output)!); await InstalledLauncher.CopyVerifiedAsync(input, output, file, ct);
            }
            var manifest = Path.Combine(source, ReleasePackage.ManifestName);
            await InstalledLauncher.CopyVerifiedAsync(manifest, Path.Combine(stage, ReleasePackage.ManifestName), new(ReleasePackage.ManifestName, new FileInfo(manifest).Length, await Workspace.HashFileAsync(manifest, ct)), ct);
            if ((await ReleasePackage.ValidateAsync(stage, ct)).PackageId != id) throw new IOException("The external setup stage differs from the installed release.");
            Directory.Move(stage, packageRoot);
        }
        if (package.PackageId != id) throw new IOException("The external setup copy differs from its recorded identity.");
        return new(Path.Combine(packageRoot, "app", "CommuteCast.Desktop.exe"), ["--setup", "--install-root", installation.Root], id, true);
    }
}
