using System.Text.Json;
using CommuteCast.Core;

namespace CommuteCast.Infrastructure;

public sealed class RemoteQueueWorker(string workerId, string name, RemoteQueueFolder folder, Workspace workspace,
    QueueCoordinator queue, RemoteRenderPlanner planner, TimeProvider? clock = null, Func<string, CloudSyncState>? syncStatus = null)
{
    private static readonly TimeSpan DeliveryNoticeAfter = TimeSpan.FromMinutes(10);
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly Func<string, CloudSyncState> syncStatus = syncStatus ?? (path => CloudFileSync.ReadPath(path));
    private readonly SemaphoreSlim updates = new(1);
    private readonly Dictionary<string, RemoteJobStatus> lastStatus = new(StringComparer.Ordinal);
    public string WorkerId => workerId;
    public event Action<RemoteJobStatus>? StatusChanged;

    public async Task PollAsync(CancellationToken ct = default, bool acceptRequests = true)
    {
        await updates.WaitAsync(ct);
        try
        {
            await RecoverStatusesAsync(ct);
            if (acceptRequests && !queue.Paused && !queue.PowerSuspended && queue.PersistenceError.Length == 0)
                await ImportAsync(ct);
        }
        finally { updates.Release(); }
    }

    // The host can check in independently of a slow provider readiness check or an upload.
    public Task CheckInAsync(RemoteWorkerState state, IEnumerable<string> presets) => folder.WriteWorkerAsync(new(workerId, name,
        clock.GetUtcNow(), state, presets.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));

    public async Task<bool> RetryAsync(string jobId, CancellationToken ct = default)
    {
        await updates.WaitAsync(ct);
        try
        {
            var request = await folder.ReadRequestAsync(jobId, ct);
            var status = await ReadStatusAsync(jobId, ct);
            if (request?.TargetWorkerId != workerId || status is not { State: RemoteJobState.NeedsAttention } || status.WorkerId != workerId)
                throw new ArgumentException("Choose a job assigned to this worker that needs attention.");
            var job = queue.Snapshot().FirstOrDefault(j => j.Id == jobId);
            if (job is null)
            {
                if (status.ActualVoice is not null) throw new IOException("Restore the worker's local queue before retrying the previously accepted job.");
                return await AcceptAsync(request, ct);
            }

            if (job.Remote is not { } remote || remote.WorkerId != workerId ||
                !folder.MatchesRoot(remote.QueueRoot) || remote.RequestFingerprint != RemoteQueueFolder.Fingerprint(request))
                throw new IOException("The saved job and shared request no longer match. Preserve both for inspection.");
            if (job.Stage == JobStage.Generated)
            {
                await PublishAsync(job, ct);
                return lastStatus[jobId].State is RemoteJobState.Ready or RemoteJobState.Uploading;
            }
            await queue.RetryAsync(jobId);
            await ReportAsync(ToStatus(job, RemoteJobState.Queued));
            return true;
        }
        finally { updates.Release(); }
    }

    private async Task ImportAsync(CancellationToken ct)
    {
        var existing = queue.Snapshot().ToDictionary(j => j.Id, StringComparer.Ordinal);
        var requests = new List<RemoteRequest>();
        foreach (var id in folder.RequestIds())
        {
            ct.ThrowIfCancellationRequested();
            if (existing.ContainsKey(id) || lastStatus.ContainsKey(id)) continue;
            try
            {
                var request = await folder.ReadRequestAsync(id, ct);
                if (request?.TargetWorkerId != workerId) continue;
                if (await ReadStatusAsync(id, ct) is { } prior && prior.WorkerId == workerId)
                {
                    lastStatus[id] = prior;
                    if (prior.State is RemoteJobState.Ready or RemoteJobState.NeedsAttention) continue;
                    // A lost workspace requires explicit recovery before starting the same request again.
                    await ReportAsync(prior with { State = RemoteJobState.NeedsAttention,
                        Attention = "Restore the worker's local queue before retrying this previously accepted job." });
                    continue;
                }
                requests.Add(request);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
            { AppLogging.Failure("RemoteRequestRead", error); }
        }

        foreach (var request in requests.OrderBy(r => r.CreatedUtc).ThenBy(r => r.JobId, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (queue.Paused || queue.PowerSuspended || queue.PersistenceError.Length > 0) return;
            await AcceptAsync(request, ct);
        }
    }

    private async Task<bool> AcceptAsync(RemoteRequest request, CancellationToken ct)
    {
        var current = await planner.TryPrepareAsync(request, workerId, folder.Root, ct);
        if (current.Job is not { } job)
        {
            await ReportAsync(new(request.JobId, workerId, clock.GetUtcNow(), RemoteJobState.NeedsAttention,
                RemoteVoice.From(request.Options, request.Preset), Attention: current.Attention));
            return false;
        }
        await queue.AddAsync(job, ct);
        await ReportAsync(ToStatus(job, RemoteJobState.Queued));
        return true;
    }

    private async Task RecoverStatusesAsync(CancellationToken ct)
    {
        var pending = 0;
        foreach (var job in queue.Snapshot().Where(j => j.Remote is { } remote && remote.WorkerId == workerId && folder.MatchesRoot(remote.QueueRoot)))
        {
            ct.ThrowIfCancellationRequested();
            if (job.Stage == JobStage.Generated)
            {
                await PublishAsync(job, ct);
                continue;
            }

            var status = job.Stage switch
            {
                JobStage.Queued => ToStatus(job, RemoteJobState.Queued) with { QueuePosition = ++pending },
                JobStage.Failed or JobStage.Cancelled or JobStage.Deleting => ToStatus(job, RemoteJobState.NeedsAttention) with
                { Attention = FailureGuidance(job) },
                _ => ToStatus(job, RemoteJobState.Generating)
            };
            await ReportAsync(status);
        }
    }

    private async Task PublishAsync(Job job, CancellationToken ct)
    {
        if (job.Remote?.Delivery is { } confirmed)
        {
            var path = folder.OutputPath(confirmed.OutputFile);
            if (!File.Exists(path))
                await ReportAsync(ToStatus(job, RemoteJobState.NeedsAttention) with
                {
                    OutputFile = confirmed.OutputFile, DurationSeconds = job.DurationSeconds,
                    Attention = "The previously delivered MP3 is missing from the worker's listening folder. Check OneDrive or restore the listening copy there."
                });
            else if (lastStatus.GetValueOrDefault(job.Id)?.State != RemoteJobState.Ready)
            {
                var cloud = syncStatus(path);
                if (cloud == CloudSyncState.InSync) await ReportReadyAsync(job, confirmed);
                else await ReportUploadingAsync(job, confirmed.OutputFile, path, cloud);
            }
            return;
        }
        string filename;
        string output;
        CloudSyncState sync;
        RemoteDelivery? delivery = null;
        try
        {
            filename = await folder.PublishAsync(job, workspace, ct);
            output = folder.OutputPath(filename);
            sync = syncStatus(output);
            if (sync == CloudSyncState.InSync)
                delivery = await queue.ConfirmRemoteDeliveryAsync(job.Id, new(filename, job.FinalHash, clock.GetUtcNow()), ct);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLogging.Failure("RemotePublication", error);
            await ReportAsync(ToStatus(job, RemoteJobState.NeedsAttention) with
            { Attention = "The finished MP3 is saved on the worker. Check the shared output folder and OneDrive sync so delivery can retry." });
            return;
        }
        if (delivery is not null) await ReportReadyAsync(job, delivery);
        else await ReportUploadingAsync(job, filename, output, sync);
    }

    private Task ReportUploadingAsync(Job job, string filename, string path, CloudSyncState sync)
    {
        string? notice = null;
        if (sync != CloudSyncState.Missing && clock.GetUtcNow() - File.GetLastWriteTimeUtc(path) >= DeliveryNoticeAfter)
            notice = sync == CloudSyncState.Unknown
                ? "Windows has not reported sync status for this MP3. Check that OneDrive syncs the folder and Files On-Demand is enabled."
                : "OneDrive has not confirmed delivery yet. Check the OneDrive icon for a paused sync or an upload error.";
        return ReportAsync(ToStatus(job, RemoteJobState.Uploading) with
        { OutputFile = filename, DurationSeconds = job.DurationSeconds, Attention = notice });
    }

    private Task ReportReadyAsync(Job job, RemoteDelivery delivery) => ReportAsync(ToStatus(job, RemoteJobState.Ready) with
    { OutputFile = delivery.OutputFile, DurationSeconds = job.DurationSeconds });

    private RemoteJobStatus ToStatus(Job job, RemoteJobState state)
    {
        var remote = job.Remote!;
        var estimated = job.Activity.Read().Timing.EstimatedRemainingMilliseconds;
        return new(job.Id, workerId, clock.GetUtcNow(), state, remote.RequestedVoice, remote.ActualVoice, job.CompletedChunks,
            job.Chunks.Count, EstimatedSecondsRemaining: state == RemoteJobState.Generating && estimated is { } ms ? ms / 1000 : null,
            FallbackReason: remote.FallbackReason);
    }

    private static string FailureGuidance(Job job) => job.FailureCategory switch
    {
        FailureCategory.ServiceConnection or FailureCategory.ServiceContract or FailureCategory.Prerequisite =>
            "Check the selected speech service on the worker, then retry the saved narration.",
        FailureCategory.Storage or FailureCategory.AccessDenied => "Check disk space and workspace permissions on the worker, then retry the saved narration.",
        FailureCategory.Cancelled => "This narration was cancelled on the worker. Resume it there if you still want the MP3.",
        _ => "Open the saved narration on the worker for details and retry options."
    };

    private async Task ReportAsync(RemoteJobStatus status)
    {
        var now = clock.GetUtcNow();
        if (lastStatus.TryGetValue(status.JobId, out var last) &&
            status with { UpdatedUtc = last.UpdatedUtc, EstimatedSecondsRemaining = last.EstimatedSecondsRemaining } == last &&
            now < last.UpdatedUtc + RemoteWorkerStatus.CheckInInterval)
            return;
        status = status with { UpdatedUtc = now };
        await folder.WriteStatusAsync(status);
        lastStatus[status.JobId] = status;
        StatusChanged?.Invoke(status);
    }

    private async Task<RemoteJobStatus?> ReadStatusAsync(string id, CancellationToken ct)
    {
        try { return await folder.ReadStatusAsync(id, ct); }
        catch (JsonException error) { throw new IOException("The previous job status needs repair before this request can be accepted.", error); }
    }
}
