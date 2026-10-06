using CommuteCast.Infrastructure;
using System.Text.Json;

// Deliberately synchronous host: the compatibility mutex is released on its owning thread.
return Run(args);

static int Run(string[] arguments)
{
    if (arguments.Length == 0 || arguments.SequenceEqual(new[] { "--help" }))
    {
        Console.WriteLine("CommuteCast local maintenance 0.1.0\nClose CommuteCast before backup, restore or recovery.\n\nbackup [--root <private workspace>]\nvalidate-backup --backup <completed backup folder>\nrestore --backup <completed backup folder> --confirm-replace-local-data [--root <private workspace>]\nrecover [--root <private workspace>]\nseal-package --package <complete portable folder>\nverify-package --package <complete portable folder>\n\nPackage sealing is an explicit build action. Verification checks inventory/integrity, not publisher trust or corporate signing approval.\nBackups contain private source, draft, settings and job audio. They stay under the private workspace/backups folder. Restore retains previous local state and never modifies exported MP3s or Docker artifacts. Historical backups/recovery copies and provisioning models are excluded from a current-state backup.");
        return 0;
    }
    Mutex? legacy = null; var ownsLegacy = false;
    try
    {
        var command = arguments[0];
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
        root ??= defaultRoot;
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
    finally { if (ownsLegacy) legacy!.ReleaseMutex(); legacy?.Dispose(); }
}
static void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
