using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public partial class ProviderContractTests
{
    [Theory] [InlineData("kokoro", false)] [InlineData("piper", false)] [InlineData("kokoro", true)] [InlineData("piper", true)]
    public async Task DaemonTimeoutsEnterStartupAndContinueBeyondTenPolls(string engine, bool repair)
    {
        using var fixture = new Fixture(engine); var probes = 0;
        fixture.Runtime.BeforeDocker = (args, _) => args[0] == "version" && ++probes <= 14
            ? throw new TimeoutException("docker exceeded its 15-second limit.") : Task.CompletedTask;
        using var provider = fixture.Provider();
        var info = repair ? (await provider.RepairAsync(engine)).Provider : await provider.ReadyAsync(engine, default);
        Assert.Equal("ready", info.State); Assert.Equal(1, fixture.Runtime.Launches);
        Assert.True(probes >= 15); Assert.Equal(0, fixture.Runtime.ForcedRestarts);
        Assert.DoesNotContain(fixture.Runtime.Calls, c => c[0] == "desktop");
    }

    [Fact] public async Task ReadOnlyDaemonTimeoutExplainsRestartWithoutMutating()
    {
        using var fixture = new Fixture();
        fixture.Runtime.BeforeDocker = (args, _) => args[0] == "version" ? throw new TimeoutException("private timeout") : Task.CompletedTask;
        using var provider = fixture.Provider(); var error = await Assert.ThrowsAsync<IOException>(() => provider.ProbeAsync("kokoro"));
        Assert.Contains("Restart Docker Desktop / repair", error.Message); Assert.DoesNotContain("private timeout", error.Message);
        Assert.Equal(0, fixture.Runtime.Launches); Assert.Equal(0, fixture.Runtime.ForcedRestarts); Assert.Empty(fixture.Http.Uris);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RepeatedDaemonTimeoutsRespectRepairDeadlineAndCallerCancellation(bool cancelCaller)
    {
        using var fixture = new Fixture(); var polls = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var probes = 0;
        fixture.Runtime.BeforeDocker = (args, _) =>
        {
            if (args[0] == "version") { if (++probes > 1) polls.TrySetResult(); throw new TimeoutException("private timeout"); }
            return Task.CompletedTask;
        };
        using var provider = fixture.Provider(Fixture.ShortLimits with { Readiness = TimeSpan.FromSeconds(10) });
        using var cancel = new CancellationTokenSource(); var before = File.ReadAllText(PinPath(fixture));
        var pending = provider.RepairAsync("kokoro", cancel.Token); await polls.Task.WaitAsync(TimeSpan.FromSeconds(8));
        if (cancelCaller) { cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); }
        else { var error = await Assert.ThrowsAsync<TimeoutException>(() => pending); Assert.Contains("local Linux engine is not responding", error.Message); }
        Assert.Equal(1, fixture.Runtime.Launches); Assert.Equal(0, fixture.Runtime.ForcedRestarts);
        Assert.Equal(before, File.ReadAllText(PinPath(fixture))); Assert.Empty(fixture.Http.Uris);
    }

    [Theory] [InlineData("success")] [InlineData("failure")] [InlineData("timeout")]
    public async Task ExplicitDesktopRestartUsesBoundedCommandAndRechecksBeforeFallback(string outcome)
    {
        using var fixture = new Fixture(); InstallPeer(fixture); var before = File.ReadAllText(PinPath(fixture));
        fixture.Runtime.DesktopRestartFails = outcome == "failure";
        fixture.Runtime.BeforeDocker = (args, _) => args[0] == "desktop" && outcome == "timeout" ? throw new TimeoutException("private error") : Task.CompletedTask;
        using var provider = fixture.Provider(); await provider.RestartDockerDesktopAsync();
        if (outcome != "timeout") Assert.Equal(new[] { "desktop", "restart", "--timeout", "30" }, Assert.Single(fixture.Runtime.Calls, c => c[0] == "desktop"));
        Assert.Equal(outcome == "success" ? 0 : 1, fixture.Runtime.ForcedRestarts);
        Assert.Equal(outcome == "success" ? 2 : 4, fixture.Http.Gets);
        Assert.Equal(before, File.ReadAllText(PinPath(fixture))); Assert.Equal(0, fixture.Http.Posts);
    }

    [Theory] [InlineData("busy")] [InlineData("loading")] [InlineData("pending")] [InlineData("context")] [InlineData("unreachable")]
    public async Task DesktopRestartRefusesActiveOrUnknownSpeechAndForeignContext(string defect)
    {
        using var fixture = new Fixture(); InstallPeer(fixture);
        if (defect == "busy") fixture.Http.PeerHealth = fixture.Http.PeerHealth!.Replace("\"active\":0", "\"active\":1");
        if (defect == "loading") fixture.Http.Health = _ => fixture.Health("loading", 0);
        if (defect == "pending") WriteAdmission(fixture);
        if (defect == "context") fixture.Runtime.Context = "ssh://foreign";
        if (defect == "unreachable") fixture.Http.BeforeHealth = _ => throw new HttpRequestException();
        using var provider = fixture.Provider(); await Assert.ThrowsAnyAsync<IOException>(() => provider.RestartDockerDesktopAsync());
        Assert.DoesNotContain(fixture.Runtime.Calls, c => c[0] == "desktop"); Assert.Equal(0, fixture.Runtime.ForcedRestarts);
        if (defect == "pending") Assert.True(File.Exists(AdmissionPath(fixture)));
    }

    [Fact] public async Task DesktopStartupFailureCanBeRepairedWhenEndpointsAndDaemonAreUnavailableWithoutReservation()
    {
        using var fixture = new Fixture(); fixture.Runtime.DaemonFailures = int.MaxValue; fixture.Runtime.DesktopRestartFails = true;
        fixture.Http.BeforeHealth = _ => throw new HttpRequestException();
        using var provider = fixture.Provider(); await provider.RestartDockerDesktopAsync();
        Assert.Equal(1, fixture.Runtime.ForcedRestarts); Assert.Equal(0, fixture.Http.Posts);
    }

    [Fact] public async Task SpeechBecomingBusyDuringDesktopRestartPreventsProcessFallback()
    {
        using var fixture = new Fixture(); InstallPeer(fixture); fixture.Runtime.DesktopRestartFails = true;
        fixture.Http.BeforeHealth = _ => { if (fixture.Http.Gets >= 3) fixture.Http.Health = _ => fixture.Health("ready", 1); return Task.CompletedTask; };
        using var provider = fixture.Provider(); await Assert.ThrowsAsync<IOException>(() => provider.RestartDockerDesktopAsync());
        Assert.Equal(0, fixture.Runtime.ForcedRestarts); Assert.Equal(0, fixture.Http.Posts);
    }

    [Fact] public async Task CallerCancellationDuringDesktopCommandNeverForcesProcessRestart()
    {
        using var fixture = new Fixture(); InstallPeer(fixture); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runtime.BeforeDocker = async (args, ct) => { if (args[0] == "desktop") { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } };
        using var provider = fixture.Provider(); using var cancel = new CancellationTokenSource();
        var pending = provider.RestartDockerDesktopAsync(cancel.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.Equal(0, fixture.Runtime.ForcedRestarts);
    }

    [Fact] public async Task DesktopRestartHasItsOwnBoundedAllowance()
    {
        using var fixture = new Fixture(); InstallPeer(fixture);
        fixture.Runtime.BeforeDocker = async (args, ct) => { if (args[0] == "desktop") await Task.Delay(Timeout.Infinite, ct); };
        using var provider = fixture.Provider(Fixture.ShortLimits with { DesktopRecovery = TimeSpan.FromMilliseconds(300) });
        var error = await Assert.ThrowsAsync<TimeoutException>(() => provider.RestartDockerDesktopAsync());
        Assert.Contains("local Linux engine is not responding", error.Message); Assert.Equal(0, fixture.Runtime.ForcedRestarts);
    }
}

public class DockerDesktopRecoveryTests
{
    [Theory] [InlineData(0)] [InlineData(181)]
    public void DesktopRecoveryAllowanceRemainsBounded(int seconds) => Assert.Throws<ArgumentException>(() => (new SpeechProviderLimits { DesktopRecovery = TimeSpan.FromSeconds(seconds) }).Validate());

    private static void SocketFixture(string root)
    {
        foreach (var (relative, endpoint) in new[] { (Path.Combine("Docker", "run"), "dockerInference"), ("docker-secrets-engine", "engine.sock") })
        {
            var directory = Path.Combine(root, relative); Directory.CreateDirectory(directory); File.WriteAllBytes(Path.Combine(directory, endpoint), []);
        }
    }

    [Fact] public void InaccessibleEndpointFoldersArePreservedWithoutTouchingModelData()
    {
        using var fixture = new TestWorkspace(); var root = fixture.Workspace.Root; SocketFixture(root);
        var models = Path.Combine(root, "Docker", "data"); Directory.CreateDirectory(models); File.WriteAllText(Path.Combine(models, "models"), "unchanged model data");
        var preserved = DockerDesktopRecovery.QuarantineInaccessibleRuntime(root, _ => true, _ => FileAttributes.ReparsePoint);
        Assert.Equal(2, preserved.Length); Assert.All(preserved, path => Assert.True(Directory.Exists(path)));
        Assert.False(Directory.Exists(Path.Combine(root, "Docker", "run"))); Assert.False(Directory.Exists(Path.Combine(root, "docker-secrets-engine")));
        Assert.Equal(0, new FileInfo(Path.Combine(preserved[0], "dockerInference")).Length);
        Assert.Equal(0, new FileInfo(Path.Combine(preserved[1], "engine.sock")).Length);
        Assert.Equal("unchanged model data", File.ReadAllText(Path.Combine(models, "models")));
    }

    [Theory] [InlineData("accessible")] [InlineData("permission")] [InlineData("unknown-data")] [InlineData("unknown-empty-file")] [InlineData("directory")]
    public void AccessibleSocketsPermissionFailuresAndUnexpectedDataArePreserved(string condition)
    {
        using var fixture = new TestWorkspace(); var root = fixture.Workspace.Root; SocketFixture(root);
        var secrets = Path.Combine(root, "docker-secrets-engine");
        if (condition == "unknown-data") File.WriteAllText(Path.Combine(secrets, "engine.sock.stale"), "preserved");
        if (condition == "unknown-empty-file") File.WriteAllBytes(Path.Combine(secrets, "credentials"), []);
        if (condition == "directory") Directory.CreateDirectory(Path.Combine(secrets, "unknown"));
        if (condition == "accessible") Assert.Empty(DockerDesktopRecovery.QuarantineInaccessibleRuntime(root, _ => false, _ => FileAttributes.ReparsePoint));
        else if (condition == "permission") Assert.Throws<UnauthorizedAccessException>(() => DockerDesktopRecovery.QuarantineInaccessibleRuntime(root, _ => throw new UnauthorizedAccessException(), _ => FileAttributes.ReparsePoint));
        else Assert.Throws<IOException>(() => DockerDesktopRecovery.QuarantineInaccessibleRuntime(root, _ => true, _ => FileAttributes.ReparsePoint));
        Assert.True(Directory.Exists(Path.Combine(root, "Docker", "run"))); Assert.True(Directory.Exists(secrets));
        Assert.Empty(Directory.GetDirectories(root, "*.commutecast-recovery-*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("resources/com.docker.backend.exe", true)] [InlineData("frontend/Docker Desktop.exe", true)] [InlineData("Docker Desktop.exe", true)]
    [InlineData("../other/com.docker.backend.exe", false)] [InlineData("resources/other.exe", false)] [InlineData("", false)]
    public void ProcessSelectionRequiresExactInstalledExecutable(string relative, bool expected)
    {
        var installation = Path.Combine(Path.GetTempPath(), "docker-install-fixture");
        Assert.Equal(expected, DockerDesktopLocation.IsManagedProcess(Path.Combine(installation, "Docker Desktop.exe"), relative == "" ? "" : Path.Combine(installation, relative)));
    }

    [Fact] public void MissingAndOrdinaryRuntimeFilesArePreserved()
    {
        using var fixture = new TestWorkspace(); var root = fixture.Workspace.Root;
        Assert.Empty(DockerDesktopRecovery.QuarantineInaccessibleRuntime(root, _ => throw new InvalidOperationException()));
        var runtime = Path.Combine(root, "Docker", "run"); Directory.CreateDirectory(runtime);
        File.WriteAllText(Path.Combine(runtime, "dockerInference"), "preserved regular data");
        Assert.Empty(DockerDesktopRecovery.QuarantineInaccessibleRuntime(root, _ => true));
        Assert.Equal("preserved regular data", File.ReadAllText(Path.Combine(runtime, "dockerInference")));
    }
}
