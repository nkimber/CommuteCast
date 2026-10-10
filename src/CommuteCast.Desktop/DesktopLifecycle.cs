using System.Windows;
using System.Windows.Input;
using CommuteCast.Core;
using CommuteCast.Infrastructure;
using Microsoft.Win32;

namespace CommuteCast.Desktop;

public sealed partial class MainViewModel
{
    private IPowerEvents powerEvents = null!;
    private IUserNotifications notifications = null!;
    private readonly JobNotifications notificationChanges = new();
    private readonly SemaphoreSlim wakeGate = new(1);
    private CancellationTokenSource? wakeCancellation;
    private int powerEpoch;
    private string powerRecoveryMessage = "";
    public bool NotificationsEnabled { get => settings.NotificationsEnabled; set { settings.NotificationsEnabled = value; Raise(); } }
    public string PowerRecoveryMessage { get => powerRecoveryMessage; private set { if (Set(ref powerRecoveryMessage, value)) Raise(nameof(HasPowerRecovery)); } }
    public bool HasPowerRecovery => PowerRecoveryMessage.Length > 0;
    public ICommand RecheckWakeCommand { get; private set; } = null!;
    public event Action? NotificationOpened;
    private void InitializeLifecycle(IPowerEvents? events, IUserNotifications? notifier)
    {
        powerEvents = events ?? new WindowsPowerEvents(); notifications = notifier ?? new WindowsNotifications();
        powerEvents.Changed += PowerChanged;
        RecheckWakeCommand = Command(_ => RecoverAfterWakeAsync());
    }
    private void PowerChanged(PowerModes mode)
    {
        if (mode == PowerModes.StatusChange || shutdown.IsCancellationRequested) return;
        _ = Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            try { await operations.RunAsync(() => mode == PowerModes.Suspend ? SuspendForPowerAsync() : RecoverAfterWakeAsync()); }
            catch (OperationCanceledException) { }
            catch (Exception error) { AppLogging.Failure("PowerTransition", error); PowerRecoveryMessage = "Generation is held after a power transition. Recheck wake recovery: " + QueueCoordinator.FriendlyError(error); }
        });
    }
    internal async Task SuspendForPowerAsync()
    {
        ++powerEpoch; wakeCancellation?.Cancel();
        PowerRecoveryMessage = "Laptop suspend detected. Generation is held; validated segments and queued narrations are retained.";
        var settlement = queue.SuspendForPowerAsync(shutdown.Token);
        try { await StopPlaybackAsync(false); }
        catch (Exception error) { AppLogging.Failure("SuspendPreviewCleanup", error); }
        await settlement;
    }
    internal async Task RecoverAfterWakeAsync(Func<string, CancellationToken, Task<ProviderInfo>>? readiness = null)
    {
        await wakeGate.WaitAsync(shutdown.Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        wakeCancellation = cancellation;
        var epoch = powerEpoch;
        try
        {
            if (!queue.PowerSuspended) return;
            PowerRecoveryMessage = "Laptop awake. Checking interrupted speech and saved work before generation resumes…";
            await queue.WaitForPowerSettlementAsync(cancellation.Token);
            var pending = queue.Snapshot().Where(j => j.Stage == JobStage.Queued && !j.CancellationRequested && !j.DeletionRequested && j.FinalHash.Length == 0).ToArray();
            foreach (var engine in pending.Select(j => j.Settings.Engine).Distinct())
            {
                cancellation.Token.ThrowIfCancellationRequested();
                PowerRecoveryMessage = $"Laptop awake. Checking {engine} and its captured model before resuming saved segments…";
                var info = await (readiness is null ? CheckReadinessAsync(false, engine, cancellation.Token) : readiness(engine, cancellation.Token));
                if (info.Engine != engine || info.State != "ready") throw new System.IO.IOException("The saved speech service is not ready.");
                if (pending.Where(j => j.Settings.Engine == engine).Any(j => j.Settings.ProviderFingerprint != info.Fingerprint || j.Settings.ProviderImageId is not null && j.Settings.ProviderImageId != info.ImageId))
                    throw new System.IO.IOException("A saved narration requires a different speech model/image. Restore its original service or use it as a new draft; saved chunks cannot be mixed with a changed model.");
            }
            // Ownership recovery shares inference admission and preserves altered/unrecorded preview files.
            await auditions.RecoverAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (epoch != powerEpoch) return;
            queue.ReleasePowerHold(); RefreshJobs(queue.Snapshot());
            PowerRecoveryMessage = "";
            StatusMessage = queue.Paused ? "Wake recovery finished. Your queue remains paused; choose Resume queue when ready." : "Wake recovery finished. Saved queued work resumes and verifies retained segments before reuse.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            AppLogging.Failure("WakeRecovery", error);
            PowerRecoveryMessage = "Generation remains held after wake. " + QueueCoordinator.FriendlyError(error) + " Recheck wake recovery after repair.";
        }
        finally { if (ReferenceEquals(wakeCancellation, cancellation)) wakeCancellation = null; wakeGate.Release(); }
    }
    private void NotifyJobChanges(IReadOnlyList<Job> snapshots)
    {
        foreach (var notice in notificationChanges.Observe(snapshots))
        {
            if (!NotificationsEnabled || shutdown.IsCancellationRequested) continue;
            try
            {
                notifications.Show(notice, () =>
                {
                    if (shutdown.IsCancellationRequested) return;
                    RevealJob(Jobs.FirstOrDefault(j => j.Id == notice.JobId)); NotificationOpened?.Invoke();
                });
            }
            catch (Exception error) { AppLogging.Failure("DesktopNotification", error); }
        }
    }
}
