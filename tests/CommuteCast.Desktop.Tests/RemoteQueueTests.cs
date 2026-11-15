using System.IO;
using System.Windows;
using System.Windows.Controls;
using CommuteCast.Core;
using CommuteCast.Desktop;
using CommuteCast.Infrastructure;

namespace CommuteCast.Desktop.Tests;

[Collection("Desktop")]
public sealed class RemoteQueueTests
{
    [Fact]
    public Task RegisteredWorkerReappearsWhenTheSharedFolderBecomesAvailableAfterStartup() => DesktopHost.Run(async () =>
    {
        var workspace = DesktopHost.Workspace();
        var shared = workspace.Root + "-late-shared";
        var settings = new AppSettings { QueuePaused = true, RemoteQueue = new(shared, true, "Basement", Guid.NewGuid().ToString("N"), []) };
        await using var model = new MainViewModel(settings, workspace, powerEvents: new LifecycleTests.Events(), notifications: new LifecycleTests.Notifications());
        await model.InitializeAsync();
        Assert.Contains("attention", model.RemoteQueueLabel);

        Directory.CreateDirectory(shared);
        var folder = new RemoteQueueFolder(shared, workspace);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while ((await folder.ReadWorkersAsync(timeout.Token)).Count == 0) await Task.Delay(100, timeout.Token);
        var original = Assert.Single(await folder.ReadWorkersAsync(timeout.Token));
        Assert.Equal("Basement", original.Name);
        Assert.Equal("Remote queue", model.RemoteQueueLabel);
        await model.SaveRemoteSettingsAsync(shared, false, "Basement", [], false);
        var retired = Assert.Single(await folder.ReadWorkersAsync(timeout.Token));
        Assert.Equal(original.WorkerId, retired.WorkerId);
        Assert.Equal(RemoteWorkerState.Disabled, retired.State);
    });

    [Fact]
    public Task DesktopSubmitsToTheSelectedWorkerWithoutLocalSpeechAndDisplaysTheFinishedFallback() => DesktopHost.Run(async () =>
    {
        var workspace = DesktopHost.Workspace();
        var shared = Directory.CreateDirectory(workspace.Root + "-shared").FullName;
        var folder = new RemoteQueueFolder(shared, workspace);
        var worker = Guid.NewGuid().ToString("N");
        await folder.WriteWorkerAsync(new(worker, "Basement", DateTimeOffset.UtcNow, RemoteWorkerState.Ready, ["Auditioned fallback"]));
        var settings = new AppSettings { RemoteQueue = new(shared, false, "Work laptop", "", []) };
        await using var model = new MainViewModel(settings, workspace, powerEvents: new LifecycleTests.Events(), notifications: new LifecycleTests.Notifications());
        model.Source = "A synthetic narration for the desktop queue test.";
        model.DraftTitle = "Morning request";
        var window = new RemoteQueueWindow(model);
        try
        {
            DesktopHost.Layout(window, 760, 650);
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var send = (Button)window.FindName("SendButton");
            await ObserveAsync(() => send.IsEnabled);
            var workers = (ComboBox)window.FindName("Workers");
            Assert.Equal(worker, workers.SelectedValue);
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await ObserveAsync(() => model.Source.Length == 0);
            var id = Assert.Single(folder.RequestIds());
            var request = Assert.IsType<RemoteRequest>(await folder.ReadRequestAsync(id));
            Assert.Equal(worker, request.TargetWorkerId);
            Assert.Equal("Morning request", request.Title);
            Assert.Empty(model.Jobs);
            var jobs = (DataGrid)window.FindName("JobList");
            await folder.WriteStatusAsync(new(id, worker, DateTimeOffset.UtcNow, RemoteJobState.Ready,
                RemoteVoice.From(request.Options), new("piper", "en_US-lessac-medium", 1, Preset: "Auditioned fallback"),
                OutputFile: id + ".mp3", FallbackReason: "Requested voice unavailable."));
            var details = (TextBox)window.FindName("JobDetails");
            await ObserveAsync(() => jobs.Items.Count == 1 && details.Text.Contains("Requested voice unavailable.", StringComparison.Ordinal));
            Assert.Contains("Used: piper · en_US-lessac-medium · Auditioned fallback", details.Text);
            Assert.True(((Button)window.FindName("PlayButton")).IsEnabled);
            Assert.True(((Button)window.FindName("DiagnosticsButton")).IsEnabled);
        }
        finally { window.Close(); }
    });

    private static async Task ObserveAsync(Func<bool> observed)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!observed()) await Task.Delay(30, timeout.Token);
    }
}
