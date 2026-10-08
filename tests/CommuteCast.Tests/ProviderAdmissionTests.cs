using CommuteCast.Infrastructure;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CommuteCast.Tests;

public partial class ProviderContractTests
{
    private static string AdmissionPath(Fixture fixture) => Path.Combine(fixture.Test.Workspace.Root, "speech-admission.json");
    private static void WriteAdmission(Fixture fixture, long sequence = 1) => File.WriteAllText(AdmissionPath(fixture), JsonSerializer.Serialize(new
    { Version = 1, fixture.Engine, Image = FakeRuntime.Image, fixture.Fingerprint, Instance = fixture.Http.Instance, Sequence = sequence }));

    [Fact] public async Task NormalReadinessWaitsBeyondCleanupBudgetAndReportsActiveWaiting()
    {
        using var fixture = new Fixture(); WriteAdmission(fixture);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Http.Control = (route, body, _) =>
        {
            entered.TrySetResult(); return Task.FromResult(fixture.Http.ControlResponse(body, finish.Task.IsCompleted ? "settled" : "active"));
        };
        var job = new CommuteCast.Core.Job { Settings = fixture.Settings }; job.Activity.Start(null);
        using var provider = fixture.Provider(Fixture.ShortLimits with { Readiness = TimeSpan.FromSeconds(10), Quiescence = TimeSpan.FromMilliseconds(100) });
        var pending = provider.ReadyForJobAsync(job, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); await Task.Delay(200);
            Assert.False(pending.IsCompleted); Assert.True(job.Activity.Read().Waiting);
            Assert.Contains("previous speech request", job.Activity.Read().Notice);
            Assert.True(File.Exists(AdmissionPath(fixture))); Assert.Equal(0, fixture.Http.Posts);
        }
        finally { finish.TrySetResult(); }
        await pending; Assert.False(File.Exists(AdmissionPath(fixture)));
        Assert.Equal(0, fixture.Runtime.Starts);
    }

    [Fact] public async Task SlowOwnershipVerificationDoesNotConsumeInferenceSettlementAllowance()
    {
        using var fixture = new Fixture(); WriteAdmission(fixture);
        fixture.Runtime.BeforeDocker = async (args, ct) =>
        { if (args[0] == "inspect") await Task.Delay(2200, ct); };
        // Give file I/O and fixture initialization room under concurrent builds;
        // the inspection still exceeds the separate settlement allowance.
        using var provider = fixture.Provider(Fixture.ShortLimits with { Readiness = TimeSpan.FromSeconds(30), Quiescence = TimeSpan.FromSeconds(2) });
        await provider.ReadyAsync(fixture.Engine, default, true);
        Assert.Equal(1, fixture.Http.Settles);
        Assert.False(File.Exists(AdmissionPath(fixture)));
        Assert.Equal(0, fixture.Runtime.Starts); Assert.Equal(0, fixture.Http.Posts);
    }
    [Fact] public async Task OwnershipVerificationTimeoutDoesNotClaimInferenceIsStillActive()
    {
        using var fixture = new Fixture(); WriteAdmission(fixture);
        var saved = await File.ReadAllTextAsync(AdmissionPath(fixture));
        fixture.Runtime.BeforeDocker = async (_, ct) => await Task.Delay(Timeout.Infinite, ct);
        using var provider = fixture.Provider(Fixture.ShortLimits with { Readiness = TimeSpan.FromMilliseconds(200) });
        var error = await Assert.ThrowsAsync<TimeoutException>(() => provider.SynthesizeAsync(fixture.Settings, "text", fixture.Output, default));
        Assert.Contains("does not establish that inference is active", error.Message);
        Assert.Equal(saved, await File.ReadAllTextAsync(AdmissionPath(fixture)));
        Assert.Equal(0, fixture.Http.Settles); Assert.Equal(0, fixture.Http.Posts); Assert.Equal(0, fixture.Runtime.Starts);
    }

    [Fact] public async Task AdmissionGateWaitUsesTheRequestDeadlineAndDoesNotReleaseAnotherOwner()
    {
        using var fixture = new Fixture(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Http.Speech = (_, _) => { entered.SetResult(); return release.Task; };
        fixture.Http.Control = (route, body, _) => Task.FromResult(fixture.Http.ControlResponse(body, route == "/reserve" ? "reserved" : "active"));
        // The first request must reach the server before cancellation. A 150ms
        // budget can expire in journal file I/O on this Windows machine. Keep
        // settlement longer than the second caller's budget to prove its wait
        // times out without releasing the first caller's admission gate.
        using var provider = fixture.Provider(Fixture.ShortLimits with { Synthesis = TimeSpan.FromSeconds(2), Quiescence = TimeSpan.FromSeconds(4) });
        using var stop = new CancellationTokenSource();
        var first = provider.SynthesizeAsync(fixture.Settings, "first", fixture.Output, stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); stop.Cancel();
        await Assert.ThrowsAsync<TimeoutException>(() => provider.SynthesizeAsync(fixture.Settings, "second", fixture.Output, default));
        Assert.False(first.IsCompleted); Assert.Equal(1, fixture.Http.Posts);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first); Assert.True(File.Exists(AdmissionPath(fixture))); release.SetResult(fixture.Audio());
    }

    [Fact] public async Task CancellationHoldsAdmissionUntilActiveServerSettles()
    {
        using var fixture = new Fixture(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverFinished = false;
        fixture.Http.Speech = (_, _) => { entered.SetResult(); return release.Task; };
        fixture.Http.Control = (route, body, _) => Task.FromResult(fixture.Http.ControlResponse(body, route == "/reserve" ? "reserved" : serverFinished ? "settled" : "active"));
        using var provider = fixture.Provider(Fixture.ShortLimits with { Quiescence = TimeSpan.FromSeconds(1) }); using var stop = new CancellationTokenSource();
        var pending = provider.SynthesizeAsync(fixture.Settings, "private text", fixture.Output, stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); stop.Cancel();
        await Task.Delay(40); Assert.False(pending.IsCompleted); Assert.True(File.Exists(AdmissionPath(fixture)));
        serverFinished = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        release.SetResult(fixture.Audio()); Assert.False(File.Exists(fixture.Output)); Assert.False(File.Exists(AdmissionPath(fixture)));
        Assert.Equal(0, fixture.Runtime.Starts); Assert.Equal(0, fixture.Runtime.Launches);
    }
    [Fact] public async Task UnsettledCancellationBlocksOtherEngineAndSurvivesNewProvider()
    {
        using var fixture = new Fixture(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Http.Speech = (_, _) => { entered.SetResult(); return release.Task; };
        fixture.Http.Control = (route, body, _) => Task.FromResult(fixture.Http.ControlResponse(body, route == "/reserve" ? "reserved" : "active"));
        using (var provider = fixture.Provider())
        using (var stop = new CancellationTokenSource())
        {
            var pending = provider.SynthesizeAsync(fixture.Settings, "private text", fixture.Output, stop.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        var saved = await File.ReadAllTextAsync(AdmissionPath(fixture)); Assert.DoesNotContain("private text", saved);
        using var next = fixture.Provider();
        var stillSettling = await Assert.ThrowsAsync<TimeoutException>(() => next.ReadyAsync("piper", default, true));
        Assert.DoesNotContain("Repair Docker", stillSettling.Message);
        Assert.Equal(saved, await File.ReadAllTextAsync(AdmissionPath(fixture))); Assert.Equal(1, fixture.Http.Posts);
        Assert.All(fixture.Http.Uris, uri => Assert.Equal(8765, uri.Port)); Assert.Equal(0, fixture.Runtime.Starts); Assert.Equal(0, fixture.Runtime.Launches);
        Assert.False(File.Exists(Path.Combine(fixture.Test.Workspace.Root, "recovery-piper.json")));
        fixture.Http.Control = null; await next.ReadyAsync("kokoro", default, true);
        Assert.False(File.Exists(AdmissionPath(fixture))); release.SetResult(fixture.Audio());
    }
    [Fact] public async Task CancellationDuringReserveRetiresBeforeSourceIsSent()
    {
        using var fixture = new Fixture(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Http.Control = (route, body, _) =>
        {
            if (route == "/reserve") { entered.SetResult(); return release.Task; }
            return Task.FromResult(fixture.Http.ControlResponse(body, "settled"));
        };
        using var provider = fixture.Provider(); using var stop = new CancellationTokenSource();
        var pending = provider.SynthesizeAsync(fixture.Settings, "private text", fixture.Output, stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.True(File.Exists(AdmissionPath(fixture))); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.Equal(0, fixture.Http.Posts); Assert.Equal(1, fixture.Http.Settles);
        Assert.False(File.Exists(AdmissionPath(fixture))); release.SetResult(new(HttpStatusCode.Conflict));
    }
    [Fact] public async Task ChangedReservationIsPreservedAndBlocksSuccessfulReturn()
    {
        using var fixture = new Fixture();
        fixture.Http.Control = async (route, body, _) =>
        {
            if (route == "/settle") await File.AppendAllTextAsync(AdmissionPath(fixture), " ");
            return fixture.Http.ControlResponse(body, route == "/reserve" ? "reserved" : "settled");
        };
        using var provider = fixture.Provider(); await Assert.ThrowsAsync<IOException>(() => provider.SynthesizeAsync(fixture.Settings, "text", fixture.Output, default));
        Assert.True(File.Exists(AdmissionPath(fixture))); Assert.Equal(1, fixture.Http.Settles); Assert.Equal(0, fixture.Runtime.Starts);
    }
    [Theory] [InlineData("instance")] [InlineData("sequence")] [InlineData("engine")] [InlineData("state")] [InlineData("type")]
    public async Task InvalidSettlementNeverClearsDurableFence(string defect)
    {
        using var fixture = new Fixture(); WriteAdmission(fixture); var hash = await Workspace.HashFileAsync(AdmissionPath(fixture));
        fixture.Http.Control = (route, body, _) =>
        {
            var response = fixture.Http.ControlResponse(body, "settled");
            var data = JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!;
            if (defect == "instance") data["instance"] = new string('e', 32);
            if (defect == "sequence") data["sequence"] = 2;
            if (defect == "engine") data["engine"] = "piper";
            if (defect == "state") data["state"] = "reserved";
            response.Content = new StringContent(data.ToJsonString(), System.Text.Encoding.UTF8, defect == "type" ? "text/plain" : "application/json"); return Task.FromResult(response);
        };
        using var provider = fixture.Provider(); await Assert.ThrowsAsync<IOException>(() => provider.ReadyAsync("kokoro", default));
        Assert.Equal(hash, await Workspace.HashFileAsync(AdmissionPath(fixture))); Assert.Equal(0, fixture.Http.Posts); Assert.Equal(0, fixture.Runtime.Starts);
    }
    [Theory] [InlineData("{")] [InlineData("null")] [InlineData("{}")] [InlineData("{\"Version\":2}")]
    public async Task CorruptReservationBlocksRecoveryAndSourceWithoutEcho(string text)
    {
        using var fixture = new Fixture(); await File.WriteAllTextAsync(AdmissionPath(fixture), text); using var provider = fixture.Provider();
        await Assert.ThrowsAsync<IOException>(() => provider.ReadyAsync("kokoro", default, true));
        await Assert.ThrowsAsync<IOException>(() => provider.SynthesizeAsync(fixture.Settings, "text", fixture.Output, default));
        Assert.Equal(text, await File.ReadAllTextAsync(AdmissionPath(fixture))); Assert.Empty(fixture.Runtime.Calls); Assert.Empty(fixture.Http.Uris);
    }
    [Fact] public async Task DirectoryReservationBlocksRecoveryBeforeRuntime()
    {
        using var fixture = new Fixture(); Directory.CreateDirectory(AdmissionPath(fixture)); using var provider = fixture.Provider();
        await Assert.ThrowsAsync<IOException>(() => provider.ReadyAsync("kokoro", default, true));
        Assert.True(Directory.Exists(AdmissionPath(fixture))); Assert.Empty(fixture.Runtime.Calls); Assert.Empty(fixture.Http.Uris);
    }
    [Fact] public async Task NewOwnedServiceProcessClearsOldReservationWithoutSendingItToReplacement()
    {
        using var fixture = new Fixture(); WriteAdmission(fixture); fixture.Http.Instance = new('e', 32); using var provider = fixture.Provider();
        await provider.ReadyAsync("kokoro", default); Assert.False(File.Exists(AdmissionPath(fixture))); Assert.Equal(0, fixture.Http.Settles); Assert.Equal(0, fixture.Http.Posts); Assert.Equal(0, fixture.Runtime.Starts);
    }
    [Fact] public async Task ExplicitReprovisionToNewOwnedImageAndProcessSettlesOldFence()
    {
        using var fixture = new Fixture(); WriteAdmission(fixture); fixture.Http.Instance = new('e', 32);
        var revised = "sha256:" + new string('b', 64); fixture.Runtime.Container["Image"] = revised;
        File.WriteAllText(Path.Combine(fixture.Test.Workspace.Root, "provider-lock.local.json"), JsonSerializer.Serialize(new { ImageId = revised, Contract = 1 }));
        // This verifies replacement identity, not a subsecond deadline. The
        // 200ms default can expire during fixture file I/O under a full-suite load.
        using var provider = fixture.Provider(Fixture.ShortLimits with { Quiescence = TimeSpan.FromSeconds(2) });
        var ready = await provider.ReadyAsync("kokoro", default);
        Assert.Equal(revised, ready.ImageId); Assert.False(File.Exists(AdmissionPath(fixture))); Assert.Equal(0, fixture.Http.Settles); Assert.Equal(0, fixture.Runtime.Starts);
    }
    [Fact] public async Task RestorePreservesCurrentRuntimeAdmissionFenceOutsideBackedUpHistory()
    {
        using var fixture = new Fixture(); using var lease = WorkspaceLease.Acquire(fixture.Test.Workspace);
        var store = new SqliteJobStore(fixture.Test.Workspace); await store.LoadAsync();
        var backup = await WorkspaceBackup.CreateAsync(lease);
        WriteAdmission(fixture); var hash = await Workspace.HashFileAsync(AdmissionPath(fixture));
        await WorkspaceBackup.RestoreAsync(lease, backup);
        Assert.Equal(hash, await Workspace.HashFileAsync(AdmissionPath(fixture)));
        Assert.False(File.Exists(Path.Combine(backup, "speech-admission.json")));
    }
    [Fact] public async Task ServiceWithoutReservationProtocolIsRefusedBeforeSourceOrJournal()
    {
        using var fixture = new Fixture(); var data = JsonNode.Parse(fixture.Health())!.AsObject(); data.Remove("admission"); data.Remove("instance"); data.Remove("sequence");
        fixture.Http.Health = _ => data.ToJsonString(); using var provider = fixture.Provider();
        var error = await Assert.ThrowsAsync<IOException>(() => provider.SynthesizeAsync(fixture.Settings, "text", fixture.Output, default));
        Assert.Contains("Reprovision", error.Message); Assert.Equal(0, fixture.Http.Posts); Assert.Equal(0, fixture.Http.Reserves); Assert.False(File.Exists(AdmissionPath(fixture)));
    }
    [Fact] public async Task ReadOnlyProbeDoesNotSettleOrRemovePendingReservation()
    {
        using var fixture = new Fixture(); WriteAdmission(fixture); var hash = await Workspace.HashFileAsync(AdmissionPath(fixture)); using var provider = fixture.Provider();
        await provider.ProbeAsync("kokoro"); Assert.Equal(hash, await Workspace.HashFileAsync(AdmissionPath(fixture))); Assert.Equal(0, fixture.Http.Settles);
    }
    [Fact] public async Task VerifiedStoppedServiceSettlesPendingFenceBeforeOneOwnedStart()
    {
        using var fixture = new Fixture(); WriteAdmission(fixture); fixture.Runtime.Container["State"]!["Running"] = false;
        fixture.Runtime.OnStart = () => { Assert.False(File.Exists(AdmissionPath(fixture))); fixture.Http.Instance = new('e', 32); };
        using var provider = fixture.Provider(); var ready = await provider.ReadyAsync("kokoro", default);
        Assert.Equal(new string('e', 32), ready.InstanceId); Assert.Equal(1, fixture.Runtime.Starts); Assert.Equal(0, fixture.Runtime.Launches);
        Assert.Equal(0, fixture.Http.Settles); Assert.False(File.Exists(AdmissionPath(fixture)));
    }
    [Fact] public async Task ReadOnlyStoppedProbePreservesPendingFenceAndDoesNotStart()
    {
        using var fixture = new Fixture(); WriteAdmission(fixture); var hash = await Workspace.HashFileAsync(AdmissionPath(fixture)); fixture.Runtime.Container["State"]!["Running"] = false;
        using var provider = fixture.Provider(); await Assert.ThrowsAsync<IOException>(() => provider.ProbeAsync("kokoro"));
        Assert.Equal(hash, await Workspace.HashFileAsync(AdmissionPath(fixture))); Assert.Equal(0, fixture.Runtime.Starts); Assert.Equal(0, fixture.Http.Settles);
    }
    [Theory] [InlineData("foreign")] [InlineData("oom")] [InlineData("paused")] [InlineData("missing")] [InlineData("daemon")]
    public async Task UnprovenOrUnsafeStoppedServiceCannotClearFenceOrRecover(string defect)
    {
        using var fixture = new Fixture(); WriteAdmission(fixture); var hash = await Workspace.HashFileAsync(AdmissionPath(fixture)); fixture.Runtime.Container["State"]!["Running"] = false;
        if (defect == "foreign") fixture.Runtime.Container["Name"] = "/foreign";
        if (defect == "oom") fixture.Runtime.Container["State"]!["OOMKilled"] = true;
        if (defect == "paused") fixture.Runtime.Container["State"]!["Paused"] = true;
        if (defect == "missing") fixture.Runtime.Missing = true;
        if (defect == "daemon") fixture.Runtime.DaemonFailures = 100;
        using var provider = fixture.Provider(); await Assert.ThrowsAnyAsync<IOException>(() => provider.ReadyAsync("kokoro", default, true));
        Assert.Equal(hash, await Workspace.HashFileAsync(AdmissionPath(fixture))); Assert.Equal(0, fixture.Runtime.Starts); Assert.Equal(0, fixture.Runtime.Launches); Assert.Empty(fixture.Http.Uris);
    }
}
