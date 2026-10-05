using CommuteCast.Core;
using System.Text.Json;

namespace CommuteCast.Infrastructure;

public sealed class QueueCoordinator(Workspace workspace, IJobStore store, ISpeechProvider provider, AudioPipeline audio, ExportPublisher publisher) : IAsyncDisposable
{
    private readonly List<Job> jobs = [];
    private readonly object sync = new();
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? activeCancellation;
    private string? activeId;
    private TaskCompletionSource? activeFinished;
    private Task? worker;
    public SemaphoreSlim InferenceGate { get; } = new(1);
    public bool Paused { get; set; }
    public event Action<IReadOnlyList<Job>>? Changed;
    public IReadOnlyList<Job> Snapshot()
    {
        lock (sync) return jobs.Select(j => JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(j))!).ToArray();
    }
    private void Notify() => Changed?.Invoke(Snapshot());
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var loaded = await store.LoadAsync(ct);
        var retained = new List<Job>();
        foreach (var job in loaded)
        {
            if (job.DeletionRequested)
            {
                try { await FinishDeletionAsync(job); }
                catch (Exception error) { job.Stage = JobStage.Deleting; job.Error = FriendlyError(error); retained.Add(job); await store.SaveAsync(job, ct); }
                continue;
            }
            try { await publisher.ReconcileAsync(job, ct); }
            catch (IOException) { job.Error = "Export reconciliation failed. The local job is preserved; inspect the destination and retry."; job.Stage = JobStage.Failed; }
            if (job.Stage is not (JobStage.Exported or JobStage.Cancelled or JobStage.Failed))
            {
                job.Stage = JobStage.Queued;
                await store.SaveAsync(job, ct);
            }
            retained.Add(job);
        }
        lock (sync) jobs.AddRange(retained);
        Notify();
        worker = Task.Run(WorkLoopAsync);
    }
    public async Task AddAsync(Job job, CancellationToken ct = default)
    {
        await store.SaveAsync(job, ct);
        lock (sync) jobs.Add(job);
        Notify();
    }
    private async Task WorkLoopAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            Job? job;
            lock (sync)
            {
                job = Paused ? null : jobs.FirstOrDefault(j => j.Stage == JobStage.Queued && !j.DeletionRequested);
                if (job is not null)
                {
                    activeId = job.Id;
                    activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    activeFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    job.Stage = JobStage.Preparing;
                }
            }
            if (job is null)
            {
                try { await Task.Delay(400, lifetime.Token); } catch (OperationCanceledException) { break; }
                continue;
            }
            var cancellation = activeCancellation!;
            try { await RunAsync(job, cancellation.Token); }
            catch (OperationCanceledException)
            {
                if (!job.ExportCommitted) job.Stage = lifetime.IsCancellationRequested ? JobStage.Queued : JobStage.Cancelled;
                job.Error = "";
                await store.SaveAsync(job, CancellationToken.None);
            }
            catch (Exception error)
            {
                job.Stage = job.ExportCommitted ? JobStage.Exported : JobStage.Failed;
                job.Error = FriendlyError(error);
                await store.SaveAsync(job, CancellationToken.None);
            }
            finally
            {
                lock (sync)
                {
                    activeId = null;
                    activeCancellation = null;
                    activeFinished!.TrySetResult();
                }
                cancellation.Dispose();
                Notify();
            }
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
    private async Task StageAsync(Job job, JobStage stage, CancellationToken ct)
    {
        job.Stage = stage;
        await store.SaveAsync(job, ct);
        Notify();
    }
    private async Task RunAsync(Job job, CancellationToken ct)
    {
        var directory = workspace.JobDirectory(job.Id);
        Workspace.RejectReparsePoints(directory);
        Directory.CreateDirectory(directory);
        job.Error = "";
        lock (sync) if (job.Chunks.Count == 0) job.Chunks = Chunker.Split(job.Prepared.Script, 450);
        if (string.Concat(job.Chunks.Select(c => c.Text)) != job.Prepared.Script) throw new IOException("Chunk coverage is invalid. No audio was exported.");
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
            catch (IOException) { }
        }
        lock (sync) job.Receipts = valid;
        job.CompletedChunks = valid.Count;
        var final = workspace.FinalPath(job);
        var finalValid = valid.Count == job.Chunks.Count && job.FinalHash.Length > 0 && File.Exists(final) && await Workspace.HashFileAsync(final, ct) == job.FinalHash;
        if (!finalValid)
        {
            job.FinalHash = "";
            if (valid.Count != job.Chunks.Count)
            {
                await InferenceGate.WaitAsync(ct);
                try
                {
                    await StageAsync(job, JobStage.WaitingForService, ct);
                    var info = await provider.ReadyAsync(job.Settings.Engine, ct);
                    if (info.Fingerprint != job.Settings.ProviderFingerprint) throw new IOException("The speech model changed since submission. Restore it or submit a new job to avoid mixed audio.");
                    foreach (var chunk in job.Chunks)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (valid.Any(r => r.Index == chunk.Index)) continue;
                        await StageAsync(job, JobStage.Synthesizing, ct);
                        var raw = Path.Combine(directory, "inference.partial.wav");
                        var normalized = Path.Combine(directory, "normalized.partial.wav");
                        await provider.SynthesizeAsync(job.Settings, chunk.Text, raw, ct);
                        await audio.NormalizeAsync(raw, normalized, ct);
                        var checkedAudio = await audio.ValidateChunkAsync(normalized, chunk.Text, ct);
                        var hash = await Workspace.HashFileAsync(normalized, ct);
                        ct.ThrowIfCancellationRequested();
                        File.Move(normalized, workspace.ChunkPath(job, chunk.Index), true);
                        lock (sync) job.Receipts.Add(new(chunk.Index, hash, job.Fingerprint, checkedAudio.Duration));
                        job.CompletedChunks = job.Receipts.Count;
                        await store.SaveAsync(job, ct);
                        Notify();
                        File.Delete(raw);
                    }
                }
                finally { InferenceGate.Release(); }
            }
            if (job.Receipts.Count != job.Chunks.Count || job.Receipts.Select(r => r.Index).Distinct().Count() != job.Chunks.Count) throw new IOException("The chunk sequence is incomplete. Publication is blocked.");
            await StageAsync(job, JobStage.Assembling, ct);
            await audio.AssembleAsync(job, directory, ct);
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
        Task? pending = null;
        Job? job;
        lock (sync)
        {
            job = jobs.FirstOrDefault(j => j.Id == id);
            if (activeId == id)
            {
                activeCancellation!.Cancel();
                pending = activeFinished!.Task;
            }
            else if (job is not null && job.Stage == JobStage.Queued) job.Stage = JobStage.Cancelled;
        }
        if (pending is not null) await pending;
        else if (job is not null) await store.SaveAsync(job);
        Notify();
    }
    public async Task RetryAsync(string id, string? destination = null)
    {
        Task? settling = null;
        lock (sync)
        {
            var current = jobs.First(j => j.Id == id);
            if (activeId == id && current.Stage is JobStage.Failed or JobStage.Cancelled) settling = activeFinished!.Task;
        }
        if (settling is not null) await settling;
        Job job;
        lock (sync)
        {
            job = jobs.First(j => j.Id == id);
            if (activeId == id || job.Stage == JobStage.Queued) throw new ArgumentException("This job is already queued or running.");
            if (job.ExportCommitted) throw new ArgumentException("This job was exported. Submit a new job to regenerate it.");
            if (job.DeletionRequested) throw new ArgumentException("This narration is pending deletion. Retry its deletion to finish removal.");
        }
        if (destination is not null) { workspace.GuardLocalDestination(destination); job.Destination = destination; job.ExportName = ""; job.ExportHash = ""; }
        job.Error = "";
        // Persist queue intent before the worker is allowed to pick up the job.
        job.Stage = JobStage.Preparing;
        var queued = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(job))!;
        queued.Stage = JobStage.Queued;
        await store.SaveAsync(queued);
        lock (sync) job.Stage = JobStage.Queued;
        Notify();
    }
    public async Task DeleteAsync(string id, bool deleteExport)
    {
        await CancelAsync(id);
        Job job;
        lock (sync) job = jobs.First(j => j.Id == id);
        job.DeletionRequested = true;
        job.DeleteExportRequested |= deleteExport;
        job.Stage = JobStage.Deleting;
        await store.SaveAsync(job);
        await FinishDeletionAsync(job);
        lock (sync) jobs.Remove(job);
        Notify();
    }
    private async Task FinishDeletionAsync(Job job)
    {
        if (job.DeleteExportRequested) await publisher.RemoveManagedExportAsync(job, CancellationToken.None);
        var directory = workspace.JobDirectory(job.Id);
        Workspace.RejectReparsePoints(directory);
        if (Directory.Exists(directory))
        {
            if (Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories).Any(p => File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint))) throw new IOException("Private job storage contains a symbolic link. Removal was refused.");
            Directory.Delete(directory, true);
        }
        await store.RemoveAsync(job.Id);
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (worker is not null) await worker.WaitAsync(TimeSpan.FromSeconds(20));
        lifetime.Dispose();
        InferenceGate.Dispose();
    }
}
