using CommuteCast.Core;

namespace CommuteCast.Infrastructure;

public record StorageUsage(long TotalBytes, long CacheBytes, long ReclaimableBytes, long ReservedBytes = 0);
public record CleanupResult(long BytesRemoved, int FilesRemoved, int Failures);

public sealed class CacheMaintenance(Workspace workspace)
{
    private static bool IsCache(string name) => name is "inference.partial.wav" or "normalized.partial.wav" or "assembled.wav" or "encoded.partial.mp3" ||
        System.Text.RegularExpressions.Regex.IsMatch(name, "^inference\\.partial\\.wav\\.attempt-[a-f0-9]{32}\\.partial$", System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
        name.StartsWith("chunk-", StringComparison.Ordinal) && name.EndsWith(".wav", StringComparison.Ordinal);

    private IEnumerable<FileInfo> ManagedFiles(CancellationToken ct)
    {
        var directories = new Stack<string>(); directories.Push(workspace.Root);
        while (directories.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested(); Workspace.RejectReparsePoints(directory);
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                var file = new FileInfo(path);
                if (!file.Attributes.HasFlag(FileAttributes.ReparsePoint)) yield return file;
            }
            foreach (var path in Directory.EnumerateDirectories(directory))
                if (!File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) directories.Push(path);
        }
    }
    private async Task<Dictionary<string, string>> EligibleAsync(IReadOnlyList<Job> jobs, string? activeId, CancellationToken ct)
    {
        var eligible = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs.Where(j => j.Id != activeId && !j.DeletionRequested))
        {
            ct.ThrowIfCancellationRequested();
            // Startup/offline maintenance must reconcile identity-bearing rename intent first.
            if (job.PrivateArtifacts.Any(r => r.PromotionIdentity is not null)) continue;
            var directory = workspace.JobDirectory(job.Id);
            if (!Directory.Exists(directory)) continue;
            Workspace.RejectReparsePoints(directory);
            // A complete private file is retained even if the export disappears. Sources/history never qualify.
            var finished = job.ExportCommitted && job.FinalHash.Length > 0 && File.Exists(workspace.FinalPath(job)) &&
                await Workspace.HashFileAsync(workspace.FinalPath(job), ct) == job.FinalHash;
            var protectedChunks = job.Receipts.Select(r => workspace.ChunkPath(job, r.Index)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var receipt in PrivateJobFiles.Inventory(job))
            {
                var path = OwnedFileRemoval.Resolve(directory, receipt.Key);
                var file = new FileInfo(path);
                if (file.Exists && !file.Attributes.HasFlag(FileAttributes.ReparsePoint) && IsCache(file.Name) && (finished || !protectedChunks.Contains(path))) eligible.Add(path, receipt.Value);
            }
        }
        return eligible;
    }
    public async Task<StorageUsage> MeasureAsync(IReadOnlyList<Job> jobs, string? activeId, CancellationToken ct)
    {
        var eligible = await EligibleAsync(jobs, activeId, ct);
        long total = 0, cache = 0, reclaimable = 0;
        foreach (var file in ManagedFiles(ct))
        {
            total += file.Length;
            if (IsCache(file.Name) && Workspace.IsWithin(Path.Combine(workspace.Root, "jobs"), file.FullName)) cache += file.Length;
            if (eligible.ContainsKey(file.FullName)) reclaimable += file.Length;
        }
        long reserved = 0;
        foreach (var job in jobs.Where(j => j.Stage is not (JobStage.Exported or JobStage.Cancelled or JobStage.Failed or JobStage.Deleting)))
        {
            var directory = workspace.JobDirectory(job.Id);
            Workspace.RejectReparsePoints(directory);
            var existing = Directory.Exists(directory) ? Directory.EnumerateFiles(directory).Select(p => new FileInfo(p)).Where(f => !f.Attributes.HasFlag(FileAttributes.ReparsePoint)).Sum(f => f.Length) : 0;
            reserved += Math.Max(0, StorageBudget.Estimate(job.Prepared.Script) - existing);
        }
        return new(total, cache, reclaimable, reserved);
    }
    public async Task<CleanupResult> CleanAsync(IReadOnlyList<Job> jobs, string? activeId, int quotaMiB, int ageDays, CancellationToken ct)
    {
        if (quotaMiB is < 1 or > 102400 || ageDays is < 1 or > 3650) throw new ArgumentException("Use a cache quota of 1–102400 MiB and retention of 1–3650 days.");
        var eligible = await EligibleAsync(jobs, activeId, ct);
        var usage = await MeasureAsync(jobs, activeId, ct);
        var cutoff = DateTime.UtcNow.AddDays(-ageDays); var quota = quotaMiB * 1024L * 1024;
        long removed = 0; var count = 0; var failures = 0;
        foreach (var file in eligible.Keys.Select(p => new FileInfo(p)).OrderBy(f => f.LastWriteTimeUtc))
        {
            ct.ThrowIfCancellationRequested();
            if (file.LastWriteTimeUtc >= cutoff && usage.CacheBytes - removed <= quota) continue;
            try
            {
                Workspace.RejectReparsePoints(file.DirectoryName!);
                file.Refresh();
                if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                var size = file.Length;
                if (await OwnedFileRemoval.DeleteByHashAsync(file.DirectoryName!, file.Name, eligible[file.FullName], ct: ct))
                { removed += size; count++; }
            }
            catch (IOException) { failures++; }
            catch (UnauthorizedAccessException) { failures++; }
        }
        return new(removed, count, failures);
    }
}

public static class StorageBudget
{
    public static long Estimate(string script)
    {
        var words = script.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LongLength;
        var equivalentWords = Math.Max(words, script.Length / 6L);
        return Math.Max(1048576L, equivalentWords * 120000L * 3); // 2.5 seconds at 48 kB/s, three lossless copies.
    }
    public static void EnsureFits(StorageUsage usage, string? script, int limitMiB, long availableFreeBytes)
    {
        if (limitMiB is < 128 or > 1048576) throw new ArgumentException("Use a private storage limit of 128–1048576 MiB.");
        var requested = script is null ? 0 : Estimate(script);
        if (usage.TotalBytes + usage.ReservedBytes + requested > limitMiB * 1048576L)
            throw new IOException("This narration and queued work exceed the private storage budget. Clean eligible cache, delete older narrations, or raise the limit in Settings. Your draft and saved jobs are retained.");
        if (availableFreeBytes < usage.ReservedBytes + requested + 512 * 1048576L)
            throw new IOException("Private storage has insufficient free space for this narration and queued work. Free disk space and retry; your draft and saved jobs are retained.");
    }
}
