using CommuteCast.Core;

namespace CommuteCast.Infrastructure;

/// <summary>Receipts for private files, independent of whether their audio is valid for reuse.</summary>
public static class PrivateJobFiles
{
    public static IReadOnlyDictionary<string, string> Inventory(Job job)
    {
        if (job.PrivateArtifacts is null || job.PrivateArtifacts.Count > 4096 || job.Receipts.Count > 4096)
            throw new IOException("The private artifact inventory is invalid. Files were preserved.");
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, string hash)
        {
            // Only job-relative files are admitted; unknown directories are never swept.
            if (name is null || name.Contains('/') || name.Length > 200 || hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                throw new IOException("A private artifact receipt is invalid. Files were preserved.");
            OwnedFileRemoval.ValidateRelativePath(name);
            hash = hash.ToUpperInvariant();
            if (files.TryGetValue(name, out var previous) && previous != hash)
                throw new IOException("Private artifact receipts disagree. Files were preserved.");
            files[name] = hash;
        }
        foreach (var receipt in job.PrivateArtifacts)
        {
            if (receipt is null) throw new IOException("A private artifact receipt is missing. Files were preserved.");
            Add(receipt.RelativePath, receipt.Hash);
        }
        foreach (var receipt in job.Receipts)
        {
            if (receipt.Index is < 0 or > 99999) throw new IOException("A private chunk receipt is invalid. Files were preserved.");
            Add($"chunk-{receipt.Index:D5}.wav", receipt.Hash);
        }
        if (!string.IsNullOrEmpty(job.FinalHash)) Add("complete.mp3", job.FinalHash);
        return files;
    }

    // Carry existing receipts forward before a retry discards incompatible audio receipts.
    public static void RetainReceipts(Job job)
    {
        foreach (var file in Inventory(job))
            if (!job.PrivateArtifacts.Any(r => r.RelativePath.Equals(file.Key, StringComparison.OrdinalIgnoreCase)))
                job.PrivateArtifacts.Add(new(file.Key, file.Value));
    }

    public static async Task PrepareOutputAsync(Job job, string directory, string name, CancellationToken ct, bool preserveChangedChunk = false)
    {
        var path = OwnedFileRemoval.Resolve(directory, name);
        var inventory = Inventory(job);
        if (File.Exists(path) || Directory.Exists(path))
        {
            if (!inventory.TryGetValue(name, out var hash))
                throw new IOException("An untracked private output occupies a narration path. It was preserved; inspect private storage before retrying.");
            if (preserveChangedChunk)
            {
                // The worker has already rejected this cached chunk. Keep changed bytes, using
                // one exclusive Windows handle for inspection and the nonoverwriting rename.
                await using var held = ExportStagingFile.OpenIfPresent(directory, name);
                if (held is not null)
                {
                    if (await held.HashAsync(ct) == hash) held.Delete();
                    else
                    {
                        ct.ThrowIfCancellationRequested();
                        held.Rename("unverified-" + Guid.NewGuid().ToString("N") + "-" + name);
                        job.PrivateStorageNotice = "A changed cached chunk was retained in private storage under an unverified name. Narration was regenerated; inspect the retained file before deleting this item.";
                    }
                }
            }
            else await OwnedFileRemoval.DeleteByHashAsync(directory, name, hash, ct: ct);
        }
        job.PrivateArtifacts.RemoveAll(r => r.RelativePath.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task RecordAsync(Job job, string directory, string name, CancellationToken ct)
    {
        var path = OwnedFileRemoval.Resolve(directory, name);
        var hash = await Workspace.HashFileAsync(path, ct);
        job.PrivateArtifacts.RemoveAll(r => r.RelativePath.Equals(name, StringComparison.OrdinalIgnoreCase));
        job.PrivateArtifacts.Add(new(name, hash));
    }

    public static async Task RemoveAsync(Job job, string directory)
    {
        Workspace.RejectReparsePoints(directory);
        foreach (var receipt in Inventory(job))
            await OwnedFileRemoval.DeleteByHashAsync(directory, receipt.Key, receipt.Value);
        // Remove only the job directory itself, and only when it is empty.
        if (Directory.Exists(directory))
        {
            if (Directory.EnumerateFileSystemEntries(directory).Any())
                throw new IOException("Private removal is incomplete: untracked files or directories remain and were preserved. Inspect private storage, then retry deletion.");
            Workspace.RejectFileReparsePoint(directory);
            Directory.Delete(directory, false);
        }
    }
}
