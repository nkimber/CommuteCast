namespace CommuteCast.Infrastructure;

/// <summary>Stops accepting work before waiting for every admitted operation to settle.</summary>
public sealed class OperationLifetime
{
    private readonly object sync = new();
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool stopping;
    private int active;
    public async Task RunAsync(Func<Task> action)
    {
        lock (sync)
        {
            if (stopping) throw new OperationCanceledException("The application is stopping its current operations.");
            active++;
        }
        try { await action(); }
        finally
        {
            lock (sync) { active--; if (stopping && active == 0) stopped.TrySetResult(); }
        }
    }
    public Task StopAsync()
    {
        lock (sync) { stopping = true; if (active == 0) stopped.TrySetResult(); return stopped.Task; }
    }
}
