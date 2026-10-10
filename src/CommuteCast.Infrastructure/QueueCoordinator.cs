using CommuteCast.Core;
using System.Text.Json;
using Serilog;
using Serilog.Context;

namespace CommuteCast.Infrastructure;

public record DeletionItemResult(string Id, string Title, bool Removed, string Error);
public record DeletionReview(int Items, int PendingRequests, int RecordedExportRemovals);
public record DeletionResult(IReadOnlyList<DeletionItemResult> Items)
{
    public int Removed => Items.Count(i => i.Removed);
    public int Failed => Items.Count - Removed;
}

public sealed partial class QueueCoordinator(Workspace workspace, IJobStore store, ISpeechProvider provider, AudioPipeline audio, ExportPublisher publisher) : IAsyncDisposable
{
    private readonly List<Job> jobs = [];
    private readonly object sync = new();
    private readonly SemaphoreSlim dispatchGate = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? activeCancellation;
    private string? activeId;
    private TaskCompletionSource? activeFinished;
    private Task? worker;
    private string? deletionCheckpointError;
    public SemaphoreSlim InferenceGate { get; } = new(1);
    public bool Paused { get; set; }
    public int CacheQuotaMiB { get; set; } = 1024;
    public int ScratchRetentionDays { get; set; } = 7;
    public int PrivateStorageLimitMiB { get; set; } // zero disables admission checks for controlled fixtures.
    public string MaintenanceError { get; private set; } = "";
    public string PersistenceError { get; private set; } = "";
    public event Action<IReadOnlyList<Job>>? Changed;
    public IReadOnlyList<Job> Snapshot()
    {
        lock (sync) return jobs.Select(j => JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(j))!).ToArray();
    }
    private void Notify() => Changed?.Invoke(Snapshot());
    public Task<StorageUsage> MeasureStorageAsync(CancellationToken ct = default) => Task.Run(async () =>
    {
        string? active;
        lock (sync) active = activeId;
        return await new CacheMaintenance(workspace).MeasureAsync(Snapshot(), active, ct);
    }, ct);
    public async Task<CleanupResult> CleanCacheAsync(CancellationToken ct = default)
    {
        await dispatchGate.WaitAsync(ct);
        try
        {
            string? active;
            lock (sync) active = activeId;
            return await Task.Run(() => new CacheMaintenance(workspace).CleanAsync(Snapshot(), active, CacheQuotaMiB, ScratchRetentionDays, ct), ct);
        }
        finally { dispatchGate.Release(); }
    }
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var loaded = await store.LoadAsync(ct);
        Log.Information("Queue recovery loaded {JobCount} jobs", loaded.Count);
        var retained = new List<Job>();
        foreach (var job in loaded)
        {
            if (job.SpeechAttempts is { } attempts)
                for (var i = 0; i < attempts.Count; i++) if (attempts[i].State == "started") attempts[i] = attempts[i] with { State = "uncertain" };
            try { await PrivateJobFiles.ReconcilePromotionsAsync(job, workspace.JobDirectory(job.Id), () => store.SaveAsync(job, ct), ct); }
            catch (IOException error)
            {
                job.Stage = job.DeletionRequested ? JobStage.Deleting : JobStage.Failed;
                job.Error = FriendlyError(error); retained.Add(job); await store.SaveAsync(job, ct); continue;
            }
            if (job.Stage is not (JobStage.Exported or JobStage.Failed or JobStage.Cancelled or JobStage.Deleting) &&
                HasUnresolvedHostedRequest(job))
            {
                job.Stage = JobStage.Failed; job.FailedStage = JobStage.Synthesizing; job.FailureCategory = FailureCategory.ServiceConnection;
                job.Error = "An interrupted hosted request may have been billed. Inspect provider usage, then explicitly Retry / resume to authorize another request. Validated segments will be reused.";
                await store.SaveAsync(job, ct);
            }
            if (job.DeletionRequested)
            {
                try { await FinishDeletionAsync(job); }
                catch (Exception error) { job.Stage = JobStage.Deleting; job.Error = FriendlyError(error); retained.Add(job); await store.SaveAsync(job, ct); }
                continue;
            }
            try { await publisher.ReconcileAsync(job, ct); }
            catch (IOException error) { AppLogging.Failure("ExportReconciliation", error); job.Error = "Export reconciliation failed. The local job is preserved; inspect the destination and retry."; job.Stage = JobStage.Failed; }
            if (job.CancellationRequested && !job.ExportCommitted)
            {
                job.Stage = JobStage.Cancelled; job.Error = ""; job.FailureCategory = FailureCategory.Cancelled; job.FailedStage = null;
                await store.SaveAsync(job, ct);
            }
            if (job.Stage is not (JobStage.Exported or JobStage.Cancelled or JobStage.Failed))
            {
                job.Stage = JobStage.Queued;
                await store.SaveAsync(job, ct);
            }
            retained.Add(job);
        }
        lock (sync) jobs.AddRange(retained.OrderBy(j => j.QueuePosition == 0 ? j.CreatedUtc.UtcTicks : j.QueuePosition).ThenBy(j => j.CreatedUtc));
        Notify();
        Log.Information("Queue recovery completed with {JobCount} retained jobs; paused {Paused}", retained.Count, Paused);
        worker = Task.Run(WorkLoopAsync);
    }
    public async Task AddAsync(Job job, CancellationToken ct = default)
    {
        ValidateConfiguration(job);
        await dispatchGate.WaitAsync(ct);
        try
        {
            await CheckStorageBudgetAsync(Snapshot(), job.Prepared.Script, ct);
            lock (sync) job.QueuePosition = Math.Max(DateTimeOffset.UtcNow.UtcTicks, jobs.Select(j => j.QueuePosition).DefaultIfEmpty().Max() + 1);
            await store.SaveAsync(job, ct);
            lock (sync) jobs.Add(job);
            Log.Information("Job {JobId} queued using {Engine}", job.Id, job.Settings.Engine);
        }
        finally { dispatchGate.Release(); }
        Notify();
    }
    private static void ValidateConfiguration(Job job)
    {
        job.Settings.ValidateProviderImage();
        job.Settings.Speech?.Validate(job.Settings.Engine);
        if (SpeechProviders.IsHosted(job.Settings.Engine)) HostedSpeechProvider.ValidateSettings(job.Settings);
        if (job.Episode is not null) PodcastScript.ValidateManifest(job);
        job.Settings.LocalVoice?.Validate(job.Settings.Engine, job.Settings.Voice);
        if (job.Settings.Profile is not { } profile) return;
        profile.Validate(job.Settings.Engine);
        if (job.Prepared.Version != (job.Episode is null ? "prepare-v3" : "podcast-prepare-v1") || job.Prepared.ProfileReview is not { } review || review.Profile != profile ||
            review.DictionaryRevision != TextPreparation.DictionaryRevision(job.Settings.Pronunciation))
            throw new ArgumentException("The captured pronunciation profile and prepared script differ. Submit a new narration after reviewing preparation.");
    }
    private static bool HasUnresolvedHostedRequest(Job job) => job.SpeechAttempts?.Any(a =>
        a.StartedUtc > (job.HostedRetryAuthorizedUtc ?? DateTimeOffset.MinValue) && a.State is "started" or "uncertain" or "received" &&
        (a.UnitIndex is null || !job.Receipts.Any(r => r.Index == a.UnitIndex && r.Fingerprint == job.Fingerprint))) == true;
    private async Task CheckStorageBudgetAsync(IReadOnlyList<Job> snapshots, string? addedScript, CancellationToken ct)
    {
        if (PrivateStorageLimitMiB == 0) return;
        string? active; lock (sync) active = activeId;
        var usage = await Task.Run(() => new CacheMaintenance(workspace).MeasureAsync(snapshots, active, ct), ct);
        var drive = new DriveInfo(Path.GetPathRoot(workspace.Root)!);
        StorageBudget.EnsureFits(usage, addedScript, PrivateStorageLimitMiB, drive.AvailableFreeSpace);
    }
    private async Task PersistOutcomeAsync(Job job)
    {
        try { job.RunTiming = job.Activity.Read().Timing; await store.SaveAsync(job, CancellationToken.None); }
        catch (Exception error)
        {
            AppLogging.Failure("QueueCheckpoint", error);
            Paused = true;
            PersistenceError = "Queue paused because its checkpoint could not be saved. Repair disk space or permissions, then retry the narration and resume. Existing durable records are retained. " + FriendlyError(error);
        }
    }
    public async Task MovePendingAsync(string id, int direction, CancellationToken ct = default)
    {
        if (direction is not (-1 or 1)) throw new ArgumentException("Choose move earlier or later.");
        await dispatchGate.WaitAsync(ct);
        try
        {
            List<Job> pending;
            lock (sync) pending = jobs.Where(j => j.Stage == JobStage.Queued && !j.DeletionRequested && j.Id != activeId).ToList();
            var index = pending.FindIndex(j => j.Id == id);
            if (index < 0) throw new ArgumentException("Only pending narrations can be reordered.");
            var target = index + direction;
            if (target < 0 || target >= pending.Count) return;
            (pending[index], pending[target]) = (pending[target], pending[index]);
            var position = DateTimeOffset.UtcNow.UtcTicks;
            var positions = pending.Select((j, i) => (j.Id, Position: position + i)).ToDictionary(x => x.Id, x => x.Position);
            await store.SaveQueueOrderAsync(positions, ct);
            lock (sync)
            {
                foreach (var job in pending) job.QueuePosition = positions[job.Id];
                var next = 0;
                for (var i = 0; i < jobs.Count; i++) if (positions.ContainsKey(jobs[i].Id)) jobs[i] = pending[next++];
            }
        }
        finally { dispatchGate.Release(); }
        Notify();
    }
    private async Task WorkLoopAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            Job? job;
            await dispatchGate.WaitAsync();
            try
            {
                lock (sync)
                {
                    job = Paused || powerHeld || PersistenceError.Length > 0 || lifetime.IsCancellationRequested ? null : jobs.FirstOrDefault(j => j.Stage == JobStage.Queued && !j.DeletionRequested && !j.CancellationRequested);
                    if (job is not null)
                    {
                        activeId = job.Id;
                        powerInterrupted = false;
                        activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        activeFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        job.Stage = JobStage.Preparing;
                    }
                }
            }
            finally { dispatchGate.Release(); }
            if (job is null)
            {
                try { await Task.Delay(400, lifetime.Token); } catch (OperationCanceledException) { break; }
                continue;
            }
            var cancellation = activeCancellation!;
            using var jobContext = LogContext.PushProperty("JobId", job.Id);
            job.Attempts++;
            job.Activity.Start(job.RunTiming);
            var started = System.Diagnostics.Stopwatch.StartNew();
            Log.Information("Job attempt {Attempt} started using {Engine}", job.Attempts, job.Settings.Engine);
            try { await RunAsync(job, cancellation.Token); job.Activity.Stop(); await PersistOutcomeAsync(job); Log.Information("Job completed at {Stage} in {ElapsedMs} ms", job.Stage, started.ElapsedMilliseconds); }
            catch (OperationCanceledException)
            {
                job.Activity.Stop();
                Log.Information("Job interrupted; user cancellation {UserCancellation}, shutdown {Shutdown}", job.CancellationRequested, lifetime.IsCancellationRequested);
                var interruptedByPower = false; lock (sync) interruptedByPower = powerInterrupted;
                if (!job.ExportCommitted) job.Stage = job.CancellationRequested || (!lifetime.IsCancellationRequested && !interruptedByPower) ? JobStage.Cancelled : JobStage.Queued;
                job.Error = "";
                job.FailureCategory = job.ExportCommitted || ((lifetime.IsCancellationRequested || interruptedByPower) && !job.CancellationRequested) ? FailureCategory.None : FailureCategory.Cancelled;
                job.FailedStage = null;
                if (!job.ExportCommitted && !job.CancellationRequested && HasUnresolvedHostedRequest(job))
                {
                    job.Stage = JobStage.Failed; job.FailedStage = JobStage.Synthesizing; job.FailureCategory = FailureCategory.ServiceConnection;
                    job.Error = "An interrupted hosted request may have been billed. Inspect provider usage, then explicitly Retry / resume to authorize another request. Validated segments will be reused.";
                }
                await PersistOutcomeAsync(job);
            }
            catch (Exception error)
            {
                job.Activity.Stop();
                Log.Error("Job failed at {Stage} with category {FailureCategory} after {ElapsedMs} ms", job.Stage, Categorize(error, job.Stage), started.ElapsedMilliseconds);
                AppLogging.Failure("RunJob", error);
                job.FailedStage = job.Stage;
                job.FailureCategory = Categorize(error, job.Stage);
                job.Stage = job.ExportCommitted ? JobStage.Exported : JobStage.Failed;
                job.Error = FriendlyError(error);
                await PersistOutcomeAsync(job);
            }
            finally
            {
                job.Activity.Stop();
                lock (sync)
                {
                    activeId = null;
                    activeCancellation = null;
                    activeFinished!.TrySetResult();
                }
                cancellation.Dispose();
                Notify();
            }
            try { await CleanCacheAsync(lifetime.Token); MaintenanceError = ""; }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
            catch (Exception error) { AppLogging.Failure("CacheMaintenance", error); MaintenanceError = FriendlyError(error); }
        }
    }
    public static string FriendlyError(Exception error) => error switch
    {
        IOException or ArgumentException or TimeoutException => error.Message,
        UnauthorizedAccessException => "Access was denied. Check folder permissions; the source and validated local audio are preserved.",
        System.ComponentModel.Win32Exception => "A required tool is unavailable. Check Docker Desktop and FFmpeg installation, then retry.",
        HttpRequestException => "The local speech connection failed. Check service readiness and retry; validated chunks are retained.",
        _ => $"The operation failed ({error.GetType().Name}). Your local job is preserved. Check prerequisites and retry."
    };
    public static FailureCategory Categorize(Exception error, JobStage stage) => error switch
    {
        OperationCanceledException => FailureCategory.Cancelled,
        TimeoutException => FailureCategory.Timeout,
        UnauthorizedAccessException => FailureCategory.AccessDenied,
        System.ComponentModel.Win32Exception => FailureCategory.Prerequisite,
        HttpRequestException => FailureCategory.ServiceConnection,
        _ when stage is JobStage.WaitingForService or JobStage.Synthesizing => FailureCategory.ServiceContract,
        _ when stage is JobStage.Assembling or JobStage.Validating => FailureCategory.AudioValidation,
        _ when stage is JobStage.Exporting => FailureCategory.Export,
        IOException => FailureCategory.Storage,
        _ => FailureCategory.Unexpected
    };
    private async Task StageAsync(Job job, JobStage stage, CancellationToken ct)
    {
        job.Stage = stage;
        job.Activity.Update(JobProgress.Describe(job).Guidance);
        await CheckpointAsync(job, ct);
        Log.Information("Job {JobId} stage {Stage}; completed {CompletedChunks} of {ChunkCount} chunks", job.Id, stage, job.CompletedChunks, job.Chunks.Count);
        Notify();
    }
    private Task CheckpointAsync(Job job, CancellationToken ct)
    {
        job.RunTiming = job.Activity.Read().Timing;
        return store.SaveAsync(job, ct);
    }
    private async Task RunAsync(Job job, CancellationToken ct)
    {
        if (job.CancellationRequested) throw new OperationCanceledException(ct);
        var directory = workspace.JobDirectory(job.Id);
        Workspace.RejectReparsePoints(directory);
        Directory.CreateDirectory(directory);
        await PrivateJobFiles.ReconcilePromotionsAsync(job, directory, () => CheckpointAsync(job, ct), ct);
        job.Error = "";
        job.FailureCategory = FailureCategory.None; job.FailedStage = null;
        var natural = NaturalChunker.IsNatural(job);
        lock (sync) if (job.Chunks.Count == 0) job.Chunks = natural ? NaturalChunker.Split(job.Prepared.Script) : Chunker.Split(job.Prepared.Script, 450);
        if (job.Episode is null && !natural && (job.ChunkingVersion != "chunk450-v1" || job.AudioContractVersion != AudioPipeline.ContractVersion)) throw new IOException("This job uses an unsupported chunk/audio contract. Restore its application version or submit a new narration.");
        Chunker.ValidateManifest(job.Chunks, job.Prepared.Script, job.Episode is not null ? 1800 : natural ? 900 : 450);
        ValidateConfiguration(job);
        PrivateJobFiles.RetainReceipts(job);
        await StageAsync(job, JobStage.Preparing, ct);
        var valid = new List<ChunkReceipt>();
        foreach (var chunk in job.Chunks)
        {
            var receipt = job.Receipts.FirstOrDefault(r => r.Index == chunk.Index && r.Fingerprint == job.Fingerprint);
            var file = workspace.ChunkPath(job, chunk.Index);
            if (receipt is null || !File.Exists(file)) continue;
            try
            {
                if (await Workspace.HashFileAsync(file, ct) == receipt.Hash)
                {
                    await audio.ValidateChunkAsync(file, chunk.Text, ct);
                    valid.Add(receipt);
                }
            }
            catch (IOException error) { AppLogging.Failure("CachedChunkValidation", error, Serilog.Events.LogEventLevel.Warning); }
        }
        lock (sync) job.Receipts = valid;
        job.CompletedChunks = valid.Count;
        if (HasUnresolvedHostedRequest(job))
            throw new IOException("Previously requested hosted audio is missing or invalid and may have been billed. Inspect provider usage, then explicitly Retry / resume to authorize regeneration.");
        var final = workspace.FinalPath(job);
        var finalValid = valid.Count == job.Chunks.Count && job.FinalHash.Length > 0 && File.Exists(final) && await Workspace.HashFileAsync(final, ct) == job.FinalHash;
        if (!finalValid)
        {
            job.FinalHash = "";
            if (valid.Count != job.Chunks.Count)
            {
                job.Activity.Update("Waiting for other local audio work to finish", true);
                await InferenceGate.WaitAsync(ct);
                try
                {
                    await StageAsync(job, JobStage.WaitingForService, ct);
                    var readinessStarted = System.Diagnostics.Stopwatch.StartNew();
                    var info = provider is IJobSpeechStatusProvider statusProvider
                        ? await statusProvider.ReadyForJobAsync(job, ct) : await provider.ReadyAsync(job.Settings.Engine, ct);
                    job.SpeechReadinessMilliseconds = (job.SpeechReadinessMilliseconds ?? 0) + readinessStarted.ElapsedMilliseconds;
                    if (info.Fingerprint != job.Settings.ProviderFingerprint || job.Settings.ProviderImageId is not null && info.ImageId != job.Settings.ProviderImageId)
                        throw new IOException("The speech model or image changed since submission. Restore it or submit a new job to avoid mixed audio.");
                    if (job.Episode is { } episode && episode.Speakers.Any(s => !info.Voices.Contains(s.Voice))) throw new IOException("A captured podcast voice is unavailable. Existing segments are preserved. Restore that voice or create a new draft with a revised cast.");
                    foreach (var chunk in job.Chunks)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (valid.Any(r => r.Index == chunk.Index)) continue;
                        job.Activity.Segment(chunk.Index + 1);
                        await StageAsync(job, JobStage.Synthesizing, ct);
                        var raw = Path.Combine(directory, "inference.partial.wav");
                        var normalized = Path.Combine(directory, "normalized.partial.wav");
                        await PrivateJobFiles.PrepareOutputAsync(job, directory, Path.GetFileName(raw), ct);
                        if (provider is IRenderUnitSpeechProvider units)
                            await units.SynthesizeUnitAsync(job, chunk, raw, () => CheckpointAsync(job, CancellationToken.None), ct);
                        else if (provider is IDurableSpeechProvider durable)
                            await durable.SynthesizeAsync(job, job.Settings, chunk.Text, raw, () => CheckpointAsync(job, ct), ct);
                        else
                        {
                            await provider.SynthesizeAsync(job.Settings, chunk.Text, raw, ct);
                            await PrivateJobFiles.RecordAsync(job, directory, Path.GetFileName(raw), ct);
                            await CheckpointAsync(job, ct);
                        }
                        job.Activity.Update("Preparing and checking this segment's audio");
                        await audio.NormalizeAsync(job, raw, normalized, () => CheckpointAsync(job, ct), ct);
                        var checkedAudio = await audio.ValidateChunkAsync(normalized, chunk.Text, ct);
                        var hash = await Workspace.HashFileAsync(normalized, ct);
                        ct.ThrowIfCancellationRequested();
                        var chunkName = Path.GetFileName(workspace.ChunkPath(job, chunk.Index));
                        await PrivateJobFiles.MoveRecordedAsync(job, directory, Path.GetFileName(normalized), chunkName, async () =>
                        {
                            lock (sync)
                            {
                                if (!job.Receipts.Any(r => r.Index == chunk.Index)) job.Receipts.Add(new(chunk.Index, hash, job.Fingerprint, checkedAudio.Duration));
                                job.CompletedChunks = job.Receipts.Count;
                            }
                            await CheckpointAsync(job, ct);
                        }, ct, preserveChangedChunk: true);
                        Notify();
                        job.Activity.EndSegment();
                        await OwnedFileRemoval.DeleteByHashAsync(directory, Path.GetFileName(raw), job.PrivateArtifacts.Single(r => r.RelativePath == Path.GetFileName(raw)).Hash, ct: ct);
                    }
                }
                finally { InferenceGate.Release(); }
            }
            if (job.Receipts.Count != job.Chunks.Count || job.Receipts.Select(r => r.Index).Distinct().Count() != job.Chunks.Count) throw new IOException("The chunk sequence is incomplete. Publication is blocked.");
            await StageAsync(job, JobStage.Assembling, ct);
            await audio.AssembleAsync(job, directory, ct, () => CheckpointAsync(job, ct));
        }
        await StageAsync(job, JobStage.Validating, ct);
        var checkedFinal = await audio.ValidateFinalAsync(job, final, ct);
        job.DurationSeconds = checkedFinal.Duration;
        job.FinalHash = await Workspace.HashFileAsync(final, ct);
        await StageAsync(job, JobStage.Generated, ct);
        await publisher.PublishAsync(job, ct);
        Notify();
    }
    public async Task CancelAsync(string id)
    {
        await dispatchGate.WaitAsync();
        try { await CancelCoreAsync(id); }
        finally { dispatchGate.Release(); }
    }
    private async Task CancelCoreAsync(string id)
    {
        Task? pending = null;
        Job? job;
        var saveRequest = false;
        lock (sync)
        {
            job = jobs.FirstOrDefault(j => j.Id == id);
            if (activeId == id)
            {
                pending = activeFinished!.Task;
                if (!job!.ExportCommitted) { job.CancellationRequested = true; saveRequest = true; }
            }
            else if (job is not null && job.Stage == JobStage.Queued) { job.CancellationRequested = true; job.Stage = JobStage.Cancelled; job.FailureCategory = FailureCategory.Cancelled; saveRequest = true; }
            if (job is not null && job.CancellationRequested && !job.ExportCommitted) saveRequest = true;
        }
        Exception? saveFailure = null;
        if (saveRequest)
        {
            try { await store.SaveAsync(job!); }
            catch (Exception error)
            {
                AppLogging.Failure("CancellationCheckpoint", error);
                saveFailure = error; Paused = true;
                PersistenceError = "Queue paused because cancellation could not be recorded. Repair storage and retry the operation. The request may not survive a crash until saved. " + FriendlyError(error);
            }
        }
        // Save the user request before signalling in-flight work; a relaunch must not resume it.
        lock (sync) if (activeId == id) activeCancellation!.Cancel();
        Notify();
        if (pending is not null) await pending;
        else if (job is not null && !saveRequest) await store.SaveAsync(job);
        Notify();
        if (saveFailure is not null) throw new IOException(PersistenceError, saveFailure);
    }
    public async Task RetryAsync(string id, string? destination = null)
    {
        await dispatchGate.WaitAsync();
        try { await RetryCoreAsync(id, destination); }
        finally { dispatchGate.Release(); }
    }
    private async Task RetryCoreAsync(string id, string? destination)
    {
        Task? settling = null;
        lock (sync)
        {
            var current = jobs.FirstOrDefault(j => j.Id == id) ?? throw new ArgumentException("This narration no longer exists. Refresh the library.");
            if (activeId == id && current.Stage is JobStage.Failed or JobStage.Cancelled) settling = activeFinished!.Task;
        }
        if (settling is not null) await settling;
        Job job;
        lock (sync)
        {
            job = jobs.FirstOrDefault(j => j.Id == id) ?? throw new ArgumentException("This narration no longer exists. Refresh the library.");
            if (activeId == id || job.Stage == JobStage.Queued) throw new ArgumentException("This job is already queued or running.");
            if (job.ExportCommitted) throw new ArgumentException("This job was exported. Submit a new job to regenerate it.");
            if (job.DeletionRequested) throw new ArgumentException("This narration is pending deletion. Retry its deletion to finish removal.");
        }
        var queued = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(job))!;
        if (job.SpeechAttempts?.LastOrDefault()?.RetryAfterUtc is { } retryAfter && retryAfter > DateTimeOffset.UtcNow)
            throw new IOException($"The speech provider requested backoff until {retryAfter.ToLocalTime():HH:mm:ss}. Retry after that time; retained segments are preserved.");
        if (SpeechProviders.IsHosted(job.Settings.Engine)) queued.HostedRetryAuthorizedUtc = DateTimeOffset.UtcNow;
        if (destination is not null)
        {
            workspace.GuardLocalDestination(destination);
            var stagingCleanup = await publisher.RemoveOwnedStagingAsync(job, lifetime.Token, preserveUnknown: true);
            if (stagingCleanup == ExportStagingCleanup.PreservedUnknown)
                queued.ExportNotice = "An unrecognized or altered staging file was preserved in the previous output folder. Inspect it manually; retry uses the newly selected folder.";
            queued.Destination = destination; queued.ExportName = ""; queued.ExportHash = ""; queued.ExportStagingOwned = false; queued.ExportStagingIdentity = null;
        }
        queued.Stage = JobStage.Queued; queued.Error = ""; queued.FailureCategory = FailureCategory.None; queued.FailedStage = null;
        queued.CancellationRequested = false;
        var admission = Snapshot().Where(j => j.Id != id).Append(queued).ToList();
        await CheckStorageBudgetAsync(admission, null, lifetime.Token);
        // Persist queue intent before the worker is allowed to pick up the job.
        await store.SaveAsync(queued);
        lock (sync) jobs[jobs.FindIndex(j => j.Id == id)] = queued;
        PersistenceError = "";
        Log.Information("Job {JobId} retry queued", id);
        Notify();
    }
    public async Task DeleteAsync(string id, bool deleteExport)
    {
        var result = await DeleteManyAsync([id], deleteExport);
        if (result.Failed > 0) throw new IOException(result.Items[0].Error);
    }
    public async Task<DeletionReview> ReviewDeletionAsync(IReadOnlyList<string> ids)
    {
        var selectedIds = ids.ToHashSet(StringComparer.Ordinal);
        if (selectedIds.Count != ids.Count || selectedIds.Count is < 1 or > 100000) throw new ArgumentException("Review distinct selected narrations.");
        await dispatchGate.WaitAsync();
        try
        {
            var recorded = (await store.LoadAsync()).Where(j => selectedIds.Contains(j.Id)).ToDictionary(j => j.Id, StringComparer.Ordinal);
            if (recorded.Count != selectedIds.Count) throw new ArgumentException("The selected library changed. Review the remaining narrations before deletion.");
            lock (sync)
                foreach (var job in jobs.Where(j => selectedIds.Contains(j.Id)))
                {
                    job.DeleteExportRequested |= recorded[job.Id].DeleteExportRequested;
                    if (recorded[job.Id].DeletionRequested) { job.DeletionRequested = true; job.Stage = JobStage.Deleting; }
                }
            Notify();
            return new(recorded.Count, recorded.Values.Count(j => j.DeletionRequested), recorded.Values.Count(j => j.DeleteExportRequested));
        }
        finally { dispatchGate.Release(); }
    }
    public async Task<DeletionResult> DeleteManyAsync(IReadOnlyList<string> ids, bool deleteExports)
    {
        var selectedIds = ids.ToArray();
        if (selectedIds.Length == 0) return new([]);
        if (selectedIds.Length > 100000 || selectedIds.Distinct(StringComparer.Ordinal).Count() != selectedIds.Length) throw new ArgumentException("Select distinct narrations before deletion.");
        await dispatchGate.WaitAsync();
        try
        {
            Job[] selected; string? selectedActive;
            lock (sync)
            {
                var byId = jobs.ToDictionary(j => j.Id, StringComparer.Ordinal);
                selected = selectedIds.Select(id => byId.TryGetValue(id, out var job) ? job : throw new ArgumentException("The selected library changed. Review the remaining narrations before deletion.")).ToArray();
                selectedActive = selected.Any(j => j.Id == activeId) ? activeId : null;
            }
            // Stop the selected worker before touching any selected item's files, irrespective of display order.
            if (selectedActive is not null) await CancelCoreAsync(selectedActive);
            IReadOnlyDictionary<string, bool> effectiveScopes;
            try
            {
                effectiveScopes = await store.RequestDeletionAsync(selectedIds, deleteExports);
                if (effectiveScopes.Count != selected.Length || selected.Any(j => !effectiveScopes.TryGetValue(j.Id, out var scope) || (deleteExports || j.DeleteExportRequested) && !scope))
                    throw new IOException("Recorded deletion scopes could not be verified. Narration removal was not started.");
            }
            catch (Exception error) { PauseForDeletionCheckpoint(error); Notify(); throw new IOException(PersistenceError, error); }
            if (ReferenceEquals(PersistenceError, deletionCheckpointError)) PersistenceError = "";
            deletionCheckpointError = null;
            lock (sync)
                foreach (var job in selected)
                {
                    job.DeletionRequested = true; job.DeleteExportRequested = effectiveScopes[job.Id]; job.Stage = JobStage.Deleting;
                    job.Error = ""; job.FailureCategory = FailureCategory.None; job.FailedStage = null;
                }
            Notify();
            var results = new List<DeletionItemResult>();
            foreach (var job in selected)
            {
                try
                {
                    await FinishDeletionAsync(job); lock (sync) jobs.Remove(job);
                    results.Add(new(job.Id, job.Title, true, ""));
                }
                catch (Exception error)
                {
                    AppLogging.Failure("JobDeletion", error);
                    job.Stage = JobStage.Deleting; job.Error = FriendlyError(error); job.FailureCategory = Categorize(error, JobStage.Deleting);
                    try { await store.SaveAsync(job); }
                    catch (Exception checkpointError) { PauseForDeletionCheckpoint(checkpointError); }
                    results.Add(new(job.Id, job.Title, false, job.Error));
                }
                Notify();
            }
            return new(results.ToArray());
        }
        finally { dispatchGate.Release(); }
    }
    private void PauseForDeletionCheckpoint(Exception error)
    {
        AppLogging.Failure("DeletionCheckpoint", error);
        Paused = true;
        PersistenceError = "Queue paused because deletion requests or results could not be confirmed. Repair storage and retry deletion. Recorded requests can finish on relaunch. " + FriendlyError(error);
        deletionCheckpointError = PersistenceError;
    }
    private async Task FinishDeletionAsync(Job job)
    {
        if (job.DeleteExportRequested) await publisher.RemoveManagedExportAsync(job, CancellationToken.None);
        var directory = workspace.JobDirectory(job.Id);
        await PrivateJobFiles.RemoveAsync(job, directory);
        await store.RemoveAsync(job.Id);
        Log.Information("Job {JobId} deleted; remove export {RemoveExport}", job.Id, job.DeleteExportRequested);
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (worker is not null) await worker.WaitAsync(TimeSpan.FromSeconds(20));
        lifetime.Dispose();
        InferenceGate.Dispose();
    }
}
