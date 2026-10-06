using System.Text.Json;

namespace CommuteCast.Infrastructure;

public record InstalledLaunchPlan(string Executable, IReadOnlyList<string> Arguments, string PackageId, bool ExternalSetup);
internal record SetupCacheOwner(int FormatVersion, string InstallationId, string InstallationRoot);

public static class LauncherPlan
{
    public static string SetupCacheRoot(string root, InstallationOwner owner) => Path.Combine(Directory.GetParent(Path.GetFullPath(root))!.FullName, "CommuteCast-setup-" + owner.InstallationId);
    public static async Task<InstalledLaunchPlan> CreateAsync(string root, bool setup = false, bool maintenance = false, CancellationToken ct = default, IInstallationObserver? observer = null)
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
        var cache = (await SetupCache.InspectOwnerAsync(installation.Root, owner, true, ct)).Root;
        using var cacheUse = SetupCache.AcquireUse(cache, id);
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
            if (observer is not null) await observer.ReachedAsync(InstallationCheckpoint.SetupCopyVerified, stage, ct);
            await PublishCopyAsync(stage, packageRoot, id, observer, ct);
        }
        if (package.PackageId != id) throw new IOException("The external setup copy differs from its recorded identity.");
        return new(Path.Combine(packageRoot, "app", "CommuteCast.Desktop.exe"), ["--setup", "--install-root", installation.Root], id, true);
    }
    private static async Task PublishCopyAsync(string stage, string target, string id, IInstallationObserver? observer, CancellationToken ct)
    {
        // An antivirus/scanner may briefly retain a directory handle after copy validation.
        // Retain the stage on exhaustion; never overwrite a competing target or trust changed bytes.
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested(); SqliteSchema.RejectLink(stage); SqliteSchema.RejectLink(target);
            if ((await ReleasePackage.ValidateAsync(stage, ct)).PackageId != id) throw new IOException("The external setup copy changed before publication. It was preserved for inspection.");
            try { Directory.Move(stage, target); return; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException && OperatingSystem.IsWindows() && attempt < 3 &&
                !Directory.Exists(target) && !File.Exists(target) && (error.HResult & 0xffff) is 5 or 32)
            {
                if (observer is not null) await observer.ReachedAsync(InstallationCheckpoint.SetupCopyRenameRetry, stage, ct);
                await Task.Delay(100 * (attempt + 1), ct);
            }
        }
    }
}
