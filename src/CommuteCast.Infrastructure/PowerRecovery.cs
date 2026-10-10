namespace CommuteCast.Infrastructure;

public sealed partial class QueueCoordinator
{
    private bool powerHeld, powerInterrupted;
    public bool PowerSuspended { get { lock (sync) return powerHeld; } }
    /// <summary>Hold dispatch separately from the user's pause setting and interrupt only this queue's worker.</summary>
    public Task SuspendForPowerAsync(CancellationToken ct = default)
    {
        CancellationTokenSource? cancellation;
        lock (sync)
        {
            powerHeld = true; cancellation = activeCancellation;
            if (activeId is not null)
            {
                powerInterrupted = true;
                jobs.FirstOrDefault(j => j.Id == activeId)?.Activity.Stop(); // suspend time must not accrue as processing time
            }
        }
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { /* the captured attempt finished between capture and cancellation */ }
        Notify();
        return WaitForPowerSettlementAsync(ct);
    }
    public Task WaitForPowerSettlementAsync(CancellationToken ct = default)
    {
        Task? settled; lock (sync) settled = activeId is null ? null : activeFinished?.Task;
        return settled is null ? Task.CompletedTask : settled.WaitAsync(TimeSpan.FromSeconds(20), ct);
    }
    /// <summary>Called only after wake checks; preserved pause and persistence-error state still control dispatch.</summary>
    public void ReleasePowerHold()
    {
        lock (sync)
        {
            if (activeId is not null) throw new IOException("The interrupted narration has not settled. Generation remains held for wake recovery.");
            powerHeld = false;
        }
        Notify();
    }
}
