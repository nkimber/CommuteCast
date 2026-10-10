using CommuteCast.Infrastructure;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CommuteCast.Tests;

public partial class ProviderContractTests
{
    private static void InstallPeer(Fixture fixture)
    {
        var other = fixture.Engine == "kokoro" ? "piper" : "kokoro";
        fixture.Runtime.PeerContainer = new FakeRuntime(other).Container;
        fixture.Http.PeerHealth = JsonSerializer.Serialize(new { service = "CommuteCast", contract = 1, engine = other,
            fingerprint = other + ":contract-v1:" + new string('c', 64), voices = new[] { other == "kokoro" ? "af_heart" : "en_US-lessac-medium" },
            state = "ready", active = 0, admission = 1, instance = new string('b', 32), sequence = 0 });
    }
    private static string PinPath(Fixture fixture) => Path.Combine(fixture.Test.Workspace.Root, "provider-lock.local.json");
    private static void StalePin(Fixture fixture) => File.WriteAllText(PinPath(fixture), JsonSerializer.Serialize(new { ImageId = "sha256:" + new string('b', 64), Contract = 1 }));

    [Theory] [InlineData("kokoro", false)] [InlineData("piper", false)] [InlineData("kokoro", true)] [InlineData("piper", true)]
    public async Task ExplicitRepairRecoversStaleOrMissingPinFromBothIdleServices(string engine, bool missingPin)
    {
        using var fixture = new Fixture(engine); InstallPeer(fixture);
        if (missingPin) File.Delete(PinPath(fixture)); else StalePin(fixture);
        using var provider = fixture.Provider();
        var result = await provider.RepairAsync(engine);
        Assert.Equal(FakeRuntime.Image, result.Provider.ImageId); Assert.Contains("Recovered", result.Detail);
        using var pin = JsonDocument.Parse(File.ReadAllText(PinPath(fixture)));
        Assert.Equal(FakeRuntime.Image, pin.RootElement.GetProperty("ImageId").GetString());
        Assert.Equal(0, fixture.Runtime.Starts); Assert.Equal(0, fixture.Runtime.Creates); Assert.Equal(0, fixture.Http.Posts);
        Assert.Equal("ready", (await provider.ProbeAsync(engine)).State);
        var error = await Assert.ThrowsAsync<IOException>(() => provider.SynthesizeAsync(fixture.Settings with { ProviderImageId = "sha256:" + new string('b', 64) }, "older saved source", fixture.Output, default));
        Assert.Contains("image", error.Message); Assert.Equal(0, fixture.Http.Posts);
    }

    [Fact] public async Task OrdinaryReadinessExplainsStalePinWithoutChangingIt()
    {
        using var fixture = new Fixture(); InstallPeer(fixture); StalePin(fixture); var before = File.ReadAllText(PinPath(fixture));
        using var provider = fixture.Provider(); var error = await Assert.ThrowsAsync<SpeechSetupException>(() => provider.ReadyAsync("kokoro", default, true));
        Assert.True(error.ImageMismatch); Assert.Contains("installed speech image differs", error.Message);
        Assert.Contains("Start / repair speech services", error.Message); Assert.Contains("PowerShell", error.Message);
        Assert.Contains("Provision-Speech.ps1 -Build", error.Message); Assert.Equal(before, File.ReadAllText(PinPath(fixture)));
        Assert.Empty(fixture.Http.Uris); Assert.Equal(0, fixture.Runtime.Starts);
    }

    [Theory] [InlineData("owner")] [InlineData("project")] [InlineData("contract")] [InlineData("different-image")]
    [InlineData("stopped")] [InlineData("busy")] [InlineData("no-admission")] [InlineData("pending")]
    public async Task ExplicitRepairPreservesPinWhenPeerOrReservationIsUnsafe(string defect)
    {
        using var fixture = new Fixture(); InstallPeer(fixture); StalePin(fixture);
        var peer = fixture.Runtime.PeerContainer!;
        switch (defect)
        {
            case "owner": peer["Config"]!["Labels"]!["com.commutecast.owner"] = "private foreign owner"; break;
            case "project": peer["Config"]!["Labels"]!["com.docker.compose.project"] = "private foreign project"; break;
            case "contract": peer["Config"]!["Labels"]!["com.commutecast.contract"] = "2"; break;
            case "different-image": peer["Image"] = "sha256:" + new string('d', 64); break;
            case "stopped": peer["State"]!["Running"] = false; break;
            case "busy": fixture.Http.PeerHealth = fixture.Http.PeerHealth!.Replace("\"active\":0", "\"active\":1"); break;
            case "no-admission": fixture.Http.PeerHealth = fixture.Http.PeerHealth!.Replace("\"admission\":1,", ""); break;
            case "pending": WriteAdmission(fixture); break;
        }
        var before = File.ReadAllText(PinPath(fixture)); using var provider = fixture.Provider();
        var error = await Assert.ThrowsAnyAsync<IOException>(() => provider.RepairAsync("kokoro"));
        Assert.DoesNotContain("private foreign", error.Message); Assert.Equal(before, File.ReadAllText(PinPath(fixture)));
        Assert.Equal(0, fixture.Runtime.Starts); Assert.Equal(0, fixture.Runtime.Creates); Assert.Equal(0, fixture.Http.Posts);
        if (defect == "pending") Assert.True(File.Exists(AdmissionPath(fixture)));
    }

    [Theory] [InlineData("container")] [InlineData("process")] [InlineData("pin")] [InlineData("cancel")]
    public async Task RepairRejectsIdentitiesChangingDuringReconciliation(string change)
    {
        using var fixture = new Fixture(); InstallPeer(fixture); StalePin(fixture); var before = File.ReadAllText(PinPath(fixture));
        var inspected = 0; using var cancellation = new CancellationTokenSource();
        fixture.Runtime.BeforeDocker = (args, _) =>
        {
            if (args[0] == "inspect" && ++inspected == 3)
            {
                if (change == "container") fixture.Runtime.Container["Id"] = new string('9', 64);
                if (change == "process") fixture.Http.Instance = new string('9', 32);
                if (change == "pin") File.WriteAllText(PinPath(fixture), before + " ");
                if (change == "cancel") cancellation.Cancel();
            }
            return Task.CompletedTask;
        };
        using var provider = fixture.Provider();
        if (change == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.RepairAsync("kokoro", cancellation.Token));
        else await Assert.ThrowsAnyAsync<IOException>(() => provider.RepairAsync("kokoro"));
        Assert.Equal(before + (change == "pin" ? " " : ""), File.ReadAllText(PinPath(fixture)));
        Assert.Equal(0, fixture.Runtime.Starts); Assert.Equal(0, fixture.Runtime.Creates); Assert.Equal(0, fixture.Http.Posts);
    }

    [Theory] [InlineData("kokoro")] [InlineData("piper")]
    public async Task ExplicitRepairRecreatesOnlyMissingContainerFromExactPin(string engine)
    {
        using var fixture = new Fixture(engine); fixture.Runtime.Missing = true;
        using var provider = fixture.Provider(); var result = await provider.RepairAsync(engine);
        Assert.Equal("ready", result.Provider.State); Assert.Contains("Recreated", result.Detail);
        Assert.Equal(1, fixture.Runtime.Creates); Assert.Equal(1, fixture.Runtime.Starts); Assert.Equal(0, fixture.Http.Posts);
        var create = Assert.Single(fixture.Runtime.Calls, c => c[0] == "create");
        Assert.Contains(FakeRuntime.Image, create); Assert.Contains("127.0.0.1:" + (engine == "kokoro" ? "8765" : "8766") + ":8765", create);
        Assert.Contains("never", create); Assert.Contains("no-new-privileges:true", create);
        Assert.DoesNotContain(fixture.Runtime.Calls, c => c[0] is "rm" or "stop" or "restart" or "pull" or "build");
    }

    [Theory] [InlineData("exists")] [InlineData("image-missing")] [InlineData("pending")] [InlineData("remote")]
    public async Task MissingServiceRepairNeverReplacesOrDownloads(string defect)
    {
        using var fixture = new Fixture(); fixture.Runtime.Missing = true;
        switch (defect)
        {
            case "exists": fixture.Runtime.ListedContainers = "existing container"; break;
            case "image-missing": fixture.Runtime.CreateFails = true; break;
            case "pending": WriteAdmission(fixture); break;
            case "remote": fixture.Runtime.Context = "tcp://foreign:2375"; break;
        }
        var before = File.ReadAllText(PinPath(fixture)); using var provider = fixture.Provider();
        var error = await Assert.ThrowsAnyAsync<IOException>(() => provider.RepairAsync("kokoro"));
        Assert.DoesNotContain("private daemon error", error.Message); Assert.Equal(before, File.ReadAllText(PinPath(fixture)));
        Assert.Equal(defect == "image-missing" ? 1 : 0, fixture.Runtime.Creates); Assert.Equal(0, fixture.Runtime.Starts); Assert.Empty(fixture.Http.Uris);
    }

    [Theory] [InlineData("unreadable")] [InlineData("oversized")] [InlineData("contract")]
    public async Task RepairPreservesInvalidPinAndDoesNotInspectOrChangeServices(string defect)
    {
        using var fixture = new Fixture();
        var text = defect switch { "unreadable" => "private invalid pin", "oversized" => new string('x', 65537), _ => JsonSerializer.Serialize(new { ImageId = FakeRuntime.Image, Contract = 2 }) };
        File.WriteAllText(PinPath(fixture), text); using var provider = fixture.Provider();
        var error = await Assert.ThrowsAnyAsync<IOException>(() => provider.RepairAsync("kokoro"));
        Assert.DoesNotContain("private invalid pin", error.Message); Assert.Equal(text, File.ReadAllText(PinPath(fixture)));
        Assert.Empty(fixture.Runtime.Calls); Assert.Empty(fixture.Http.Uris);
    }

    [Fact] public async Task RepairLaunchesDesktopAtMostOnceIfDaemonIsLostAgain()
    {
        using var fixture = new Fixture(); fixture.Runtime.DaemonFailures = 1; var inspections = 0;
        fixture.Runtime.BeforeDocker = (args, _) => { if (args[0] == "inspect" && ++inspections == 1) fixture.Runtime.DaemonFailures = 1; return Task.CompletedTask; };
        using var provider = fixture.Provider(); await Assert.ThrowsAnyAsync<IOException>(() => provider.RepairAsync("kokoro"));
        Assert.Equal(1, fixture.Runtime.Launches); Assert.Equal(0, fixture.Runtime.Starts); Assert.Empty(fixture.Http.Uris);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RepairRespectsCancellationAndOverallDeadlineBeforeChangingConfiguration(bool callerCancellation)
    {
        using var fixture = new Fixture(); var before = File.ReadAllText(PinPath(fixture));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runtime.BeforeDocker = async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); };
        using var provider = fixture.Provider(Fixture.ShortLimits with { Readiness = TimeSpan.FromMilliseconds(500) }); using var cancellation = new CancellationTokenSource();
        var repair = provider.RepairAsync("kokoro", cancellation.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (callerCancellation) { cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repair); }
        else await Assert.ThrowsAsync<TimeoutException>(() => repair);
        Assert.Equal(before, File.ReadAllText(PinPath(fixture))); Assert.Equal(0, fixture.Runtime.Creates); Assert.Equal(0, fixture.Runtime.Starts);
    }
}
