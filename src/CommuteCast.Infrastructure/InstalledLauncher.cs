using System.Text.Json;
using System.Text.RegularExpressions;

namespace CommuteCast.Infrastructure;

public record LauncherReceipt(int FormatVersion, string InstallationId, string PackageId, ReleaseFile File);
internal record LauncherJournal(int FormatVersion, string Id, string? OriginalReceiptHash, LauncherReceipt? Original, LauncherReceipt Proposed, string Stage);

/// <summary>Recoverable ownership of the stable launcher; installed release inventories remain immutable.</summary>
public static class InstalledLauncher
{
    public const string FileName = "CommuteCast.exe";
    public const string PackageFile = "app/CommuteCast.Launcher.exe";
    private const string ReceiptName = "launcher.owner.json", JournalName = "launcher.pending.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static bool Hash(string? value) => Regex.IsMatch(value ?? "", "^[A-F0-9]{64}$", RegexOptions.CultureInvariant);
    private static bool Id(string? value) => Regex.IsMatch(value ?? "", "^[a-f0-9]{32}$", RegexOptions.CultureInvariant);
    public static bool HasPending(string root)
    { var path = Path.Combine(root, JournalName); SqliteSchema.RejectLink(path); return File.Exists(path); }
    internal static async Task<string> PendingHashAsync(string root, CancellationToken ct)
    { var path = Path.Combine(root, JournalName); if (!HasPending(root)) return ""; if (new FileInfo(path).Length > 65536) throw new IOException("Launcher recovery records exceed their safe limit."); return await Workspace.HashFileAsync(path, ct); }
    private static Task Observe(IInstallationObserver? observer, InstallationCheckpoint checkpoint, CancellationToken ct) => observer?.ReachedAsync(checkpoint, null, ct) ?? Task.CompletedTask;
    private static async Task<T?> ReadAsync<T>(string path, CancellationToken ct)
    {
        SqliteSchema.RejectLink(path); if (!File.Exists(path)) return default;
        if (new FileInfo(path).Length > 65536) throw new IOException("Launcher ownership records exceed their safe limit. Files were preserved.");
        try { return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, ct)) ?? throw new JsonException(); }
        catch (JsonException error) { throw new IOException("Launcher ownership records are unreadable. Files were preserved.", error); }
    }
    private static void Check(LauncherReceipt receipt, InstallationOwner owner)
    {
        if (receipt.FormatVersion != 1 || receipt.InstallationId != owner.InstallationId || !Hash(receipt.PackageId) || receipt.File is null ||
            receipt.File.RelativePath != FileName || receipt.File.Bytes <= 0 || !Hash(receipt.File.Sha256)) throw new IOException("Launcher ownership differs from this installation. Files were preserved.");
    }
    private static void CheckPending(LauncherJournal pending, InstallationOwner owner)
    {
        if (pending.FormatVersion != 1 || !Id(pending.Id) || pending.Stage != "launcher-stage-" + pending.Id + ".exe" || pending.OriginalReceiptHash is not null && !Hash(pending.OriginalReceiptHash) || pending.Proposed is null)
            throw new IOException("Launcher recovery records are incompatible. Files were preserved.");
        Check(pending.Proposed, owner); if (pending.Original is not null) Check(pending.Original, owner);
        if ((pending.Original is null) != (pending.OriginalReceiptHash is null)) throw new IOException("Launcher recovery ownership is incomplete. Files were preserved.");
    }
    private static async Task CheckPendingReceiptAsync(string root, LauncherJournal pending, CancellationToken ct)
    {
        var path = Path.Combine(root, ReceiptName); SqliteSchema.RejectLink(path);
        var hash = File.Exists(path) ? await Workspace.HashFileAsync(path, ct) : null;
        if (hash != pending.OriginalReceiptHash && hash != CommuteCast.Core.Job.Hash(JsonSerializer.Serialize(pending.Proposed, Json))) throw new IOException("Launcher ownership changed outside recovery. Files were preserved.");
    }
    private static async Task<bool> MatchesAsync(string path, ReleaseFile file, CancellationToken ct)
    { SqliteSchema.RejectLink(path); return File.Exists(path) && new FileInfo(path).Length == file.Bytes && await Workspace.HashFileAsync(path, ct) == file.Sha256; }
    public static async Task<LauncherReceipt?> ReadReceiptAsync(string root, InstallationOwner owner, CancellationToken ct = default)
    {
        var receipt = await ReadAsync<LauncherReceipt>(Path.Combine(root, ReceiptName), ct);
        if (receipt is not null) Check(receipt, owner); return receipt;
    }
    internal static async Task<string> OverviewHashAsync(string root, InstallationOwner owner, CancellationToken ct)
    {
        var receipt = await ReadReceiptAsync(root, owner, ct); var path = Path.Combine(root, FileName); SqliteSchema.RejectLink(path);
        if (File.Exists(path) && receipt is null) throw new IOException("An unowned stable launcher was found. It was preserved.");
        if (File.Exists(path) && new FileInfo(path).Length > 512 * 1048576L) throw new IOException("The stable launcher exceeds the review limit. Files were preserved.");
        var receiptHash = receipt is null ? "" : await Workspace.HashFileAsync(Path.Combine(root, ReceiptName), ct);
        return receipt is null && !File.Exists(path) ? "" : CommuteCast.Core.Job.Hash(receiptHash + "|" + (File.Exists(path) ? await Workspace.HashFileAsync(path, ct) : "missing"));
    }
    public static async Task VerifyAsync(string root, InstallationOwner owner, CancellationToken ct = default)
    {
        if (HasPending(root)) throw new IOException("Launcher deployment is unfinished. Recover installation before launching.");
        var receipt = await ReadReceiptAsync(root, owner, ct) ?? throw new IOException("No owned stable launcher is recorded.");
        if (!await MatchesAsync(Path.Combine(root, FileName), receipt.File, ct)) throw new IOException("The stable launcher is missing or changed. Repair it with a verified installation package.");
    }
    internal static async Task<string> VerifyForSetupAsync(string root, InstallationOwner owner, CancellationToken ct)
    {
        var pending = await ReadAsync<LauncherJournal>(Path.Combine(root, JournalName), ct);
        if (pending is null) { await VerifyAsync(root, owner, ct); return (await ReadReceiptAsync(root, owner, ct))!.PackageId; }
        CheckPending(pending, owner); await CheckPendingReceiptAsync(root, pending, ct);
        var final = Path.Combine(root, FileName);
        if (File.Exists(final))
        {
            if (!await MatchesAsync(final, pending.Proposed.File, ct) && (pending.Original is null || !await MatchesAsync(final, pending.Original.File, ct))) throw new IOException("The interrupted launcher changed. It was preserved.");
        }
        else if (!await MatchesAsync(Path.Combine(root, pending.Stage), pending.Proposed.File, ct)) throw new IOException("No verified interrupted launcher is available. Use a complete portable setup package.");
        return pending.Proposed.PackageId;
    }
    internal static async Task RecoverAsync(string root, InstallationOwner owner, CancellationToken ct = default, IInstallationObserver? observer = null)
    {
        var journalPath = Path.Combine(root, JournalName); var pending = await ReadAsync<LauncherJournal>(journalPath, ct); if (pending is null) return;
        CheckPending(pending, owner); await CheckPendingReceiptAsync(root, pending, ct);
        var receiptPath = Path.Combine(root, ReceiptName); SqliteSchema.RejectLink(receiptPath);
        var proposedText = JsonSerializer.Serialize(pending.Proposed, Json);
        var final = Path.Combine(root, FileName); var stage = Path.Combine(root, pending.Stage);
        if (!await MatchesAsync(final, pending.Proposed.File, ct))
        {
            if (!await MatchesAsync(stage, pending.Proposed.File, ct)) throw new IOException("The verified launcher stage is missing or changed. Preserve it for inspection.");
            if (File.Exists(final))
            {
                if (pending.Original is null) throw new IOException("An unowned launcher file was found. It was preserved.");
                await OwnedFileRemoval.DeleteAsync(root, pending.Original.File, ct: ct);
            }
            await Observe(observer, InstallationCheckpoint.LauncherRemoved, ct);
            File.Move(stage, final, false);
            await Observe(observer, InstallationCheckpoint.LauncherActivated, ct);
        }
        await Workspace.AtomicWriteAsync(receiptPath, proposedText);
        await Observe(observer, InstallationCheckpoint.LauncherRecorded, ct);
        if (File.Exists(stage)) await OwnedFileRemoval.DeleteAsync(root, pending.Proposed.File with { RelativePath = pending.Stage }, ct: ct);
        File.Delete(journalPath);
    }
    internal static async Task EnsureAsync(string root, InstallationOwner owner, string packageRoot, ReleaseManifest package, CancellationToken ct = default, IInstallationObserver? observer = null)
    {
        await RecoverAsync(root, owner, ct);
        var sourceFile = package.Files.SingleOrDefault(f => f.RelativePath == PackageFile); if (sourceFile is null) return; // Existing portable releases remain rollback-compatible.
        var original = await ReadReceiptAsync(root, owner, ct); var final = Path.Combine(root, FileName); SqliteSchema.RejectLink(final);
        if (original is null && File.Exists(final)) throw new IOException("An unowned stable launcher occupies the installation folder. It was preserved.");
        if (original is not null && File.Exists(final) && !await MatchesAsync(final, original.File, ct)) throw new IOException("The owned stable launcher changed. It was preserved.");
        var proposed = new LauncherReceipt(1, owner.InstallationId, package.PackageId, sourceFile with { RelativePath = FileName });
        if (original == proposed && await MatchesAsync(final, proposed.File, ct)) return;
        var id = Guid.NewGuid().ToString("N"); var stageName = "launcher-stage-" + id + ".exe"; var stage = Path.Combine(root, stageName);
        await CopyVerifiedAsync(Path.Combine(packageRoot, PackageFile.Replace('/', Path.DirectorySeparatorChar)), stage, sourceFile, ct);
        var receiptPath = Path.Combine(root, ReceiptName); SqliteSchema.RejectLink(receiptPath);
        var journal = new LauncherJournal(1, id, File.Exists(receiptPath) ? await Workspace.HashFileAsync(receiptPath, ct) : null, original, proposed, stageName);
        await Workspace.AtomicWriteAsync(Path.Combine(root, JournalName), JsonSerializer.Serialize(journal, Json));
        await Observe(observer, InstallationCheckpoint.LauncherPrepared, ct);
        await RecoverAsync(root, owner, ct, observer);
    }
    internal static async Task CopyVerifiedAsync(string source, string destination, ReleaseFile file, CancellationToken ct)
    {
        SqliteSchema.RejectLink(source); SqliteSchema.RejectLink(destination);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough);
        if (input.Length != file.Bytes) throw new IOException("A deployment source changed. Existing files were preserved.");
        await input.CopyToAsync(output, ct); output.Flush(true); await output.DisposeAsync();
        if (!await MatchesAsync(destination, file, ct)) throw new IOException("A deployment copy differs from its verified inventory.");
    }
}
