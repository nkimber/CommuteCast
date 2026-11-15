using System.Text.Json;
using CommuteCast.Core;

namespace CommuteCast.Infrastructure;

public sealed class RemoteQueueWorker(string workerId, string name, RemoteQueueFolder folder, Workspace workspace,
    QueueCoordinator queue, RemoteRenderPlanner planner, TimeProvider? clock = null, Func<string, CloudSyncState>? syncStatus = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly Func<string, CloudSyncState> syncStatus = syncStatus ?? (path => CloudFileSync.ReadPath(path));
    private readonly SemaphoreSlim updates = new(1);
    private readonly Dictionary<string, RemoteJobStatus> lastStatus = new(StringComparer.Ordinal);

    public async Task PollAsync(CancellationToken ct = default)
    {
        await updates.WaitAsync(ct);
        try
        {
            await RecoverStatusesAsync(ct);
            if (!queue.Paused && !queue.PowerSuspended && queue.PersistenceError.Length == 0)
                await ImportAsync(ct);
        }
        finally { updates.Release(); }
    }

    // The host can check in independently of a slow provider readiness check or an upload.
    public Task CheckInAsync(RemoteWorkerState state, IEnumerable<string> presets) => folder.WriteWorkerAsync(new(workerId, name,
        clock.GetUtcNow(), state, presets.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));

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
            Job job;
            try { job = await planner.PrepareAsync(request, workerId, folder.Root, ct); }
            catch (Exception error) when (error is RemoteSpeechUnavailableException or ArgumentException)
            {
                var reason = error is RemoteSpeechUnavailableException ? error.Message : "This narration could not be prepared. Review its text and voice choices on the submitting PC.";
                await ReportAsync(new(request.JobId, workerId, clock.GetUtcNow(), RemoteJobState.NeedsAttention,
                    RemoteVoice.From(request.Options, request.Preset), Attention: reason));
                continue;
            }
            await queue.AddAsync(job, ct);
            await ReportAsync(ToStatus(job, RemoteJobState.Queued));
        }
    }

    private async Task RecoverStatusesAsync(CancellationToken ct)
    {
        var pending = 0;
        foreach (var job in queue.Snapshot().Where(j => j.Remote is { } remote && remote.WorkerId == workerId &&
            remote.QueueRoot.Equals(folder.Root, StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            if (lastStatus.GetValueOrDefault(job.Id)?.State == RemoteJobState.Ready) continue;
            if (job.Stage == JobStage.Generated)
            {
                try
                {
                    var filename = await folder.PublishAsync(job, workspace, ct);
                    var state = syncStatus(folder.OutputPath(filename)) == CloudSyncState.InSync ? RemoteJobState.Ready : RemoteJobState.Uploading;
                    await ReportAsync(ToStatus(job, state) with { OutputFile = filename, DurationSeconds = job.DurationSeconds });
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    AppLogging.Failure("RemotePublication", error);
                    await ReportAsync(ToStatus(job, RemoteJobState.NeedsAttention) with
                    { Attention = "The finished MP3 is saved on the worker. Check the shared output folder and OneDrive sync so delivery can retry." });
                }
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
    }

    private async Task<RemoteJobStatus?> ReadStatusAsync(string id, CancellationToken ct)
    {
        try { return await folder.ReadStatusAsync(id, ct); }
        catch (JsonException error) { throw new IOException("The previous job status needs repair before this request can be accepted.", error); }
    }
}
