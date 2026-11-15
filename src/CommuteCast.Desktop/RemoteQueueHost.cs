using System.IO;
using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Desktop;

internal sealed class RemoteQueueHost
{
    private readonly RemoteQueueWorker worker;
    private readonly RemoteQueueSettings settings;
    private readonly QueueCoordinator queue;
    private readonly RemoteRenderPlanner planner;
    private readonly NarrationPreset[] capabilities;
    private readonly CancellationTokenSource stopping = new();
    private readonly Task[] loops;
    private volatile Health? health;
    private volatile bool folderTrouble;
    public string WorkerId => worker.WorkerId;

    public RemoteQueueHost(RemoteQueueWorker worker, RemoteQueueSettings settings, QueueCoordinator queue,
        RemoteRenderPlanner planner, NarrationPreset[] capabilities)
    {
        this.worker = worker;
        this.settings = settings;
        this.queue = queue;
        this.planner = planner;
        this.capabilities = capabilities;
        loops = [Task.Run(PollAsync), Task.Run(CheckInAsync)];
        if (settings.EnableWorker) loops = [.. loops, Task.Run(ProbeAsync)];
    }

    private async Task PollAsync()
    {
        var ct = stopping.Token;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try { await worker.PollAsync(ct, settings.EnableWorker); folderTrouble = false; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { folderTrouble = true; AppLogging.Failure("RemoteQueuePoll", error); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task ProbeAsync()
    {
        var ct = stopping.Token;
        using var timer = new PeriodicTimer(RemoteWorkerStatus.CheckInInterval);
        do
        {
            if (WorkInProgress()) continue;
            var options = capabilities.Select(async preset =>
                await planner.IsAvailableAsync(preset.Options, ct) ? preset.Name : null);
            var available = (await Task.WhenAll(options)).OfType<string>().ToArray();
            health = new(available);
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task CheckInAsync()
    {
        var ct = stopping.Token;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        DateTimeOffset lastCheckIn = default;
        RemoteWorkerState? lastState = null;
        string[] lastPresets = [];
        do
        {
            var current = health;
            var state = CurrentState(current);
            var presets = current?.Presets ?? [];
            var now = DateTimeOffset.UtcNow;
            if (lastState == state && presets.SequenceEqual(lastPresets) && now < lastCheckIn + RemoteWorkerStatus.CheckInInterval)
                continue;
            try
            {
                await worker.CheckInAsync(state, presets);
                if (state == RemoteWorkerState.Disabled) return;
                lastCheckIn = now; lastState = state; lastPresets = presets;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { AppLogging.Failure("RemoteWorkerCheckIn", error); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private RemoteWorkerState CurrentState(Health? current)
    {
        if (!settings.EnableWorker) return RemoteWorkerState.Disabled;
        if (queue.PowerSuspended || folderTrouble || queue.Paused || queue.PersistenceError.Length > 0) return RemoteWorkerState.NeedsAttention;
        if (WorkInProgress()) return RemoteWorkerState.Busy;
        if (current is null) return RemoteWorkerState.Starting;
        return current.Presets.Length > 0 ? RemoteWorkerState.Ready : RemoteWorkerState.NeedsAttention;
    }

    private bool WorkInProgress() => queue.Snapshot().Any(job => JobProgress.Describe(job).IsWorking && job.Stage != JobStage.Queued);

    public async Task<bool> RetryAsync(string id, CancellationToken ct)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, stopping.Token);
        return await worker.RetryAsync(id, cancellation.Token);
    }

    public async Task StopAsync(bool disable)
    {
        await stopping.CancelAsync();
        try { await Task.WhenAll(loops); }
        catch (OperationCanceledException) { }
        catch (Exception error) { AppLogging.Failure("RemoteWorkerStop", error); }
        finally { stopping.Dispose(); }
        if (!settings.EnableWorker && !disable) return;
        try { await worker.CheckInAsync(disable || !settings.EnableWorker ? RemoteWorkerState.Disabled : RemoteWorkerState.Stopped, []); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { AppLogging.Failure("RemoteWorkerFinalCheckIn", error); }
    }

    private sealed record Health(string[] Presets);
}
