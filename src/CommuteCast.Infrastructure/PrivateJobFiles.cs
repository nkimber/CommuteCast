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
        var artifactNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            if (!artifactNames.Add(receipt.RelativePath)) throw new IOException("Private artifact receipts are duplicated. Files were preserved.");
            if (receipt.PromotionIdentity is { } identity && (identity.FormatVersion != 1 || identity.FileId?.Length != 32 ||
                !identity.FileId.All(Uri.IsHexDigit) || identity.CreationFileTime <= 0)) throw new IOException("A private promotion identity is invalid. Files were preserved.");
            if (receipt.CreationIdentity is { } creation)
            {
                if (receipt.PromotionIdentity is not null || receipt.Hash != "" || creation.FormatVersion != 1 || creation.FileId?.Length != 32 ||
                    !creation.FileId.All(Uri.IsHexDigit) || creation.CreationFileTime <= 0 || receipt.RelativePath is null ||
                    receipt.RelativePath.Contains('/') || receipt.RelativePath.Length > 200)
                    throw new IOException("An incomplete private creation receipt is invalid. Files were preserved.");
                OwnedFileRemoval.ValidateRelativePath(receipt.RelativePath);
                continue; // Incomplete bytes never enter the completed-content inventory.
            }
            Add(receipt.RelativePath, receipt.Hash);
        }
        foreach (var receipt in job.Receipts)
        {
            if (receipt.Index is < 0 or > 99999) throw new IOException("A private chunk receipt is invalid. Files were preserved.");
            Add($"chunk-{receipt.Index:D5}.wav", receipt.Hash);
        }
        if (!string.IsNullOrEmpty(job.FinalHash)) Add("complete.mp3", job.FinalHash);
        if (job.PrivateArtifacts.Any(r => r.CreationIdentity is not null && files.ContainsKey(r.RelativePath)))
            throw new IOException("Incomplete and completed private receipts disagree. Files were preserved.");
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
            var creation = job.PrivateArtifacts.FirstOrDefault(r => r.RelativePath.Equals(name, StringComparison.OrdinalIgnoreCase))?.CreationIdentity;
            if (creation is not null)
            {
                await using var partial = ExportStagingFile.OpenIfPresent(directory, name);
                if (partial is not null)
                {
                    if (!partial.Matches(creation)) throw new IOException("An interrupted private creation was replaced. Files were preserved; inspect private storage before retrying.");
                    ct.ThrowIfCancellationRequested(); partial.Delete();
                }
            }
            else
            {
                if (!inventory.TryGetValue(name, out var hash))
                    throw new IOException("An untracked private output occupies a narration path. It was preserved; inspect private storage before retrying.");
                var identity = job.PrivateArtifacts.FirstOrDefault(r => r.RelativePath.Equals(name, StringComparison.OrdinalIgnoreCase))?.PromotionIdentity;
                if (identity is not null)
                {
                    await using var pending = ExportStagingFile.OpenIfPresent(directory, name);
                    if (pending is not null)
                    {
                        if (!pending.Matches(identity) || await pending.HashAsync(ct) != hash) throw new IOException("An interrupted private promotion was replaced or changed. Files were preserved.");
                        pending.Delete();
                    }
                }
                else if (preserveChangedChunk)
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

    // Keep creation, writing and the completed receipt under the same exclusive
    // handle. Persist creation identity before the writer runs, so a process loss
    // during writing can retire only this original incomplete file on recovery.
    public static async Task WriteRecordedAsync(Job job, string directory, string name,
        Func<Stream, Task> write, Func<Task> checkpoint, CancellationToken ct)
    {
        await PrepareOutputAsync(job, directory, name, ct);
        ct.ThrowIfCancellationRequested();
        await using var held = ExportStagingFile.Create(directory, name);
        try
        {
            job.PrivateArtifacts.Add(new(name, "", CreationIdentity: held.Identity));
            await checkpoint();
            ct.ThrowIfCancellationRequested();
            await write(held.Stream);
            await held.Stream.FlushAsync(ct);
            held.Stream.Flush(true);
            var hash = await held.HashAsync(ct);
            job.PrivateArtifacts.RemoveAll(r => r.RelativePath.Equals(name, StringComparison.OrdinalIgnoreCase));
            job.PrivateArtifacts.Add(new(name, hash));
            await checkpoint();
        }
        catch
        {
            // Do not use the canceled token or reopen a path that may have changed.
            held.Delete();
            job.PrivateArtifacts.RemoveAll(r => r.RelativePath.Equals(name, StringComparison.OrdinalIgnoreCase));
            throw;
        }
    }

    // Finish a provider download without reopening its held file. Both sides carry
    // identity until the nonoverwriting rename and final receipt have been saved.
    public static async Task CompleteAndMoveCreatedAsync(Job job, string directory, string source, string destination,
        ExportStagingFile held, Func<Task> checkpoint, CancellationToken ct)
    {
        Inventory(job);
        if (!held.IsAt(directory, source) || source.Equals(destination, StringComparison.OrdinalIgnoreCase) ||
            job.PrivateArtifacts.SingleOrDefault(r => r.RelativePath.Equals(source, StringComparison.OrdinalIgnoreCase))?.CreationIdentity is not { } identity || !held.Matches(identity))
            throw new IOException("Private download completion requires its original creation identity. Files were preserved.");
        await PrepareOutputAsync(job, directory, destination, ct);
        await held.Stream.FlushAsync(ct); held.Stream.Flush(true);
        var hash = await held.HashAsync(ct);
        job.PrivateArtifacts.RemoveAll(r => r.RelativePath.Equals(source, StringComparison.OrdinalIgnoreCase));
        job.PrivateArtifacts.Add(new(source, hash, held.Identity));
        job.PrivateArtifacts.Add(new(destination, hash, held.Identity));
        await checkpoint(); ct.ThrowIfCancellationRequested();
        held.Rename(destination);
        job.PrivateArtifacts.RemoveAll(r => r.RelativePath.Equals(source, StringComparison.OrdinalIgnoreCase) || r.RelativePath.Equals(destination, StringComparison.OrdinalIgnoreCase));
        job.PrivateArtifacts.Add(new(destination, hash));
        await checkpoint();
    }

    // Save the destination's expected bytes before moving them. Recovery can
    // recognize either side of a crash without adopting an existing filename.
    public static async Task MoveRecordedAsync(Job job, string directory, string source, string destination,
        Func<Task> checkpoint, CancellationToken ct, bool preserveChangedChunk = false)
    {
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase)) throw new IOException("Private promotion requires distinct paths.");
        var inventory = Inventory(job);
        if (!inventory.TryGetValue(source, out var hash)) throw new IOException("Private promotion requires a completed source receipt. Files were preserved.");
        await using var held = ExportStagingFile.OpenIfPresent(directory, source) ?? throw new IOException("The recorded private source is missing.");
        var sourceIdentity = job.PrivateArtifacts.FirstOrDefault(r => r.RelativePath.Equals(source, StringComparison.OrdinalIgnoreCase))?.PromotionIdentity;
        if (sourceIdentity is not null && !held.Matches(sourceIdentity)) throw new IOException("The interrupted private source was replaced. Files were preserved.");
        if (await held.HashAsync(ct) != hash) throw new IOException("The recorded private source changed. Files were preserved.");
        await PrepareOutputAsync(job, directory, destination, ct, preserveChangedChunk);
        job.PrivateArtifacts.Add(new(destination, hash, held.Identity));
        await checkpoint();
        ct.ThrowIfCancellationRequested();
        held.Rename(destination);
        job.PrivateArtifacts.RemoveAll(r => r.RelativePath.Equals(source, StringComparison.OrdinalIgnoreCase));
        job.PrivateArtifacts.RemoveAll(r => r.RelativePath.Equals(destination, StringComparison.OrdinalIgnoreCase));
        job.PrivateArtifacts.Add(new(destination, hash));
        await checkpoint();
    }

    public static async Task ReconcilePromotionsAsync(Job job, string directory, Func<Task> checkpoint, CancellationToken ct)
    {
        Inventory(job);
        foreach (var receipt in job.PrivateArtifacts.Where(r => r.CreationIdentity is not null).ToArray())
        {
            await using (var partial = ExportStagingFile.OpenIfPresent(directory, receipt.RelativePath))
            {
                if (partial is not null)
                {
                    if (!partial.Matches(receipt.CreationIdentity!)) throw new IOException("An interrupted private creation was replaced. Files were preserved; inspect private storage before retrying.");
                    ct.ThrowIfCancellationRequested(); partial.Delete();
                }
            } // Close the exact deleted handle before committing the retired intent.
            job.PrivateArtifacts.Remove(receipt);
            await checkpoint();
        }
        foreach (var receipt in job.PrivateArtifacts.Where(r => r.PromotionIdentity is not null).ToArray())
        {
            await using var held = ExportStagingFile.OpenIfPresent(directory, receipt.RelativePath);
            if (held is not null && (!held.Matches(receipt.PromotionIdentity!) || await held.HashAsync(ct) != receipt.Hash))
                throw new IOException("An interrupted private promotion was replaced or changed. Files were preserved; inspect private storage before retrying.");
            job.PrivateArtifacts.Remove(receipt);
            if (held is not null) job.PrivateArtifacts.Add(new(receipt.RelativePath, receipt.Hash));
            else
            {
                job.Receipts.RemoveAll(r => $"chunk-{r.Index:D5}.wav".Equals(receipt.RelativePath, StringComparison.OrdinalIgnoreCase));
                job.CompletedChunks = job.Receipts.Count;
            }
            await checkpoint(); // commit confirmed ownership while the same file remains exclusively held
        }
    }

    public static async Task RemoveAsync(Job job, string directory)
    {
        Workspace.RejectReparsePoints(directory);
        Inventory(job);
        foreach (var creation in job.PrivateArtifacts.Where(r => r.CreationIdentity is not null))
        {
            await using var partial = ExportStagingFile.OpenIfPresent(directory, creation.RelativePath);
            if (partial is null) continue;
            if (!partial.Matches(creation.CreationIdentity!)) throw new IOException("An interrupted private creation was replaced. Files were preserved.");
            partial.Delete();
        }
        foreach (var receipt in Inventory(job))
        {
            var identity = job.PrivateArtifacts.FirstOrDefault(r => r.RelativePath.Equals(receipt.Key, StringComparison.OrdinalIgnoreCase))?.PromotionIdentity;
            if (identity is null) await OwnedFileRemoval.DeleteByHashAsync(directory, receipt.Key, receipt.Value);
            else
            {
                await using var held = ExportStagingFile.OpenIfPresent(directory, receipt.Key);
                if (held is null) continue;
                if (!held.Matches(identity) || await held.HashAsync(default) != receipt.Value) throw new IOException("An interrupted private promotion was replaced or changed. Files were preserved.");
                held.Delete();
            }
        }
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
