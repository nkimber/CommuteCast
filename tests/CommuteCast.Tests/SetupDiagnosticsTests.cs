using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.ComponentModel;
using System.Text.Json;

namespace CommuteCast.Tests;

public class SetupDiagnosticsTests
{
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void DesktopLocationSupportsUserAndSystemInstallationsWithoutLaunching(bool user, bool system)
    {
        using var test = new TestWorkspace(); var local = Path.Combine(test.Parent, "user"); var programs = Path.Combine(test.Parent, "system");
        var userExe = Path.Combine(local, "Programs", "DockerDesktop", "Docker Desktop.exe"); var systemExe = Path.Combine(programs, "Docker", "Docker", "Docker Desktop.exe");
        foreach (var path in (user ? new[] { userExe } : Array.Empty<string>()).Concat(system ? new[] { systemExe } : [])) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "Synthetic executable-location fixture; never launched"); }
        Assert.Equal(user ? userExe : system ? systemExe : null, DockerDesktopLocation.Find(local, programs));
    }
    [Fact] public async Task AvailableToolsAndSelectedSpeechRemainSeparateFromCorporateApproval()
    {
        var runtime = new Runtime(); var report = await new SetupDiagnostics(runtime).CheckAsync(new());
        Assert.True(report.SelectedSpeechAvailable); Assert.Equal(SetupStatus.ReviewRequired, Find(report, "policy").Status);
        Assert.Contains("2026-03-01-git-approved", Find(report, "ffmpeg").Detail);
        Assert.Equal(new[] { "kokoro", "piper" }, runtime.SpeechCalls);
        Assert.All(runtime.Commands, call => Assert.True(call is "ffmpeg -version" or "ffprobe -version" or "wsl.exe --version" or "docker --version" or "docker context inspect --format {{.Endpoints.docker.Host}}" or "docker version --format {{.Server.Version}}", call));
        Assert.DoesNotContain("Private echo", JsonSerializer.Serialize(report));
    }
    [Fact] public async Task MissingCliDoesNotInspectContextDaemonOrSpeech()
    {
        var runtime = new Runtime { Execute = (tool, _) => tool == "docker" ? throw new Win32Exception(2) : null };
        var report = await new SetupDiagnostics(runtime).CheckAsync(new()); Assert.Equal(SetupStatus.Missing, Find(report, "docker-cli").Status);
        Assert.False(report.SelectedSpeechAvailable); Assert.Empty(runtime.SpeechCalls); Assert.DoesNotContain(runtime.Commands, c => c.Contains("context") || c.Contains("Server.Version"));
    }
    [Fact] public async Task ForeignContextIsRedactedAndNeverReachesTheDaemonOrSpeech()
    {
        var runtime = new Runtime { Execute = (_, args) => args[0] == "context" ? new(0, "tcp://Private echo-corporate-host:2375", "") : null };
        var report = await new SetupDiagnostics(runtime).CheckAsync(new()); Assert.Equal(SetupStatus.NeedsAttention, Find(report, "context").Status);
        Assert.Empty(runtime.SpeechCalls); Assert.DoesNotContain(runtime.Commands, c => c.Contains("Server.Version")); Assert.DoesNotContain("Private echo", JsonSerializer.Serialize(report));
    }
    [Theory] [InlineData(false)] [InlineData(true)] public async Task EngineTimeoutExplainsObservedDesktopStateAndPreservesOtherChecks(bool running)
    {
        var runtime = new Runtime { Host = Runtime.GoodHost with { DesktopRunning = running }, Execute = (_, args) => args[0] == "version" ? throw new TimeoutException("Private echo") : null };
        var report = await new SetupDiagnostics(runtime).CheckAsync(new()); Assert.Equal(SetupStatus.TimedOut, Find(report, "daemon").Status);
        Assert.Contains(running ? "present" : "Open", Find(report, "daemon").NextStep); Assert.Equal(SetupStatus.Available, Find(report, "ffprobe").Status);
        Assert.Empty(runtime.SpeechCalls); Assert.DoesNotContain("Private echo", JsonSerializer.Serialize(report));
    }
    [Theory] [InlineData("WSL version: 1.0.0", SetupStatus.NeedsAttention)] [InlineData("WSL version: 2.1.5", SetupStatus.Available)] [InlineData("Localized WSL label: 2.6.0.0", SetupStatus.Available)] [InlineData("Private echo with no version", SetupStatus.NeedsAttention)]
    public async Task WslVersionIsBoundedAndUnparsedOutputIsNotAssumedSupported(string output, SetupStatus expected)
    {
        var runtime = new Runtime { Execute = (tool, _) => tool == "wsl.exe" ? new(0, output, "") : null };
        var report = await new SetupDiagnostics(runtime).CheckAsync(new()); Assert.Equal(expected, Find(report, "wsl").Status); Assert.DoesNotContain("Private echo", JsonSerializer.Serialize(report));
    }
    [Theory] [InlineData("Speech services are not provisioned.", SetupStatus.Missing, "provisioning")]
    [InlineData("The configured CommuteCast container is missing.", SetupStatus.Missing, "missing")]
    [InlineData("The speech container stopped; retry to resume.", SetupStatus.Unavailable, "stopped")]
    [InlineData("The speech container ran out of memory.", SetupStatus.NeedsAttention, "memory")]
    [InlineData("Private echo C:\\Corporate\\secret.txt", SetupStatus.NeedsAttention, "could not be verified")]
    public async Task SpeechFailuresAreClassifiedWithoutEchoingProviderContent(string message, SetupStatus expected, string detail)
    {
        var runtime = new Runtime { SpeechFailure = new IOException(message) }; var report = await new SetupDiagnostics(runtime).CheckAsync(new());
        Assert.Equal(expected, Find(report, "speech-selected").Status); Assert.Contains(detail, Find(report, "speech-selected").Detail); Assert.DoesNotContain("Private echo", JsonSerializer.Serialize(report));
    }
    [Theory] [InlineData("loading", 0)] [InlineData("ready", 1)] [InlineData("failed", 0)]
    public async Task BusyLoadingOrFailedServicesAreNotReportedAsAvailable(string state, int active)
    {
        var runtime = new Runtime { State = state, Active = active }; var report = await new SetupDiagnostics(runtime).CheckAsync(new()); Assert.False(report.SelectedSpeechAvailable); Assert.Equal(SetupStatus.NeedsAttention, Find(report, "speech-selected").Status);
    }
    [Fact] public async Task MissingSelectedVoiceAndUnexpectedEncoderIdentityNeedAttention()
    {
        var runtime = new Runtime { Voice = "different_voice", Execute = (tool, _) => tool == "ffmpeg" ? new(0, "Unrelated program 2.0 Private echo", "") : null };
        var report = await new SetupDiagnostics(runtime).CheckAsync(new()); Assert.False(report.SelectedSpeechAvailable); Assert.Contains("Selected voice", Find(report, "speech-selected").Detail); Assert.Equal(SetupStatus.NeedsAttention, Find(report, "ffmpeg").Status); Assert.DoesNotContain("Private echo", JsonSerializer.Serialize(report));
    }
    [Fact] public async Task CallerCancellationBoundsANoncooperativeCommand()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new Runtime { Pending = _ => { entered.TrySetResult(); return release.Task; } }; using var cancel = new CancellationTokenSource();
        var pending = new SetupDiagnostics(runtime).CheckAsync(new(), cancel.Token); await entered.Task; cancel.Cancel();
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(1))); }
        finally { release.SetResult(new(0, "late", "")); }
        Assert.Empty(runtime.SpeechCalls);
    }
    [Fact] public async Task UnsupportedArchitectureAndInsufficientMemoryAreVisible()
    {
        var runtime = new Runtime { Host = Runtime.GoodHost with { Architecture = "Arm64", MemoryBytes = 4UL * 1024 * 1024 * 1024 } };
        var report = await new SetupDiagnostics(runtime).CheckAsync(new()); Assert.Equal(SetupStatus.NeedsAttention, Find(report, "platform").Status); Assert.Equal(SetupStatus.NeedsAttention, Find(report, "resources").Status);
    }
    private static SetupCheck Find(SetupReport report, string id) => Assert.Single(report.Checks, c => c.Id == id);
    private sealed class Runtime : ISetupRuntime
    {
        public static SetupHost GoodHost => new(true, "X64", new(10, 0, 26100), "10.0.12", 8, 16UL * 1024 * 1024 * 1024, "4.62.0", true);
        public SetupHost Host { get; init; } = GoodHost;
        public List<string> Commands { get; } = []; public List<string> SpeechCalls { get; } = [];
        public Func<string, IReadOnlyList<string>, ProcessResult?>? Execute { get; init; }
        public Func<CancellationToken, Task<ProcessResult>>? Pending { get; init; }
        public Exception? SpeechFailure { get; init; } public string State { get; init; } = "ready"; public int Active { get; init; } public string Voice { get; init; } = "af_heart";
        public SetupHost InspectHost() => Host;
        public Task<ProcessResult> RunAsync(string tool, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
        {
            Commands.Add(tool + " " + string.Join(" ", args)); if (Pending is not null) return Pending(ct);
            var result = Execute?.Invoke(tool, args) ?? new(0, tool switch {
                "ffmpeg" or "ffprobe" => tool + " version 2026-03-01-git-approved Private echo",
                "wsl.exe" => "WSL version: 2.6.0.0\nKernel version: 6.6.0 Private echo",
                _ => args[0] == "context" ? "npipe:////./pipe/dockerDesktopLinuxEngine" : "Docker version 29.2.1 Private echo" }, "Private echo");
            return Task.FromResult(result);
        }
        public Task<ProviderInfo> ProbeSpeechAsync(string engine, CancellationToken ct)
        { SpeechCalls.Add(engine); if (SpeechFailure is not null) throw SpeechFailure; return Task.FromResult(new ProviderInfo(engine, engine + ":contract-v1:" + new string('a', 64), [Voice], State, Active)); }
    }
}
