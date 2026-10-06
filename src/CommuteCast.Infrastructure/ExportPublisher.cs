using CommuteCast.Core;
using System.Text;
using System.Security.Cryptography;

namespace CommuteCast.Infrastructure;

public enum ExportCheckpoint { IntentSaved, CopyStarted, CopyProgress, CopyFlushed, CopyVerified, BeforeRename, Renamed, Committed, RemovalValidated }
public interface IExportObserver
{
    Task ReachedAsync(ExportCheckpoint checkpoint, CancellationToken ct);
}

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
        title = title[..Math.Min(title.Length, 70)].TrimEnd(' ', '.');
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
        if (job.ExportStagingOwned && job.ExportHash != job.FinalHash) throw new IOException("The staging journal no longer matches the validated audio. Inspection is required before retrying.");
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
        RejectFileLink(temporary);
        var created = false;
        var copyClosed = false;
        try
        {
            if (File.Exists(temporary))
            {
                if (await Workspace.HashFileAsync(temporary, ct) != job.ExportHash)
                {
                    if (!job.ExportStagingOwned) throw new IOException("An unrecognized export staging file exists. It was preserved. Inspect it before retrying, or choose another output folder.");
                    File.Delete(temporary); // Durable ownership was saved after exclusive creation.
                }
            }
            if (!File.Exists(temporary))
            {
                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.WriteThrough);
                created = true;
                job.ExportStagingOwned = true;
                // A saved intent alone cannot authorize removal of a pre-existing collision.
                await store.SaveAsync(job, ct);
                await ReachedAsync(ExportCheckpoint.CopyStarted, ct);
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    await ReachedAsync(ExportCheckpoint.CopyProgress, ct);
                }
                output.Flush(true);
                await ReachedAsync(ExportCheckpoint.CopyFlushed, ct);
                output.Position = 0;
                if (Convert.ToHexString(await SHA256.HashDataAsync(output, ct)) != job.ExportHash) throw new IOException("Export copy verification failed. The completed local MP3 is retained.");
            }
            copyClosed = true;
            await ReachedAsync(ExportCheckpoint.CopyVerified, ct);
            await ReachedAsync(ExportCheckpoint.BeforeRename, ct);
            ThrowIfStopped(job, ct);
            workspace.GuardLocalDestination(job.Destination);
            RejectFileLink(temporary);
            if (await Workspace.HashFileAsync(temporary, ct) != job.ExportHash) throw new IOException("Export staging changed before publication. The completed local MP3 is retained.");
            ThrowIfStopped(job, ct);
            File.Move(temporary, final, false);
            // Rename is the publication commit point. Cancellation cannot undo an exported file.
            job.ExportStagingOwned = false;
            job.CancellationRequested = false;
            job.ExportCommitted = true;
            job.Stage = JobStage.Exported;
            await ReachedAsync(ExportCheckpoint.Renamed, CancellationToken.None);
            await store.SaveAsync(job, CancellationToken.None);
            await ReachedAsync(ExportCheckpoint.Committed, CancellationToken.None);
        }
        finally
        {
            // Graceful cancellation/failure cleans only the file this invocation created.
            // Existing staging collisions are never truncated or deleted here.
            if (created && !job.ExportCommitted)
            {
                try
                {
                    workspace.GuardLocalDestination(job.Destination);
                    if (File.Exists(temporary) && !File.GetAttributes(temporary).HasFlag(FileAttributes.ReparsePoint) &&
                        (!copyClosed || await Workspace.HashFileAsync(temporary) == job.ExportHash)) { File.Delete(temporary); job.ExportStagingOwned = false; }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Preserve the original failure and the journal if cleanup is unavailable.
                }
            }
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

    public async Task RemoveOwnedStagingAsync(Job job, CancellationToken ct)
    {
        if (!job.ExportStagingOwned || !Directory.Exists(job.Destination)) return;
        workspace.GuardLocalDestination(job.Destination);
        if (job.ExportName != Filename(job) || job.ExportHash.Length == 0) throw new IOException("The export staging journal is invalid. Inspection is required.");
        await RemoveStagingAsync(job, Path.Combine(job.Destination, $".commutecast-{job.Id}.partial"), ct);
    }

    private static async Task RemoveStagingAsync(Job job, string temporary, CancellationToken ct, bool preserveUnknown = false)
    {
        if (File.Exists(temporary))
        {
            RejectFileLink(temporary);
            if (job.ExportHash.Length == 0 || (!job.ExportStagingOwned && await Workspace.HashFileAsync(temporary, ct) != job.ExportHash))
            {
                if (preserveUnknown) { job.Error = "Local export is verified. An unrecognized staging file was preserved for inspection."; return; }
                throw new IOException("The export staging file is unrecognized. It was preserved for inspection.");
            }
            File.Delete(temporary);
            job.ExportStagingOwned = false;
        }
        else job.ExportStagingOwned = false;
    }

    private sealed class RemovalObserver(ExportPublisher publisher) : IFileRemovalObserver
    {
        public Task ValidatedAsync(string path, CancellationToken ct) => publisher.ReachedAsync(ExportCheckpoint.RemovalValidated, ct);
    }
}
