using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommuteCast.Core;

namespace CommuteCast.Infrastructure;

public record RegistrationValue(string Name, string? Text, int? Number);
public record RegistrationReceipt(int FormatVersion, string InstallationId, string InstallationRoot, string AppVersion,
    IReadOnlyList<RegistrationValue> Values, IReadOnlyList<ReleaseFile> Shortcuts);
internal record RegistrationJournal(int FormatVersion, string Id, string? OriginalHash, RegistrationReceipt? Original,
    RegistrationReceipt? Proposed);

/// <summary>Per-user Windows integration. Each installation owns one key and two checksum-bound links.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsRegistration
{
    public const string FeatureFile = "app/windows-integration.json";
    public const string FeatureContent = "{\"formatVersion\":1}";
    private const string ReceiptName = "windows.owner.json", JournalName = "windows.pending.json";
    private static readonly string[] LinkNames = ["CommuteCast.lnk", "CommuteCast setup.lnk"];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string RegistryPath(InstallationOwner owner) => @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CommuteCast-" + owner.InstallationId;
    public static string ShortcutRoot(InstallationOwner owner) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "CommuteCast-" + owner.InstallationId);
    private static bool Hash(string? value) => Regex.IsMatch(value ?? "", "^[A-F0-9]{64}$", RegexOptions.CultureInvariant);
    private static bool Id(string? value) => Regex.IsMatch(value ?? "", "^[a-f0-9]{32}$", RegexOptions.CultureInvariant);
    private static IReadOnlyList<RegistrationValue> Values(string root, InstallationOwner owner, string version)
    {
        var launcher = Path.Combine(root, InstalledLauncher.FileName);
        return [new("DisplayName", "CommuteCast", null), new("DisplayVersion", version, null), new("InstallLocation", root, null),
            new("DisplayIcon", "\"" + launcher + "\",0", null), new("UninstallString", "\"" + launcher + "\" --setup", null),
            new("ModifyPath", "\"" + launcher + "\" --setup", null), new("NoRepair", null, 1),
            new("CommuteCastOwner", owner.InstallationId, null)];
    }
    private static void Check(RegistrationReceipt receipt, string root, InstallationOwner owner)
    {
        if (receipt.FormatVersion != 1 || !Id(owner.InstallationId) || receipt.InstallationId != owner.InstallationId || receipt.InstallationRoot != root ||
            string.IsNullOrWhiteSpace(receipt.AppVersion) || receipt.AppVersion.Length > 64 || receipt.Values is null ||
            !receipt.Values.SequenceEqual(Values(root, owner, receipt.AppVersion)) || receipt.Shortcuts is null || receipt.Shortcuts.Count != 2 ||
            receipt.Shortcuts.Where((f, i) => f is null || f.RelativePath != LinkNames[i] || f.Bytes is <= 0 or > 1048576 || !Hash(f.Sha256)).Any())
            throw new IOException("Windows integration ownership is incompatible. Existing entries were preserved.");
        Workspace.RejectReparsePoints(ShortcutRoot(owner));
    }
    private static async Task<T?> ReadAsync<T>(string path, CancellationToken ct)
    {
        SqliteSchema.RejectLink(path); if (!File.Exists(path)) return default;
        if (new FileInfo(path).Length > 65536) throw new IOException("Windows integration records exceed their safe limit.");
        try { return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, ct)) ?? throw new JsonException(); }
        catch (JsonException error) { throw new IOException("Windows integration records are unreadable. Entries were preserved.", error); }
    }
    private static async Task<RegistrationReceipt?> ReceiptAsync(string root, InstallationOwner owner, CancellationToken ct)
    { var receipt = await ReadAsync<RegistrationReceipt>(Path.Combine(root, ReceiptName), ct); if (receipt is not null) Check(receipt, root, owner); return receipt; }
    public static bool HasPending(string root)
    { var path = Path.Combine(root, JournalName); SqliteSchema.RejectLink(path); return File.Exists(path); }
    internal static async Task<string> PendingHashAsync(string root, CancellationToken ct)
    { if (!HasPending(root)) return ""; var path = Path.Combine(root, JournalName); if (new FileInfo(path).Length > 65536) throw new IOException("Windows integration recovery exceeds its safe limit."); return await Workspace.HashFileAsync(path, ct); }
    private static IReadOnlyList<RegistrationValue>? ReadValues(InstallationOwner owner)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath(owner), false); if (key is null) return null;
        if (key.SubKeyCount != 0 || key.ValueCount > 16) throw new IOException("Windows integration contains unrecognized entries. They were preserved.");
        var values = new List<RegistrationValue>();
        foreach (var name in key.GetValueNames().Order(StringComparer.Ordinal))
        {
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            values.Add(key.GetValueKind(name) switch
            {
                RegistryValueKind.String when value is string text && text.Length <= 8192 => new(name, text, null),
                RegistryValueKind.DWord when value is int number => new(name, null, number),
                _ => throw new IOException("Windows integration contains changed value types. They were preserved.")
            });
        }
        return values;
    }
    private static bool Equal(IReadOnlyList<RegistrationValue>? a, IReadOnlyList<RegistrationValue>? b) =>
        a is null ? b is null : b is not null && a.OrderBy(v => v.Name, StringComparer.Ordinal).SequenceEqual(b.OrderBy(v => v.Name, StringComparer.Ordinal));
    private static async Task<bool> MatchesAsync(string path, ReleaseFile file, CancellationToken ct)
    { SqliteSchema.RejectLink(path); return File.Exists(path) && new FileInfo(path).Length == file.Bytes && await Workspace.HashFileAsync(path, ct) == file.Sha256; }
    internal static async Task VerifyAsync(string root, InstallationOwner owner, CancellationToken ct)
    {
        var receipt = await ReceiptAsync(root, owner, ct); var actual = ReadValues(owner); var links = ShortcutRoot(owner); Workspace.RejectReparsePoints(links);
        if (receipt is null)
        {
            if (actual is not null || Directory.Exists(links) && Directory.EnumerateFileSystemEntries(links).Any()) throw new IOException("Unowned Windows integration occupies this installation's entry points. It was preserved.");
            return;
        }
        if (actual is not null && !Equal(actual, receipt.Values)) throw new IOException("Windows integration changed outside setup. Entries were preserved.");
        foreach (var file in receipt.Shortcuts)
        { var path = Path.Combine(links, file.RelativePath); SqliteSchema.RejectLink(path); if (Directory.Exists(path) || File.Exists(path) && !await MatchesAsync(path, file, ct)) throw new IOException("An owned shortcut changed. It was preserved."); }
    }
    internal static async Task<string> OverviewHashAsync(string root, InstallationOwner owner, CancellationToken ct)
    {
        var receipt = await ReceiptAsync(root, owner, ct); var registry = ReadValues(owner); var links = ShortcutRoot(owner); Workspace.RejectReparsePoints(links);
        if (receipt is null && registry is null && !HasPending(root) && !Directory.Exists(links)) return "";
        var hashes = new List<string>();
        foreach (var name in LinkNames)
        { var path = Path.Combine(links, name); SqliteSchema.RejectLink(path); if (Directory.Exists(path) || File.Exists(path) && new FileInfo(path).Length > 1048576) throw new IOException("A shortcut exceeds its review limit."); hashes.Add(File.Exists(path) ? await Workspace.HashFileAsync(path, ct) : "missing"); }
        return Job.Hash(JsonSerializer.Serialize(new { receiptHash = receipt is null ? "" : await Workspace.HashFileAsync(Path.Combine(root, ReceiptName), ct), registry, hashes }));
    }
    private static Task Observe(IInstallationObserver? observer, InstallationCheckpoint checkpoint, string? item, CancellationToken ct) => observer?.ReachedAsync(checkpoint, item, ct) ?? Task.CompletedTask;
    private static Task CreateShortcutAsync(string path, string root, bool setup)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            object? shell = null, link = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!);
                dynamic dispatch = shell!; link = dispatch.CreateShortcut(path); dynamic shortcut = link;
                shortcut.TargetPath = Path.Combine(root, InstalledLauncher.FileName); shortcut.Arguments = setup ? "--setup" : "";
                shortcut.WorkingDirectory = root; shortcut.WindowStyle = 1; shortcut.IconLocation = Path.Combine(root, InstalledLauncher.FileName) + ",0";
                shortcut.Description = setup ? "Review CommuteCast installation, recovery or removal" : "CommuteCast local narration";
                shortcut.Save(); completion.SetResult();
            }
            catch (Exception error) { completion.SetException(new IOException("Windows could not create the reviewed shortcut. Recover setup before launching.", error)); }
            finally { if (link is not null) Marshal.FinalReleaseComObject(link); if (shell is not null) Marshal.FinalReleaseComObject(shell); }
        }) { IsBackground = true }; thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    internal static async Task EnsureAsync(string root, InstallationOwner owner, string packageRoot, ReleaseManifest package, CancellationToken ct, IInstallationObserver? observer = null)
    {
        await RecoverAsync(root, owner, ct); var original = await ReceiptAsync(root, owner, ct);
        var feature = package.Files.SingleOrDefault(f => f.RelativePath == FeatureFile);
        if (feature is null && original is null) return; // Older releases keep their original deployment behavior.
        await ValidateFeatureAsync(packageRoot, package, ct);
        await InstalledLauncher.VerifyAsync(root, owner, ct); await VerifyAsync(root, owner, ct);
        if (original?.AppVersion == package.AppVersion && Equal(ReadValues(owner), original.Values) &&
            await MatchesAsync(Path.Combine(ShortcutRoot(owner), LinkNames[0]), original.Shortcuts[0], ct) && await MatchesAsync(Path.Combine(ShortcutRoot(owner), LinkNames[1]), original.Shortcuts[1], ct)) return;
        var id = Guid.NewGuid().ToString("N"); var files = new List<ReleaseFile>();
        for (var i = 0; i < LinkNames.Length; i++)
        {
            ct.ThrowIfCancellationRequested(); var stage = Path.Combine(root, Stage(id, i)); SqliteSchema.RejectLink(stage);
            if (File.Exists(stage) || Directory.Exists(stage)) throw new IOException("A shortcut staging path is already occupied. It was preserved.");
            await CreateShortcutAsync(stage, root, i == 1).WaitAsync(TimeSpan.FromSeconds(30), ct);
            if (new FileInfo(stage).Length is <= 0 or > 1048576) throw new IOException("Windows produced an invalid shortcut.");
            files.Add(new(LinkNames[i], new FileInfo(stage).Length, await Workspace.HashFileAsync(stage, ct)));
        }
        var proposed = new RegistrationReceipt(1, owner.InstallationId, root, package.AppVersion, Values(root, owner, package.AppVersion), files);
        await PrepareAsync(root, original, proposed, id, observer, ct); await RecoverAsync(root, owner, ct, observer);
    }
    private static string Stage(string id, int index) => "registration-stage-" + id + "-" + index + ".lnk";
    internal static async Task ValidateFeatureAsync(string packageRoot, ReleaseManifest package, CancellationToken ct)
    {
        var feature = package.Files.SingleOrDefault(f => f.RelativePath == FeatureFile); if (feature is null) return;
        if (feature.Bytes > 128 || !package.Files.Any(f => f.RelativePath == InstalledLauncher.PackageFile) ||
            await File.ReadAllTextAsync(Path.Combine(packageRoot, FeatureFile.Replace('/', Path.DirectorySeparatorChar)), ct) != FeatureContent)
            throw new IOException("The Windows integration declaration or stable launcher is incompatible.");
    }
    private static async Task PrepareAsync(string root, RegistrationReceipt? original, RegistrationReceipt? proposed, string id, IInstallationObserver? observer, CancellationToken ct)
    {
        var path = Path.Combine(root, ReceiptName); SqliteSchema.RejectLink(path);
        await Workspace.AtomicWriteAsync(Path.Combine(root, JournalName), JsonSerializer.Serialize(new RegistrationJournal(1, id, File.Exists(path) ? await Workspace.HashFileAsync(path, ct) : null, original, proposed), Json));
        await Observe(observer, InstallationCheckpoint.RegistrationPrepared, null, ct);
    }
    internal static async Task RemoveAsync(string root, InstallationOwner owner, CancellationToken ct, IInstallationObserver? observer = null)
    {
        await RecoverAsync(root, owner, ct); var original = await ReceiptAsync(root, owner, ct); if (original is null) return;
        await VerifyAsync(root, owner, ct); await PrepareAsync(root, original, null, Guid.NewGuid().ToString("N"), observer, ct); await RecoverAsync(root, owner, ct, observer);
    }
    internal static async Task RecoverAsync(string root, InstallationOwner owner, CancellationToken ct, IInstallationObserver? observer = null)
    {
        var pending = await ReadAsync<RegistrationJournal>(Path.Combine(root, JournalName), ct); if (pending is null) return;
        if (pending.FormatVersion != 1 || !Id(pending.Id) || pending.Original is null && pending.Proposed is null ||
            (pending.Original is null) != (pending.OriginalHash is null) || pending.OriginalHash is not null && !Hash(pending.OriginalHash)) throw new IOException("Windows integration recovery is incompatible.");
        if (pending.Original is not null) Check(pending.Original, root, owner); if (pending.Proposed is not null) Check(pending.Proposed, root, owner);
        var receiptPath = Path.Combine(root, ReceiptName); SqliteSchema.RejectLink(receiptPath);
        var receiptHash = File.Exists(receiptPath) ? await Workspace.HashFileAsync(receiptPath, ct) : null;
        var proposedHash = pending.Proposed is null ? null : Job.Hash(JsonSerializer.Serialize(pending.Proposed, Json));
        if (receiptHash != pending.OriginalHash && receiptHash != proposedHash) throw new IOException("Windows integration ownership changed outside recovery.");
        var oldValues = pending.Original?.Values ?? []; var newValues = pending.Proposed?.Values ?? [];
        void CheckRegistry()
        {
            var current = ReadValues(owner) ?? [];
            if (current.Any(v => !oldValues.Contains(v) && !newValues.Contains(v))) throw new IOException("Windows integration changed during recovery. Entries were preserved.");
        }
        CheckRegistry(); var links = ShortcutRoot(owner); Workspace.RejectReparsePoints(links);
        // Inspect every target and stage before deleting any owned entry.
        for (var i = 0; i < LinkNames.Length; i++)
        {
            var path = Path.Combine(links, LinkNames[i]); var stage = Path.Combine(root, Stage(pending.Id, i)); SqliteSchema.RejectLink(path); SqliteSchema.RejectLink(stage);
            var old = pending.Original?.Shortcuts[i]; var next = pending.Proposed?.Shortcuts[i];
            if (Directory.Exists(path) || File.Exists(path) && !(old is not null && await MatchesAsync(path, old, ct)) && !(next is not null && await MatchesAsync(path, next, ct))) throw new IOException("A shortcut changed during recovery. It was preserved.");
            if (Directory.Exists(stage) || File.Exists(stage) && (next is null || !await MatchesAsync(stage, next, ct))) throw new IOException("A staged shortcut changed. It was preserved.");
            if (next is not null && !await MatchesAsync(path, next, ct) && !await MatchesAsync(stage, next, ct)) throw new IOException("The verified shortcut stage is missing. Existing entries were preserved.");
        }
        foreach (var name in oldValues.Concat(newValues).Select(v => v.Name).Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested(); CheckRegistry(); var next = newValues.SingleOrDefault(v => v.Name == name);
            using var key = next is null ? Registry.CurrentUser.OpenSubKey(RegistryPath(owner), true) : Registry.CurrentUser.CreateSubKey(RegistryPath(owner), true);
            if (next is null) key?.DeleteValue(name, false);
            else key!.SetValue(name, next.Text is null ? next.Number!.Value : next.Text, next.Text is null ? RegistryValueKind.DWord : RegistryValueKind.String);
            key?.Flush(); await Observe(observer, InstallationCheckpoint.RegistrationValueWritten, name, ct);
        }
        if (pending.Proposed is null)
        {
            CheckRegistry(); using var key = Registry.CurrentUser.OpenSubKey(RegistryPath(owner), false);
            if (key is not null && key.ValueCount == 0 && key.SubKeyCount == 0) Registry.CurrentUser.DeleteSubKey(RegistryPath(owner), false);
        }
        else Directory.CreateDirectory(links);
        for (var i = 0; i < LinkNames.Length; i++)
        {
            var path = Path.Combine(links, LinkNames[i]); var stage = Path.Combine(root, Stage(pending.Id, i)); var old = pending.Original?.Shortcuts[i]; var next = pending.Proposed?.Shortcuts[i];
            if (next is null || !await MatchesAsync(path, next, ct))
            {
                if (File.Exists(path)) { if (old is null) throw new IOException("An unowned shortcut appeared. It was preserved."); await OwnedFileRemoval.DeleteAsync(links, old, ct: ct); }
                await Observe(observer, InstallationCheckpoint.RegistrationShortcutRemoved, LinkNames[i], ct);
                if (next is not null) { SqliteSchema.RejectLink(stage); SqliteSchema.RejectLink(path); File.Move(stage, path, false); await Observe(observer, InstallationCheckpoint.RegistrationShortcutActivated, LinkNames[i], ct); }
            }
            if (File.Exists(stage) && next is not null) await OwnedFileRemoval.DeleteAsync(root, next with { RelativePath = Stage(pending.Id, i) }, ct: ct);
        }
        if (pending.Proposed is null) { File.Delete(receiptPath); if (Directory.Exists(links) && !Directory.EnumerateFileSystemEntries(links).Any()) Directory.Delete(links, false); }
        else await Workspace.AtomicWriteAsync(receiptPath, JsonSerializer.Serialize(pending.Proposed, Json));
        await Observe(observer, InstallationCheckpoint.RegistrationRecorded, null, ct); File.Delete(Path.Combine(root, JournalName));
    }
}
