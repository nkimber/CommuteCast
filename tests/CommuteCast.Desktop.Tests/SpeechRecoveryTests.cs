using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommuteCast.Core;
using CommuteCast.Desktop;
using CommuteCast.Infrastructure;

namespace CommuteCast.Desktop.Tests;

[Collection("Desktop")]
public class SpeechRecoveryTests
{
    [Fact] public Task RepairSelectedPreservesOldJobWhenInstalledImageChanged() => DesktopHost.Run(async () =>
    {
        var workspace = DesktopHost.Workspace(); var oldImage = "sha256:" + new string('b', 64);
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "provider-lock.local.json"), JsonSerializer.Serialize(new { ImageId = oldImage, Contract = 1 }));
        var runtime = new Runtime();
        using var provider = new LocalSpeechProvider(workspace, runtime, new HealthHandler()); runtime.Provider = provider;
        var saved = new AppSettings { QueuePaused = true };
        var model = new MainViewModel(saved, workspace, provider, runtime, playbackOutput: new PlaybackTests.Output(), powerEvents: new LifecycleTests.Events(), notifications: new LifecycleTests.Notifications());
        try
        {
            var job = new Job { Title = "My older narration", Stage = JobStage.Failed, Source = "Preserved source", Settings = new("kokoro", "af_heart", 1, false, "", "kokoro:contract-v1:" + new string('c', 64), ProviderImageId: oldImage) };
            model.RefreshJobs([job]); model.SelectedJob = Assert.Single(model.Jobs);
            await DesktopHost.Execute(model.RepairSelectedCommand);
            Assert.Contains("model or image changed", model.SelectedSpeechResult); Assert.Contains("Use as a new draft", model.AttentionMessage);
            Assert.Equal(JobStage.Failed, job.Stage); Assert.Equal(oldImage, job.Settings.ProviderImageId); Assert.Equal("Preserved source", job.Source);
            Assert.True(saved.QueuePaused); Assert.Equal(0, runtime.Mutations);
        }
        finally { await model.DisposeAsync(); }
    });

    [Theory] [InlineData(false)] [InlineData(true)]
    public Task RepairCommandExplainsRefusalOrRecoversPinAndClearsWarning(bool foreign) => DesktopHost.Run(async () =>
    {
        var workspace = DesktopHost.Workspace();
        var pinPath = Path.Combine(workspace.Root, "provider-lock.local.json");
        var original = JsonSerializer.Serialize(new { ImageId = "sha256:" + new string('b', 64), Contract = 1 });
        await File.WriteAllTextAsync(pinPath, original);
        var runtime = new Runtime { Foreign = foreign };
        using var provider = new LocalSpeechProvider(workspace, runtime, new HealthHandler(), new() { Readiness = TimeSpan.FromSeconds(5), Poll = TimeSpan.FromMilliseconds(5) });
        runtime.Provider = provider;
        var saved = new AppSettings { QueuePaused = true };
        var model = new MainViewModel(saved, workspace, provider, runtime, playbackOutput: new PlaybackTests.Output(), powerEvents: new LifecycleTests.Events(), notifications: new LifecycleTests.Notifications());
        try
        {
            var job = new Job { Title = "My completed narration", Stage = JobStage.Exported, ExportCommitted = true };
            model.RefreshJobs([job]);
            await DesktopHost.Execute(model.ReadinessCommand);
            Assert.True(model.HasAttention);
            await DesktopHost.Execute(model.RepairSpeechCommand);
            Assert.True(saved.QueuePaused); Assert.Equal(job.Id, Assert.Single(model.Jobs).Id);
            if (foreign)
            {
                Assert.Equal("Speech repair needs attention", model.AttentionHeading);
                Assert.Contains("not identified as owned by CommuteCast", model.AttentionMessage);
                Assert.Contains("PowerShell", model.AttentionMessage); Assert.Contains("Provision-Speech.ps1", model.SpeechRepairDetails);
                Assert.Equal(original, await File.ReadAllTextAsync(pinPath));
                var window = new MainWindow(model);
                foreach (var width in new[] { 1060, 1380 })
                {
                    var height = width == 1060 ? 700 : 900;
                    DesktopHost.Layout(window, width, height);
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                    Assert.Equal(model.AttentionMessage, ((TextBlock)window.FindName("AttentionMessageText")).Text);
                    var root = (FrameworkElement)window.Content;
                    if (root is Panel panel) panel.Background = (Brush)Application.Current.Resources["Canvas"];
                    root.Measure(new(width, height)); root.Arrange(new(0, 0, width, height)); root.UpdateLayout();
                    Assert.True(((TextBlock)window.FindName("StatusText")).ActualHeight <= 32);
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(workspace.Root, "speech-repair-" + width + ".png")); encoder.Save(file);
                    var libraryHeight = ((ListBox)window.FindName("NarrationList")).ActualHeight;
                    Assert.True(libraryHeight > 0, "Library height after long repair guidance: " + libraryHeight);
                }
                Console.WriteLine("Speech repair layout evidence: " + workspace.Root);
            }
            else
            {
                Assert.False(model.HasAttention); Assert.Contains("ready on this laptop", model.ServiceStatus);
                Assert.Contains("Recovered the local image pin", model.SpeechRepairDetails);
                Assert.Contains("piper: ready", model.SpeechRepairDetails);
                using var pin = JsonDocument.Parse(await File.ReadAllTextAsync(pinPath));
                Assert.Equal(Runtime.Image, pin.RootElement.GetProperty("ImageId").GetString());
            }
            Assert.Equal(0, runtime.Mutations);
        }
        finally { await model.DisposeAsync(); }
    });

    private sealed class Runtime : ILocalSpeechRuntime, ISetupRuntime
    {
        public const string Image = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        public bool Foreign { get; init; } public int Mutations { get; private set; } public LocalSpeechProvider? Provider { get; set; }
        public Task<ProcessResult> DockerAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (arguments[0] == "context") return Task.FromResult(new ProcessResult(0, "npipe:////./pipe/dockerDesktopLinuxEngine", ""));
            if (arguments[0] == "version") return Task.FromResult(new ProcessResult(0, "29.2.1", ""));
            if (arguments[0] != "inspect") { Mutations++; throw new InvalidOperationException("Unexpected container mutation."); }
            var engine = arguments[1].EndsWith("kokoro", StringComparison.Ordinal) ? "kokoro" : "piper";
            return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(new[] { new {
                Id = new string(engine == "kokoro" ? 'e' : 'f', 64), Name = "/commutecast-" + engine, Image,
                Config = new { Labels = new Dictionary<string, string> { ["com.commutecast.owner"] = Foreign ? "other" : "CommuteCast", ["com.docker.compose.project"] = "commutecast", ["com.commutecast.contract"] = "1" }, Cmd = new[] { "uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8765", "--no-access-log" }, Entrypoint = (string[]?)null, Env = new[] { "COMMUTECAST_ENGINE=" + engine } },
                HostConfig = new { PortBindings = new Dictionary<string, object> { ["8765/tcp"] = new[] { new { HostIp = "127.0.0.1", HostPort = engine == "kokoro" ? "8765" : "8766" } } }, Privileged = false, NetworkMode = "bridge", NanoCpus = 2_000_000_000L, Memory = (engine == "kokoro" ? 2L : 1L) * 1024 * 1024 * 1024, SecurityOpt = new[] { "no-new-privileges:true" } },
                Mounts = Array.Empty<object>(), State = new { Running = true, OOMKilled = false, Paused = false, Restarting = false }
            } }), ""));
        }
        public void LaunchInstalledDesktop() { Mutations++; throw new InvalidOperationException("Unexpected desktop launch."); }
        public SetupHost InspectHost() => new(true, "X64", new(10, 0, 26100), "10.0.12", 8, 16UL * 1024 * 1024 * 1024, "4.62.0", true);
        public Task<ProcessResult> RunAsync(string tool, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) => tool == "docker" && args[0] is "context" or "version" ? DockerAsync(args, timeout, ct)
            : Task.FromResult(new ProcessResult(0, tool switch { "ffmpeg" when args.Contains("-protocols") => "Output:\n fd", "ffmpeg" or "ffprobe" => tool + " version 8.0.1", "wsl.exe" => "WSL version: 2.6.0.0", _ => "Docker version 29.2.1" }, ""));
        public Task<ProviderInfo> ProbeSpeechAsync(string engine, CancellationToken ct) => Provider!.ProbeAsync(engine, ct);
    }
    private sealed class HealthHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            var engine = request.RequestUri!.Port == 8765 ? "kokoro" : "piper";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { service = "CommuteCast", contract = 1, engine,
                fingerprint = engine + ":contract-v1:" + new string('c', 64), voices = new[] { engine == "kokoro" ? "af_heart" : "en_US-lessac-medium" },
                state = "ready", active = 0, admission = 1, instance = new string(engine == "kokoro" ? 'd' : 'b', 32), sequence = 0 }), System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
