using CommuteCast.Core;

namespace CommuteCast.Infrastructure;

public record StorageUsage(long TotalBytes, long CacheBytes, long ReclaimableBytes);
public record CleanupResult(long BytesRemoved, int FilesRemoved, int Failures);

public sealed class CacheMaintenance(Workspace workspace)
{
    private static bool IsCache(string name) => name is "inference.partial.wav" or "normalized.partial.wav" or "assembled.wav" or "encoded.partial.mp3" ||
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
    private async Task<HashSet<string>> EligibleAsync(IReadOnlyList<Job> jobs, string? activeId, CancellationToken ct)
    {
        var eligible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs.Where(j => j.Id != activeId && !j.DeletionRequested))
        {
            ct.ThrowIfCancellationRequested();
            var directory = workspace.JobDirectory(job.Id);
            if (!Directory.Exists(directory)) continue;
            Workspace.RejectReparsePoints(directory);
            // A complete private file is retained even if the export disappears. Sources/history never qualify.
            var finished = job.ExportCommitted && job.FinalHash.Length > 0 && File.Exists(workspace.FinalPath(job)) &&
                await Workspace.HashFileAsync(workspace.FinalPath(job), ct) == job.FinalHash;
            var protectedChunks = job.Receipts.Select(r => workspace.ChunkPath(job, r.Index)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                var file = new FileInfo(path);
                if (!file.Attributes.HasFlag(FileAttributes.ReparsePoint) && IsCache(file.Name) && (finished || !protectedChunks.Contains(path))) eligible.Add(path);
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
            if (eligible.Contains(file.FullName)) reclaimable += file.Length;
        }
        return new(total, cache, reclaimable);
    }
    public async Task<CleanupResult> CleanAsync(IReadOnlyList<Job> jobs, string? activeId, int quotaMiB, int ageDays, CancellationToken ct)
    {
        if (quotaMiB is < 1 or > 102400 || ageDays is < 1 or > 3650) throw new ArgumentException("Use a cache quota of 1–102400 MiB and retention of 1–3650 days.");
        var eligible = await EligibleAsync(jobs, activeId, ct);
        var usage = await MeasureAsync(jobs, activeId, ct);
        var cutoff = DateTime.UtcNow.AddDays(-ageDays); var quota = quotaMiB * 1024L * 1024;
        long removed = 0; var count = 0; var failures = 0;
        foreach (var file in eligible.Select(p => new FileInfo(p)).OrderBy(f => f.LastWriteTimeUtc))
        {
            ct.ThrowIfCancellationRequested();
            if (file.LastWriteTimeUtc >= cutoff && usage.CacheBytes - removed <= quota) continue;
            try
            {
                Workspace.RejectReparsePoints(file.DirectoryName!);
                file.Refresh();
                if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                var size = file.Length;
                // Exact known filenames only; no recursive deletion, folder sweep, or export access.
                File.Delete(file.FullName); removed += size; count++;
            }
            catch (IOException) { failures++; }
            catch (UnauthorizedAccessException) { failures++; }
        }
        return new(removed, count, failures);
    }
}
