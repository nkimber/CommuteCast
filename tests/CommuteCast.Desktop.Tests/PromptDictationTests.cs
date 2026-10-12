using System.Windows.Controls;
using System.Windows.Threading;
using System.IO;
using CommuteCast.Desktop;
using CommuteCast.Infrastructure;

namespace CommuteCast.Desktop.Tests;

public sealed class DictationAcceptanceFactAttribute : FactAttribute
{
    public DictationAcceptanceFactAttribute(string variable)
    {
        if (Environment.GetEnvironmentVariable(variable) != "1") Skip = "Opt-in local Docker/microphone acceptance: set " + variable + "=1.";
    }
}

[Collection("Desktop")]
public class PromptDictationTests
{
    [DictationAcceptanceFact("COMMUTECAST_TEST_MICROPHONE")]
    public Task PhysicalMicrophoneCaptureWhenRequested() => DesktopHost.Run(async () =>
    {
        using var service = new DictationService();
        await service.ReadyAsync(CancellationToken.None);
        using var microphone = new MicrophoneRecording();
        microphone.Start();
        for (var attempt = 0; attempt < 50 && microphone.CapturedBytes < 16000; attempt++) await Task.Delay(100);
        Assert.True(microphone.CapturedBytes >= 16000, "The default Windows microphone supplied no PCM samples within five seconds.");
        var wav = await microphone.StopAsync();
        Assert.True(wav.Length > 1000); Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        _ = await service.TranscribeAsync(wav, CancellationToken.None);
    });
    [DictationAcceptanceFact("COMMUTECAST_TEST_DICTATION")]
    public Task LocalWhisperNativeBindingAcceptanceWhenRequested() => DesktopHost.Run(async () =>
    {
        var sample = Path.GetFullPath(Environment.GetEnvironmentVariable("COMMUTECAST_DICTATION_SAMPLE")!);
        using (var readiness = new DictationService()) await readiness.ReadyAsync(CancellationToken.None);
        var stopped = await ProcessRunner.RunAsync("docker", ["stop", "commutecast-whisper"], TimeSpan.FromSeconds(15));
        Assert.Equal(0, stopped.ExitCode); // Record must recover the verified stopped container.
        var workspace = DesktopHost.Workspace();
        var model = Model(workspace, new(), new());
        model.DictationServiceForTests = new DictationService();
        model.MicrophoneFactory = () => new FileRecording(sample);
        try
        {
            var window = new MainWindow(model); model.NavigateCommand.Execute("prompt");
            model.PromptTopic = "Railways"; model.PromptGoal = "Existing listening goal.";
            await DesktopHost.Execute(model.BuildPromptCommand);
            await DesktopHost.Execute(model.RecordGoalCommand); Assert.True(model.CanStopGoal);
            await DesktopHost.Execute(model.StopGoalCommand);
            await Dispatcher.Yield(DispatcherPriority.Background); DesktopHost.Layout(window);
            Assert.Contains("railways", model.PromptGoal, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("public transport", model.PromptGoal, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("Existing listening goal.", model.PromptGoal);
            Assert.Equal(model.PromptGoal, ((TextBox)window.FindName("PromptGoalInput")).Text);
            Assert.True(model.PromptIsStale);
            RenderPreview(window, Path.Combine(Path.GetDirectoryName(sample)!, "prompt-dictation.png"));
        }
        finally { await model.DisposeAsync(); }
        Assert.Contains("railways", (await new DraftStore(workspace).LoadAsync()).PromptDraft!.Brief.Goal, StringComparison.OrdinalIgnoreCase);
    });

    private sealed class FileRecording(string path) : IMicrophoneRecording
    {
        public event Action? LimitReached { add { } remove { } }
        public event Action<Exception>? Failed { add { } remove { } }
        public void Start() { }
        public Task<byte[]> StopAsync() => File.ReadAllBytesAsync(path);
        public void Dispose() { }
    }

    private static void RenderPreview(MainWindow window, string path)
    {
        var content = (Panel)window.Content;
        // Rendering Content offscreen must include the background normally
        // painted by the window's native surface.
        content.Background = window.Background;
        content.Measure(new System.Windows.Size(1380, 900));
        content.Arrange(new System.Windows.Rect(0, 0, 1380, 900)); content.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1380, 900, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var image = File.Create(path); encoder.Save(image);
    }

    private static MainViewModel Model(Workspace workspace, Service service, Recording recording)
    {
        var model = new MainViewModel(new() { QueuePaused = true }, workspace,
            playbackOutput: new PlaybackTests.Output(), powerEvents: new LifecycleTests.Events(), notifications: new LifecycleTests.Notifications());
        model.DictationServiceForTests = service; model.MicrophoneFactory = () => recording;
        return model;
    }

    [Fact]
    public Task RecordingAppendsToCurrentGoalUpdatesNativeTextboxAndSavesDraft() => DesktopHost.Run(async () =>
    {
        var workspace = DesktopHost.Workspace(); var recording = new Recording();
        var model = Model(workspace, new() { Text = "Understand railways." }, recording);
        try
        {
            var window = new MainWindow(model); model.NavigateCommand.Execute("prompt");
            model.PromptTopic = "Cities"; model.PromptGoal = "Existing goal";
            await DesktopHost.Execute(model.BuildPromptCommand);
            await Dispatcher.Yield(DispatcherPriority.Background); DesktopHost.Layout(window);
            var start = (Button)window.FindName("RecordGoalButton");
            var stop = (Button)window.FindName("StopGoalButton");
            var cancel = (Button)window.FindName("CancelGoalButton");
            Assert.True(start.IsEnabled); Assert.False(stop.IsEnabled); Assert.False(cancel.IsEnabled);
            await DesktopHost.Execute(start.Command!);
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert.True(recording.Started); Assert.False(start.IsEnabled); Assert.True(stop.IsEnabled); Assert.True(cancel.IsEnabled);
            model.PromptGoal = "Edited during recording";
            await DesktopHost.Execute(stop.Command!);
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert.Equal("Edited during recording" + Environment.NewLine + "Understand railways.", model.PromptGoal);
            Assert.Equal(model.PromptGoal, ((TextBox)window.FindName("PromptGoalInput")).Text);
            Assert.True(model.PromptIsStale); Assert.True(recording.Disposed); Assert.True(start.IsEnabled);
            if (Environment.GetEnvironmentVariable("COMMUTECAST_DICTATION_PREVIEW") is { } preview) RenderPreview(window, preview);
        }
        finally { await model.DisposeAsync(); }
        var draft = await new DraftStore(workspace).LoadAsync();
        var reopened = Model(workspace, new(), new());
        try
        {
            reopened.RestorePromptDraft(draft.PromptDraft);
            Assert.Equal(draft.PromptDraft!.Brief.Goal, reopened.PromptGoal);
            Assert.Contains("Understand railways.", reopened.PromptGoal);
        }
        finally { await reopened.DisposeAsync(); }
    });

    [Theory]
    [InlineData("cancel")]
    [InlineData("navigate")]
    [InlineData("suspend")]
    [InlineData("close")]
    public Task RecordingIsReleasedOnLifecycleExit(string action) => DesktopHost.Run(async () =>
    {
        var recording = new Recording(); var service = new Service(); var model = Model(DesktopHost.Workspace(), service, recording);
        try
        {
            model.PromptGoal = "Keep this";
            await DesktopHost.Execute(model.RecordGoalCommand);
            if (action == "cancel") await DesktopHost.Execute(model.CancelGoalCommand);
            if (action == "navigate") await DesktopHost.Execute(model.NavigateCommand, "library");
            if (action == "suspend") await model.SuspendForPowerAsync();
            if (action == "close") await model.DisposeAsync();
            Assert.Equal("Keep this", model.PromptGoal); Assert.True(recording.Disposed);
            Assert.Equal(0, service.Transcriptions); Assert.True(model.CanRecordGoal);
        }
        finally { await model.DisposeAsync(); }
    });

    [Fact]
    public Task CancelDuringInferenceRejectsEvenLateResultAndAllowsNextRecording() => DesktopHost.Run(async () =>
    {
        var completion = new TaskCompletionSource<string>(); var service = new Service { Completion = completion };
        var model = Model(DesktopHost.Workspace(), service, new());
        try
        {
            model.PromptGoal = "Keep this"; await DesktopHost.Execute(model.RecordGoalCommand);
            model.StopGoalCommand.Execute(null); await Task.Delay(20);
            Assert.False(model.CanStopGoal); Assert.True(model.CanCancelGoal);
            await DesktopHost.Execute(model.CancelGoalCommand);
            completion.SetResult("Discard me"); await Task.Delay(40);
            Assert.Equal("Keep this", model.PromptGoal); Assert.True(model.CanRecordGoal);
            model.MicrophoneFactory = () => new Recording(); service.Completion = null; service.Text = "Next recording";
            await DesktopHost.Execute(model.RecordGoalCommand); await DesktopHost.Execute(model.StopGoalCommand);
            Assert.EndsWith("Next recording", model.PromptGoal);
        }
        finally { await model.DisposeAsync(); }
    });

    [Fact]
    public Task CancelDuringPreparationNeverStartsMicrophoneOnLateReadiness() => DesktopHost.Run(async () =>
    {
        var ready = new TaskCompletionSource(); var service = new Service { ReadyCompletion = ready };
        var recording = new Recording(); var model = Model(DesktopHost.Workspace(), service, recording);
        try
        {
            model.PromptGoal = "Keep this";
            model.RecordGoalCommand.Execute(null); await Task.Delay(20);
            Assert.True(model.CanCancelGoal); Assert.False(model.CanStopGoal);
            await DesktopHost.Execute(model.CancelGoalCommand);
            ready.SetResult(); await Task.Delay(40);
            Assert.False(recording.Started); Assert.True(model.CanRecordGoal); Assert.Equal("Keep this", model.PromptGoal);
            await DesktopHost.Execute(model.RepairDictationCommand);
            Assert.Contains("ready", model.DictationStatus); Assert.False(recording.Started);
        }
        finally { ready.TrySetResult(); await model.DisposeAsync(); }
    });

    [Fact]
    public Task OverflowPreservesCompleteTranscriptUntilGoalHasRoom() => DesktopHost.Run(async () =>
    {
        var model = Model(DesktopHost.Workspace(), new() { Text = "Complete recorded goal" }, new());
        try
        {
            model.PromptGoal = new string('x', 1990);
            await DesktopHost.Execute(model.RecordGoalCommand); await DesktopHost.Execute(model.StopGoalCommand);
            Assert.Equal(1990, model.PromptGoal.Length); Assert.Equal("Complete recorded goal", model.PendingGoalTranscript);
            model.PromptGoal = "Shortened"; await DesktopHost.Execute(model.AddGoalTranscriptCommand);
            Assert.EndsWith("Complete recorded goal", model.PromptGoal); Assert.False(model.HasPendingGoalTranscript);
        }
        finally { await model.DisposeAsync(); }
    });

    [Theory]
    [InlineData("service")]
    [InlineData("microphone")]
    [InlineData("inference")]
    [InlineData("silence")]
    public Task FailuresAndSilencePreserveGoalAndReleaseResources(string failure) => DesktopHost.Run(async () =>
    {
        var service = new Service { FailReadiness = failure == "service", FailInference = failure == "inference", Text = "" };
        var recording = new Recording { FailStart = failure == "microphone" };
        var model = Model(DesktopHost.Workspace(), service, recording);
        try
        {
            model.PromptGoal = "Keep this";
            await DesktopHost.Execute(model.RecordGoalCommand);
            if (model.CanStopGoal) await DesktopHost.Execute(model.StopGoalCommand);
            Assert.Equal("Keep this", model.PromptGoal); Assert.True(model.CanRecordGoal);
            if (failure != "service") Assert.True(recording.Disposed);
            Assert.DoesNotContain("Recording…", model.DictationStatus);
        }
        finally { await model.DisposeAsync(); }
    });

    [Fact]
    public Task LimitAutomaticallyTranscribesAndDisconnectedMicCancels() => DesktopHost.Run(async () =>
    {
        var recording = new Recording(); var model = Model(DesktopHost.Workspace(), new() { Text = "At the limit" }, recording);
        try
        {
            await DesktopHost.Execute(model.RecordGoalCommand); recording.ReachLimit();
            await Task.Delay(60); Assert.Equal("At the limit", model.PromptGoal); Assert.True(recording.Disposed);
            var disconnected = new Recording(); model.MicrophoneFactory = () => disconnected;
            await DesktopHost.Execute(model.RecordGoalCommand); disconnected.Disconnect();
            await Task.Delay(60); Assert.True(disconnected.Disposed); Assert.Equal("At the limit", model.PromptGoal);
        }
        finally { await model.DisposeAsync(); }
    });

    internal sealed class Service : IDictationService
    {
        public string Text = "";
        public bool FailReadiness, FailInference;
        public int Transcriptions;
        public TaskCompletionSource<string>? Completion;
        public TaskCompletionSource? ReadyCompletion;
        public Task ReadyAsync(CancellationToken ct) => FailReadiness ? Task.FromException(new IOException("Setup needed")) : ReadyCompletion?.Task ?? Task.CompletedTask;
        public Task<string> TranscribeAsync(byte[] wav, CancellationToken ct)
        {
            Transcriptions++;
            return FailInference ? Task.FromException<string>(new IOException("Inference failed")) : Completion?.Task ?? Task.FromResult(Text);
        }
        public void Dispose() { }
    }

    internal sealed class Recording : IMicrophoneRecording
    {
        public bool Started, Disposed, FailStart;
        public event Action? LimitReached;
        public event Action<Exception>? Failed;
        public void Start() { if (FailStart) throw new IOException("Microphone unavailable"); Started = true; }
        public Task<byte[]> StopAsync() => Task.FromResult(new byte[100]);
        public void ReachLimit() => LimitReached?.Invoke();
        public void Disconnect() => Failed?.Invoke(new IOException("Disconnected"));
        public void Dispose() => Disposed = true;
    }
}
