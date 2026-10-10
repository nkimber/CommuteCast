using System.Reflection;
using CommuteCast.Core;
using CommuteCast.Desktop;
using CommuteCast.Infrastructure;
using Microsoft.Win32;

namespace CommuteCast.Desktop.Tests;

[Collection("Desktop")]
public class LifecycleTests
{
    internal sealed class Events : IPowerEvents
    {
        public event Action<PowerModes>? Changed;
        public bool Disposed { get; private set; }
        public void Send(PowerModes mode) => Changed?.Invoke(mode);
        public void Dispose() => Disposed = true;
    }
    internal sealed class Notifications : IUserNotifications
    {
        public List<JobNotification> Items { get; } = [];
        public Action? Click { get; private set; }
        public bool Disposed { get; private set; }
        public void Show(JobNotification notification, Action clicked) { Items.Add(notification); Click = clicked; }
        public void Dispose() => Disposed = true;
    }
    private static QueueCoordinator Queue(MainViewModel model) => (QueueCoordinator)typeof(MainViewModel).GetField("queue", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(model)!;
    [Fact]
    public Task NotificationMuteAndClickRespectSavedStateAndRevealTheExactItem() => DesktopHost.Run(async () =>
    {
        var events = new Events(); var notices = new Notifications(); var model = new MainViewModel(new() { QueuePaused = true }, DesktopHost.Workspace(), powerEvents: events, notifications: notices);
        try
        {
            var job = new Job { Title = "Private title", Stage = JobStage.Queued }; model.RefreshJobs([job]);
            job.Stage = JobStage.Exported; job.ExportCommitted = true; model.RefreshJobs([job]);
            Assert.Single(notices.Items); model.LibrarySearch = "hidden"; notices.Click!();
            Assert.Equal(job.Id, model.SelectedJob!.Id); Assert.Equal("", model.LibrarySearch);
            model.NotificationsEnabled = false; job.Stage = JobStage.Failed; job.ExportCommitted = false; model.RefreshJobs([job]);
            Assert.Single(notices.Items); model.NotificationsEnabled = true; model.RefreshJobs([job]); Assert.Single(notices.Items);
        }
        finally { await model.DisposeAsync(); }
        Assert.True(events.Disposed); Assert.True(notices.Disposed);
    });
    [Fact]
    public Task NativePowerEventsClearTheRecoveryBannerAndKeepAnIntentionallyPausedQueue() => DesktopHost.Run(async () =>
    {
        var events = new Events(); var model = new MainViewModel(new() { QueuePaused = true }, DesktopHost.Workspace(), powerEvents: events, notifications: new Notifications());
        try
        {
            events.Send(PowerModes.Suspend); await Task.Delay(100);
            Assert.True(model.HasPowerRecovery); Assert.True(Queue(model).PowerSuspended);
            events.Send(PowerModes.Resume);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (model.HasPowerRecovery) await Task.Delay(20, deadline.Token);
            Assert.False(Queue(model).PowerSuspended); Assert.Equal("Resume queue", model.PauseLabel); Assert.Contains("remains paused", model.StatusMessage);
        }
        finally { await model.DisposeAsync(); }
    });
    [Fact]
    public Task WakeFailureAndModelMismatchKeepDispatchHeldUntilACompatibleRecheck() => DesktopHost.Run(async () =>
    {
        var model = new MainViewModel(new() { QueuePaused = true }, DesktopHost.Workspace(), powerEvents: new Events(), notifications: new Notifications());
        try
        {
            var queue = Queue(model); var source = "Saved original source.";
            var job = new Job { Source = source, Prepared = TextPreparation.Prepare(source), Settings = new("kokoro", "af_heart", 1, false, "", "original") };
            await queue.AddAsync(job); await model.SuspendForPowerAsync();
            await model.RecoverAfterWakeAsync((_, _) => throw new System.IO.IOException("Service unavailable"));
            Assert.True(queue.PowerSuspended); Assert.Contains("Service unavailable", model.PowerRecoveryMessage);
            await model.RecoverAfterWakeAsync((engine, _) => Task.FromResult(new ProviderInfo(engine, "changed", ["af_heart"], "ready", 0)));
            Assert.True(queue.PowerSuspended); Assert.Contains("different speech model", model.PowerRecoveryMessage);
            await model.RecoverAfterWakeAsync((engine, _) => Task.FromResult(new ProviderInfo(engine, "original", ["af_heart"], "ready", 0)));
            Assert.False(queue.PowerSuspended); Assert.True(queue.Paused); Assert.Equal(source, Assert.Single(queue.Snapshot()).Source);
        }
        finally { await model.DisposeAsync(); }
    });
    [Fact]
    public Task ASecondSuspendCancelsAnOlderWakeCheckWithoutReleasingItsHold() => DesktopHost.Run(async () =>
    {
        var model = new MainViewModel(new() { QueuePaused = true }, DesktopHost.Workspace(), powerEvents: new Events(), notifications: new Notifications());
        try
        {
            var queue = Queue(model); var source = "Saved original source.";
            await queue.AddAsync(new() { Source = source, Prepared = TextPreparation.Prepare(source) });
            await model.SuspendForPowerAsync();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var checking = model.RecoverAfterWakeAsync(async (_, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException(); });
            await entered.Task; await model.SuspendForPowerAsync(); await checking;
            Assert.True(queue.PowerSuspended); Assert.Contains("suspend detected", model.PowerRecoveryMessage);
        }
        finally { await model.DisposeAsync(); }
    });
}
