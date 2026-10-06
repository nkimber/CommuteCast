using CommuteCast.Infrastructure;
using System.Text.Json;

// Deliberately synchronous host: the compatibility mutex is released on its owning thread.
return Run(args);

static int Run(string[] arguments)
{
    if (arguments.Length == 0 || arguments.SequenceEqual(new[] { "--help" }))
    {
        Console.WriteLine("CommuteCast local maintenance 0.1.0\nClose CommuteCast before backup, restore or recovery.\n\nbackup [--root <private workspace>]\nvalidate-backup --backup <completed backup folder>\nrestore --backup <completed backup folder> --confirm-replace-local-data [--root <private workspace>]\nrecover [--root <private workspace>]\nseal-package --package <complete portable folder>\nverify-package --package <complete portable folder>\n\nPackage sealing is an explicit build action. Verification checks inventory/integrity, not publisher trust or corporate signing approval.\nBackups contain private source, draft, settings and job audio. They stay under the private workspace/backups folder. Restore retains previous local state and never modifies exported MP3s or Docker artifacts. Historical backups/recovery copies and provisioning models are excluded from a current-state backup.");
        Console.WriteLine("\nRead-only setup: check-setup [--root <private workspace>]\n\nInstallation commands (close the app first):\ninstall --package <complete portable folder> [--install-root <local folder>]\ninspect-install [--install-root <local folder>]\nlaunch-installed [--install-root <local folder>]\nrollback --confirm-replace-local-data [--install-root <local folder>]\nuninstall --confirm-uninstall [--local-data retain|remove] [--confirm-remove-local-data] [--install-root <local folder>]\nrecover-install [--install-root <local folder>]\n\nDefault binaries: %LOCALAPPDATA%\\Programs\\CommuteCast. Private data is bound to the installation and stays separate. --root is an explicit isolated-workspace override for installation testing. Uninstall preserves exports, Docker and provisioning models; unknown root files remain for inspection.");
        Console.WriteLine("\nSetup distribution cleanup:\nreview-setup-cache [--install-root <local folder>]\nclean-setup-cache --review-fingerprint <hash from review> --confirm-remove-setup-copies [--install-root <local folder>]\n\nThe active recovery kit, running sources and unrecognized entries are kept. After uninstall, run cleanup from a separate portable tool to reclaim all unused setup copies. Coordination ownership/use records remain; private narration, exports and installed releases are separate.");
        return 0;
    }
    Mutex? legacy = null; var ownsLegacy = false; CachedSetupUse? cachedUse = null;
    try
    {
        cachedUse = SetupCache.AcquireHostUseAsync(AppContext.BaseDirectory).GetAwaiter().GetResult();
        var command = arguments[0];
        if (command == "check-setup")
        {
            if (arguments.Length is not (1 or 3) || arguments.Length == 3 && arguments[1] != "--root") throw new ArgumentException("check-setup [--root <private workspace>]");
            var setupRoot = Path.GetFullPath(arguments.Length == 3 ? arguments[2] : cachedUse?.PrivateRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CommuteCast"));
            Workspace.RejectReparsePoints(setupRoot);
            var setupSettings = Directory.Exists(setupRoot) ? new Workspace(setupRoot).LoadSettingsAsync().GetAwaiter().GetResult() : new CommuteCast.Core.AppSettings();
            Print(new SetupDiagnostics(new SetupRuntime(setupRoot)).CheckAsync(setupSettings).GetAwaiter().GetResult()); return 0;
        }
        if (command is "install" or "rollback" or "uninstall" or "recover-install" or "inspect-install" or "launch-installed" or "review-setup-cache" or "clean-setup-cache") return RunInstallation(arguments, cachedUse?.InstallationRoot);
        if (command is "seal-package" or "verify-package")
        {
            if (arguments.Length != 3 || arguments[1] != "--package") throw new ArgumentException("Choose one complete portable folder with --package.");
            var package = command == "seal-package" ? ReleasePackage.SealAsync(arguments[2]).GetAwaiter().GetResult() : ReleasePackage.ValidateAsync(arguments[2]).GetAwaiter().GetResult();
            Print(new { verified = true, package.PackageId, package.AppVersion, package.DesktopBuild, package.MaintenanceBuild, package.BundledRuntime, package.Target, package.MinimumSchema, package.MaximumSchema, package.ProviderContract, files = package.Files.Count, publisherTrust = "not established by checksums" }); return 0;
        }
        if (command is not ("backup" or "validate-backup" or "restore" or "recover")) throw new ArgumentException("Unknown maintenance command. Use --help.");
        string? root = null, backup = null; var confirmed = false;
        for (var i = 1; i < arguments.Length; i++)
        {
            switch (arguments[i])
            {
                case "--root" when root is null && i + 1 < arguments.Length: root = Path.GetFullPath(arguments[++i]); break;
                case "--backup" when backup is null && i + 1 < arguments.Length: backup = Path.GetFullPath(arguments[++i]); break;
                case "--confirm-replace-local-data" when !confirmed: confirmed = true; break;
                default: throw new ArgumentException("Invalid or duplicate maintenance option. Use --help.");
            }
        }
        if (command is "restore" or "validate-backup" && backup is null) throw new ArgumentException("Choose a completed backup with --backup.");
        if (command == "restore" && !confirmed) throw new ArgumentException("Restore replaces the current local queue, draft, settings and job files. Explicit --confirm-replace-local-data is required. Exported MP3s are separate and remain unchanged.");
        if (command != "restore" && confirmed || command is "backup" or "recover" && backup is not null || command == "validate-backup" && root is not null) throw new ArgumentException("The option does not apply to this maintenance command. Use --help.");
        if (command == "validate-backup")
        {
            var validated = WorkspaceBackup.ValidateAsync(backup!).GetAwaiter().GetResult();
            Print(new { valid = true, validated.BackupId, validated.SchemaVersion, files = validated.Files.Count }); return 0;
        }
        var defaultRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CommuteCast");
        root ??= cachedUse?.PrivateRoot ?? defaultRoot;
        if (root.StartsWith("\\\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(root)!).DriveType == DriveType.Network) throw new IOException("Use a local nonsynced private workspace for maintenance.");
        if (OperatingSystem.IsWindows() && root.TrimEnd(Path.DirectorySeparatorChar).Equals(defaultRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            // Keep compatibility with earlier desktop builds which do not yet hold the file lease.
            legacy = new Mutex(false, "Local\\CommuteCast-" + Environment.UserName);
            try { ownsLegacy = legacy.WaitOne(0); } catch (AbandonedMutexException) { ownsLegacy = true; }
            if (!ownsLegacy) throw new IOException("CommuteCast is open for this Windows user. Close it before maintenance; no private state was changed.");
        }
        var workspace = new Workspace(root); using var lease = WorkspaceLease.Acquire(workspace);
        switch (command)
        {
            case "backup":
                var path = WorkspaceBackup.CreateAsync(lease).GetAwaiter().GetResult();
                var manifest = WorkspaceBackup.ValidateAsync(path).GetAwaiter().GetResult();
                Print(new { backup = path, manifest.BackupId, manifest.SchemaVersion, files = manifest.Files.Count, containsPrivateContent = true }); break;
            case "restore":
                try
                {
                    var result = WorkspaceBackup.RestoreAsync(lease, backup!).GetAwaiter().GetResult();
                    Print(new { restored = true, result.RestoreId, result.PreviousState, exportedFiles = "unchanged", dockerArtifacts = "unchanged" });
                }
                catch
                {
                    try { WorkspaceBackup.RecoverInterruptedAsync(lease).GetAwaiter().GetResult(); }
                    catch (Exception error) { Console.Error.WriteLine("Automatic restore recovery needs inspection: " + QueueCoordinator.FriendlyError(error)); }
                    throw;
                }
                break;
            case "recover": Print(new { recoveredInterruptedRestore = WorkspaceBackup.RecoverInterruptedAsync(lease).GetAwaiter().GetResult() }); break;
        }
        return 0;
    }
    catch (Exception error) { Console.Error.WriteLine(QueueCoordinator.FriendlyError(error)); return 1; }
    finally { if (ownsLegacy) legacy!.ReleaseMutex(); legacy?.Dispose(); cachedUse?.Dispose(); }
}
static void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

static int RunInstallation(string[] arguments, string? cachedInstallRoot = null)
{
    string? root = null, installRoot = null, package = null, cacheFingerprint = null; var confirmedReplacement = false; var confirmedRemoval = false; var confirmedUninstall = false; var confirmedCacheCleanup = false; var localData = "retain"; var dataOption = false;
    for (var i = 1; i < arguments.Length; i++)
    {
        switch (arguments[i])
        {
            case "--root" when root is null && i + 1 < arguments.Length: root = Path.GetFullPath(arguments[++i]); break;
            case "--install-root" when installRoot is null && i + 1 < arguments.Length: installRoot = Path.GetFullPath(arguments[++i]); break;
            case "--package" when package is null && i + 1 < arguments.Length: package = Path.GetFullPath(arguments[++i]); break;
            case "--confirm-replace-local-data" when !confirmedReplacement: confirmedReplacement = true; break;
            case "--confirm-remove-local-data" when !confirmedRemoval: confirmedRemoval = true; break;
            case "--confirm-uninstall" when !confirmedUninstall: confirmedUninstall = true; break;
            case "--confirm-remove-setup-copies" when !confirmedCacheCleanup: confirmedCacheCleanup = true; break;
            case "--review-fingerprint" when cacheFingerprint is null && i + 1 < arguments.Length: cacheFingerprint = arguments[++i]; break;
            case "--local-data" when !dataOption && i + 1 < arguments.Length: localData = arguments[++i]; dataOption = true; break;
            default: throw new ArgumentException("Invalid or duplicate installation option. Use --help.");
        }
    }
    var command = arguments[0];
    if (command == "clean-setup-cache" ? !confirmedCacheCleanup || !System.Text.RegularExpressions.Regex.IsMatch(cacheFingerprint ?? "", "^[A-F0-9]{64}$") : confirmedCacheCleanup || cacheFingerprint is not null) throw new ArgumentException("Only clean-setup-cache requires --review-fingerprint from a current review and --confirm-remove-setup-copies; review-setup-cache displays verified unused distribution counts. Private narration and exports remain separate.");
    if (command == "install" ? package is null : package is not null) throw new ArgumentException("Only install accepts and requires --package with a verified complete portable folder.");
    if (command == "rollback" ? !confirmedReplacement : confirmedReplacement) throw new ArgumentException("Only rollback requires --confirm-replace-local-data; it restores the recorded pre-update queue, draft, settings and job artifacts.");
    if (command != "uninstall" && (confirmedRemoval || confirmedUninstall || dataOption) || command == "uninstall" && (!confirmedUninstall || localData is not ("retain" or "remove") || (localData == "remove") != confirmedRemoval))
        throw new ArgumentException("Uninstall requires --confirm-uninstall; choose --local-data retain or remove. Removal also requires --confirm-remove-local-data. Exported files and engine artifacts remain separate.");
    var installation = new Installation(installRoot ?? cachedInstallRoot);
    if (command == "review-setup-cache")
    {
        if (root is not null && !installation.ReadOwnerAsync().GetAwaiter().GetResult().WorkspaceRoot.Equals(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("The requested private folder differs from the installation binding.");
        var review = installation.ReviewSetupCacheAsync().GetAwaiter().GetResult(); Print(new { review.InstallationRoot, review.CacheRoot, review.Copies, review.Files, review.Bytes, review.PackageIds, review.Preserved, review.Fingerprint, changes = "none" }); return 0;
    }
    var ownerFile = Path.Combine(installation.Root, "installation.owner.json");
    if (root is null && File.Exists(ownerFile)) root = installation.ReadOwnerAsync().GetAwaiter().GetResult().WorkspaceRoot;
    var defaultRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CommuteCast"); root ??= defaultRoot;
    if (root.StartsWith("\\\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(root)!).DriveType == DriveType.Network) throw new IOException("Use a local nonsynced private workspace.");
    Mutex? legacy = null; var ownsLegacy = false; string? executable = null;
    try
    {
        if (OperatingSystem.IsWindows() && root.TrimEnd(Path.DirectorySeparatorChar).Equals(defaultRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            legacy = new Mutex(false, "Local\\CommuteCast-" + Environment.UserName);
            try { ownsLegacy = legacy.WaitOne(0); } catch (AbandonedMutexException) { ownsLegacy = true; }
            if (!ownsLegacy) throw new IOException("CommuteCast is open for this Windows user. Close it before installation maintenance; no private state was changed.");
        }
        using var lease = WorkspaceLease.Acquire(new Workspace(root));
        if (command == "recover-install") { Print(new { recovered = installation.RecoverAsync(lease).GetAwaiter().GetResult() }); return 0; }
        if (command == "clean-setup-cache")
        {
            var review = installation.ReviewSetupCacheAsync().GetAwaiter().GetResult();
            if (review.Fingerprint != cacheFingerprint) throw new IOException("The reviewed setup cleanup scope changed. Run review-setup-cache again; no removal was committed.");
            var cleanedCache = installation.CleanSetupCacheAsync(lease, review, true).GetAwaiter().GetResult();
            Print(new { removed = cleanedCache, review.Preserved, privateData = "unchanged", installedReleases = "unchanged", exportedFiles = "unchanged", engineArtifacts = "unchanged" }); return 0;
        }
        InstallationResult result;
        SetupReport? setup = null;
        try
        {
            if (command == "install") setup = new SetupDiagnostics(new SetupRuntime(root)).CheckAsync(lease.Workspace.LoadSettingsAsync().GetAwaiter().GetResult()).GetAwaiter().GetResult();
            result = command switch
            {
                "install" => installation.ActivateAsync(lease, package!).GetAwaiter().GetResult(),
                "rollback" => installation.RollbackAsync(lease).GetAwaiter().GetResult(),
                "uninstall" => installation.UninstallAsync(lease, localData == "remove").GetAwaiter().GetResult(),
                _ => installation.InspectAsync(lease).GetAwaiter().GetResult()
            };
        }
        catch
        {
            if (command is "install" or "rollback" or "uninstall" && File.Exists(ownerFile))
                try { installation.RecoverAsync(lease).GetAwaiter().GetResult(); }
                catch (Exception error) { Console.Error.WriteLine("Deployment recovery needs inspection: " + QueueCoordinator.FriendlyError(error)); }
            throw;
        }
        Print(new { operation = command, result.State, result.Executable, result.AlreadyCurrent, setup, localData = command == "uninstall" ? localData : "preserved or restored from recorded snapshot", exportedFiles = "unchanged", engineArtifacts = "unchanged", publisherTrust = "corporate signing approval remains external" });
        if (command == "launch-installed") executable = result.Executable ?? throw new IOException("This installation is uninstalled. Install a verified release first.");
    }
    finally { if (ownsLegacy) legacy!.ReleaseMutex(); legacy?.Dispose(); }
    // Release maintenance exclusion before the child's startup acquires its own lease/mutex.
    // Startup rechecks that this is still the active installed release.
    if (executable is not null) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Path.GetDirectoryName(executable))!, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden });
    return 0;
}
