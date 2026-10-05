using CommuteCast.Core;
using System.Text;

namespace CommuteCast.Infrastructure;

public sealed class ExportPublisher(Workspace workspace, IJobStore store)
{
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
        workspace.GuardLocalDestination(job.Destination);
        if (string.IsNullOrWhiteSpace(job.FinalHash)) throw new IOException("No validated finished audio is available for export.");
        var source = workspace.FinalPath(job);
        if (await Workspace.HashFileAsync(source, ct) != job.FinalHash) throw new IOException("The generated MP3 changed. Retry generation before exporting.");
        if (job.ExportName.Length == 0) job.ExportName = Filename(job);
        if (job.ExportName != Filename(job)) throw new IOException("Export filename does not match the recorded job identity.");
        var final = Path.Combine(job.Destination, job.ExportName);
        var temporary = Path.Combine(job.Destination, $".commutecast-{job.Id}.partial");
        job.Stage = JobStage.Exporting;
        job.ExportHash = job.FinalHash;
        // Persist intent before touching destination so a crash after rename can reconcile.
        await store.SaveAsync(job, ct);
        if (File.Exists(final))
        {
            if (await Workspace.HashFileAsync(final, ct) != job.ExportHash) throw new IOException("An unrelated or changed file occupies the export name. Nothing was overwritten.");
            job.ExportCommitted = true;
            job.Stage = JobStage.Exported;
            await store.SaveAsync(job, CancellationToken.None);
            return;
        }
        if (File.Exists(temporary) && File.GetAttributes(temporary).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Export temporary file is a symbolic link. Publication was refused.");
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
        await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
        {
            await input.CopyToAsync(output, ct);
            output.Flush(true);
        }
        if (await Workspace.HashFileAsync(temporary, ct) != job.ExportHash) throw new IOException("Export copy verification failed. The completed local MP3 is retained.");
        ct.ThrowIfCancellationRequested();
        File.Move(temporary, final, false);
        // Rename is the publication commit point. Cancellation cannot undo an exported file.
        job.ExportCommitted = true;
        job.Stage = JobStage.Exported;
        await store.SaveAsync(job, CancellationToken.None);
    }

    public async Task ReconcileAsync(Job job, CancellationToken ct)
    {
        if (job.ExportName.Length == 0 || job.ExportHash.Length == 0) return;
        if (job.ExportName != Filename(job)) throw new IOException("Export journal contains an invalid filename.");
        var final = Path.Combine(job.Destination, job.ExportName);
        if (File.Exists(final) && await Workspace.HashFileAsync(final, ct) == job.ExportHash)
        {
            job.ExportCommitted = true;
            job.Stage = JobStage.Exported;
            job.Error = "";
            await store.SaveAsync(job, ct);
        }
    }

    public async Task RemoveManagedExportAsync(Job job, CancellationToken ct)
    {
        if (job.ExportName.Length == 0) return;
        workspace.GuardLocalDestination(job.Destination);
        if (job.ExportName != Filename(job)) throw new IOException("Unrecognized export name. Removal was refused.");
        var final = Path.Combine(job.Destination, job.ExportName);
        if (File.Exists(final))
        {
            if (File.GetAttributes(final).HasFlag(FileAttributes.ReparsePoint) || job.ExportHash.Length == 0 || await Workspace.HashFileAsync(final, ct) != job.ExportHash) throw new IOException("The exported file changed or is no longer owned. It was preserved.");
            File.Delete(final);
        }
        var temporary = Path.Combine(job.Destination, $".commutecast-{job.Id}.partial");
        if (File.Exists(temporary) && !File.GetAttributes(temporary).HasFlag(FileAttributes.ReparsePoint)) File.Delete(temporary);
    }
}
