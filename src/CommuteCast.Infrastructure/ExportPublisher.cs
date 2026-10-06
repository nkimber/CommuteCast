using CommuteCast.Core;
using System.Text;
using System.Security.Cryptography;

namespace CommuteCast.Infrastructure;

public enum ExportCheckpoint { IntentSaved, CopyStarted, CopyProgress, CopyFlushed, CopyVerified, BeforeRename, Renamed, Committed, RemovalValidated, StagingCreated, StagingResumed, StagingRemovalValidated }
public interface IExportObserver
{
    Task ReachedAsync(ExportCheckpoint checkpoint, CancellationToken ct);
}
public enum ExportStagingCleanup { Absent, Removed, PreservedUnknown }

public sealed class ExportPublisher(Workspace workspace, IJobStore store, IExportObserver? observer = null)
{
    private Task ReachedAsync(ExportCheckpoint checkpoint, CancellationToken ct) => observer?.ReachedAsync(checkpoint, ct) ?? Task.CompletedTask;
    private static void ThrowIfStopped(Job job, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (job.CancellationRequested || job.DeletionRequested) throw new OperationCanceledException("Publication was cancelled.", ct);
    }
    private static void RejectFileLink(string path)
    {
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("An export file is a symbolic link. The file was preserved.");
    }
    public static string Filename(Job job)
    {
        var title = new string(job.Title.Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? '-' : c).ToArray()).Trim(' ', '.');
        if (title.Length == 0) title = "CommuteCast";
        var length = Math.Min(title.Length, 70);
        if (length < title.Length && char.IsHighSurrogate(title[length - 1]) && char.IsLowSurrogate(title[length])) length--;
        title = title[..length].TrimEnd(' ', '.');
        return $"{job.CreatedUtc:yyyyMMdd-HHmmss}-{title}-{job.Id}.mp3";
    }
    public async Task TestDestinationAsync(string destination, CancellationToken ct = default)
    {
        workspace.GuardLocalDestination(destination);
        var probe = Path.Combine(destination, $".commutecast-write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            await using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("CommuteCast write test"), ct);
            stream.Flush(true);
        }
        finally { if (File.Exists(probe)) File.Delete(probe); }
    }

    public async Task PublishAsync(Job job, CancellationToken ct)
    {
        ThrowIfStopped(job, ct);
        workspace.GuardLocalDestination(job.Destination);
        if (string.IsNullOrWhiteSpace(job.FinalHash)) throw new IOException("No validated finished audio is available for export.");
        var source = workspace.FinalPath(job);
        Workspace.RejectReparsePoints(Path.GetDirectoryName(source)!);
        RejectFileLink(source);
        // Keep the exact validated source locked through copying so it cannot change after hashing.
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (Convert.ToHexString(await SHA256.HashDataAsync(input, ct)) != job.FinalHash) throw new IOException("The generated MP3 changed. Retry generation before exporting.");
        input.Position = 0;
        if (job.ExportName.Length == 0) job.ExportName = Filename(job);
        if (job.ExportName != Filename(job)) throw new IOException("Export filename does not match the recorded job identity.");
        var final = Path.Combine(job.Destination, job.ExportName);
        var temporary = Path.Combine(job.Destination, $".commutecast-{job.Id}.partial");
        if ((job.ExportStagingOwned || job.ExportStagingIdentity is not null) && job.ExportHash != job.FinalHash) throw new IOException("The staging journal no longer matches the validated audio. Inspection is required before retrying.");
        job.Stage = JobStage.Exporting;
        job.ExportHash = job.FinalHash;
        // Persist intent before touching destination so a crash after rename can reconcile.
        await store.SaveAsync(job, ct);
        await ReachedAsync(ExportCheckpoint.IntentSaved, ct);
        ThrowIfStopped(job, ct);
        workspace.GuardLocalDestination(job.Destination);
        RejectFileLink(final);
        if (File.Exists(final))
        {
            if (await Workspace.HashFileAsync(final, ct) != job.ExportHash) throw new IOException("An unrelated or changed file occupies the export name. Nothing was overwritten.");
            job.ExportCommitted = true;
            job.CancellationRequested = false;
            job.Stage = JobStage.Exported;
            await RemoveStagingAsync(job, temporary, ct, true);
            await store.SaveAsync(job, CancellationToken.None);
            return;
        }
        var created = false;
        ExportStagingFile? staging = null;
        try
        {
            staging = ExportStagingFile.OpenIfPresent(job.Destination, Path.GetFileName(temporary));
            if (staging is not null)
            {
                if (!await ValidateStagingAsync(job, staging, input, ct)) throw new IOException("An unrecognized or changed export staging file exists. It was preserved. Inspect it before retrying, or choose another output folder.");
                await ReachedAsync(ExportCheckpoint.StagingResumed, ct);
            }
            else
            {
                staging = ExportStagingFile.Create(job.Destination, Path.GetFileName(temporary));
                created = true;
                job.ExportStagingIdentity = staging.Identity;
                // Older releases see false and preserve an incomplete copy whose newer identity they cannot inspect.
                job.ExportStagingOwned = false;
                await ReachedAsync(ExportCheckpoint.StagingCreated, ct);
                // A saved intent alone cannot authorize removal of a pre-existing collision or replacement.
                await store.SaveAsync(job, ct);
                await ReachedAsync(ExportCheckpoint.CopyStarted, ct);
            }
            input.Position = staging.Stream.Position = staging.Stream.Length;
            var buffer = new byte[81920]; int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                ThrowIfStopped(job, ct);
                await staging.Stream.WriteAsync(buffer.AsMemory(0, read), ct);
                await ReachedAsync(ExportCheckpoint.CopyProgress, ct);
            }
            staging.Stream.Flush(true);
            await ReachedAsync(ExportCheckpoint.CopyFlushed, ct);
            if (await staging.HashAsync(ct) != job.ExportHash) throw new IOException("Export copy verification failed. The completed local MP3 is retained.");
            await ReachedAsync(ExportCheckpoint.CopyVerified, ct);
            await ReachedAsync(ExportCheckpoint.BeforeRename, ct);
            ThrowIfStopped(job, ct);
            workspace.GuardLocalDestination(job.Destination);
            if (await staging.HashAsync(ct) != job.ExportHash) throw new IOException("Export staging changed before publication. The completed local MP3 is retained.");
            ThrowIfStopped(job, ct);
            staging.Rename(job.ExportName);
            // Rename is the publication commit point. Cancellation cannot undo an exported file.
            ClearStaging(job);
            job.CancellationRequested = false;
            job.ExportCommitted = true;
            job.Stage = JobStage.Exported;
            await staging.DisposeAsync(); staging = null;
            await ReachedAsync(ExportCheckpoint.Renamed, CancellationToken.None);
            await store.SaveAsync(job, CancellationToken.None);
            await ReachedAsync(ExportCheckpoint.Committed, CancellationToken.None);
        }
        finally
        {
            // The original exclusive handle identifies this invocation's new file even if its journal save failed.
            // Resumed files and pre-existing collisions remain intact on failure.
            if (created && !job.ExportCommitted && staging is not null)
            {
                try
                {
                    staging.Delete(); ClearStaging(job);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Preserve the original failure and the journal if cleanup is unavailable.
                }
            }
            if (staging is not null) await staging.DisposeAsync();
        }
    }

    public async Task ReconcileAsync(Job job, CancellationToken ct)
    {
        if (job.ExportName.Length == 0 || job.ExportHash.Length == 0) return;
        if (job.ExportName != Filename(job)) throw new IOException("Export journal contains an invalid filename.");
        var final = Path.Combine(job.Destination, job.ExportName);
        if (!Directory.Exists(job.Destination)) return; // Preserve history for a moved/missing destination.
        workspace.GuardLocalDestination(job.Destination);
        RejectFileLink(final);
        if (File.Exists(final) && await Workspace.HashFileAsync(final, ct) == job.ExportHash)
        {
            job.ExportCommitted = true;
            job.Stage = JobStage.Exported;
            job.Error = "";
            job.CancellationRequested = false;
            await RemoveStagingAsync(job, Path.Combine(job.Destination, $".commutecast-{job.Id}.partial"), ct, true);
            await store.SaveAsync(job, ct);
        }
    }

    public async Task RemoveManagedExportAsync(Job job, CancellationToken ct)
    {
        if (job.ExportName.Length == 0) return;
        workspace.GuardLocalDestination(job.Destination);
        if (job.ExportName != Filename(job)) throw new IOException("Unrecognized export name. Removal was refused.");
        var final = Path.Combine(job.Destination, job.ExportName);
        if (job.ExportHash.Length == 0)
        { if (File.Exists(final)) throw new IOException("The exported file has no recorded identity. It was preserved."); }
        else
            await OwnedFileRemoval.DeleteByHashAsync(job.Destination, job.ExportName, job.ExportHash,
                observer is null ? null : new RemovalObserver(this), ct);
        var temporary = Path.Combine(job.Destination, $".commutecast-{job.Id}.partial");
        await RemoveStagingAsync(job, temporary, ct);
    }

    public async Task<ExportStagingCleanup> RemoveOwnedStagingAsync(Job job, CancellationToken ct, bool preserveUnknown = false)
    {
        if (!Directory.Exists(job.Destination) || job.ExportName.Length == 0 && !job.ExportStagingOwned && job.ExportStagingIdentity is null) return ExportStagingCleanup.Absent;
        workspace.GuardLocalDestination(job.Destination);
        if (job.ExportName != Filename(job) || job.ExportHash.Length == 0) throw new IOException("The export staging journal is invalid. Inspection is required.");
        try { return await RemoveStagingAsync(job, Path.Combine(job.Destination, $".commutecast-{job.Id}.partial"), ct, preserveUnknown); }
        catch (Exception error) when (preserveUnknown && error is IOException or UnauthorizedAccessException)
        {
            job.ExportNotice = "Staging in the previous output folder could not be verified or removed. It was retained for manual inspection.";
            return ExportStagingCleanup.PreservedUnknown;
        }
    }

    private async Task<ExportStagingCleanup> RemoveStagingAsync(Job job, string temporary, CancellationToken ct, bool preserveUnknown = false)
    {
        ct.ThrowIfCancellationRequested();
        await using var staging = ExportStagingFile.OpenIfPresent(job.Destination, Path.GetFileName(temporary));
        if (staging is not null)
        {
            if (!await ValidateStagingAsync(job, staging, null, ct))
            {
                if (preserveUnknown)
                {
                    job.ExportNotice = "An unrecognized or altered staging file was preserved in its output folder for manual inspection.";
                    if (job.ExportCommitted) job.Error = "Local export is verified. " + job.ExportNotice;
                    return ExportStagingCleanup.PreservedUnknown;
                }
                throw new IOException("The export staging file is unrecognized. It was preserved for inspection.");
            }
            await ReachedAsync(ExportCheckpoint.StagingRemovalValidated, ct);
            ct.ThrowIfCancellationRequested(); staging.Delete(); ClearStaging(job);
            return ExportStagingCleanup.Removed;
        }
        ClearStaging(job); return ExportStagingCleanup.Absent;
    }

    private static void ClearStaging(Job job) { job.ExportStagingOwned = false; job.ExportStagingIdentity = null; }

    private async Task<bool> ValidateStagingAsync(Job job, ExportStagingFile staging, Stream? validatedSource, CancellationToken ct)
    {
        if (job.ExportHash.Length == 0 || job.ExportStagingIdentity is not null && !staging.Matches(job.ExportStagingIdentity)) return false;
        if (await staging.HashAsync(ct) == job.ExportHash) return true;
        // A legacy boolean is not proof of ownership of the current filesystem item.
        if (job.ExportStagingIdentity is null) return false;
        if (validatedSource is not null) return await staging.IsSourcePrefixAsync(validatedSource, ct);
        var source = workspace.FinalPath(job);
        Workspace.RejectReparsePoints(Path.GetDirectoryName(source)!); RejectFileLink(source);
        if (!File.Exists(source)) return false;
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (Convert.ToHexString(await SHA256.HashDataAsync(input, ct)) != job.ExportHash) return false;
        return await staging.IsSourcePrefixAsync(input, ct);
    }

    private sealed class RemovalObserver(ExportPublisher publisher) : IFileRemovalObserver
    {
        public Task ValidatedAsync(string path, CancellationToken ct) => publisher.ReachedAsync(ExportCheckpoint.RemovalValidated, ct);
    }
}
