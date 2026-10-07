using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CommuteCast.Tests;

public partial class ProviderContractTests
{
    [Fact] public async Task ReadOnlyProbeLeavesSpentRecoveryAllowanceAndRequestsNoSpeech()
    {
        using var fixture = new Fixture(); await new RecoveryBudget(fixture.Test.Workspace).BeginAsync("kokoro", default);
        var budget = Path.Combine(fixture.Test.Workspace.Root, "recovery-kokoro.json"); var before = await Workspace.HashFileAsync(budget);
        using var provider = fixture.Provider(); var result = await provider.ProbeAsync("kokoro"); Assert.Equal("ready", result.State);
        Assert.Equal(before, await Workspace.HashFileAsync(budget)); Assert.Equal(0, fixture.Runtime.Starts); Assert.Equal(0, fixture.Runtime.Launches); Assert.Equal(0, fixture.Http.Posts);
    }
    [Theory] [InlineData(false)] [InlineData(true)] public async Task ReadOnlyProbeNeverStartsStoppedOrUnverifiedContainer(bool foreign)
    {
        using var fixture = new Fixture(); fixture.Runtime.Container["State"]!["Running"] = false;
        if (foreign) fixture.Runtime.Container["Name"] = "/foreign";
        using var provider = fixture.Provider(); await Assert.ThrowsAsync<IOException>(() => provider.ProbeAsync("kokoro"));
        Assert.Equal(0, fixture.Runtime.Starts); Assert.Equal(0, fixture.Runtime.Launches); Assert.Empty(fixture.Http.Uris);
        Assert.False(File.Exists(Path.Combine(fixture.Test.Workspace.Root, "recovery-kokoro.json")));
    }
    [Fact] public async Task OutputOutsidePrivateWorkspaceIsRefusedBeforeRuntimeOrHttp()
    {
        using var fixture = new Fixture(); using var provider = fixture.Provider();
        await Assert.ThrowsAsync<IOException>(() => provider.SynthesizeAsync(fixture.Settings, "text", Path.Combine(fixture.Test.Destination, "unexpected.wav"), default));
        Assert.Empty(fixture.Runtime.Calls); Assert.Empty(fixture.Http.Uris);
    }
    [Fact] public async Task VoiceChangeWhileBusyBlocksSourceBeforePost()
    {
        using var fixture = new Fixture(); fixture.Http.Health = index => index == 1 ? fixture.Health("ready", 1) : fixture.Health().Replace("af_heart", "am_michael");
        using var provider = fixture.Provider(); await Assert.ThrowsAsync<IOException>(() => provider.SynthesizeAsync(fixture.Settings, "text", fixture.Output, default));
        Assert.Equal(0, fixture.Http.Posts);
    }
    [Theory] [InlineData(0.5, 10)] [InlineData(2.0, 10)] [InlineData(double.NaN, 10)] [InlineData(1.0, 901)] [InlineData(1.0, 0)]
    public async Task InvalidInputAndUnsupportedPaceAreRejectedBeforeAnyRuntimeAccess(double speed, int size)
    {
        using var fixture = new Fixture(); using var provider = fixture.Provider();
        await Assert.ThrowsAsync<ArgumentException>(() => provider.SynthesizeAsync(fixture.Settings with { Speed = speed }, new string('a', size), fixture.Output, default)); Assert.Empty(fixture.Runtime.Calls); Assert.Empty(fixture.Http.Uris);
    }
    [Theory] [InlineData("[]")] [InlineData("{}")] [InlineData("[{\"Private provider echo\":true}]")] [InlineData("Private provider echo")]
    public async Task MalformedDockerMetadataDoesNotReachHttpOrEchoContent(string json)
    {
        using var fixture = new Fixture(); fixture.Runtime.MetadataOverride = json; using var provider = fixture.Provider();
        var error = await Assert.ThrowsAsync<IOException>(() => provider.ReadyAsync("kokoro", default));
        Assert.DoesNotContain("Private provider echo", error.Message); Assert.Empty(fixture.Http.Uris); Assert.Equal(0, fixture.Runtime.Starts);
    }
    [Fact] public async Task StreamedSizeLimitIsEnforcedWithoutAContentLengthHeader()
    {
        using var fixture = new Fixture(); fixture.Http.Speech = (_, _) => Task.FromResult(fixture.AudioContent(new StreamContent(new OversizedStream())));
        using var provider = fixture.Provider(); var error = await Assert.ThrowsAsync<IOException>(() => provider.SynthesizeAsync(fixture.Settings, "text", fixture.Output, default));
        Assert.Contains("size", error.Message); Assert.False(File.Exists(fixture.Output)); Assert.Empty(Directory.GetFiles(fixture.Test.Workspace.Root, "*.partial"));
    }
    [Theory] [InlineData("kokoro")] [InlineData("piper")]
    public async Task ApprovedProviderUsesLoopbackContractAndPromotesValidatedLosslessAudio(string engine)
    {
        using var fixture = new Fixture(engine); using var provider = fixture.Provider();
        var ready = await provider.ReadyAsync(engine, default); Assert.Equal(fixture.Fingerprint, ready.Fingerprint);
        await provider.SynthesizeAsync(fixture.Settings, "Preserve this exact API text.", fixture.Output, default);
        Assert.Equal(fixture.Wave, await File.ReadAllBytesAsync(fixture.Output));
        Assert.Equal(1, fixture.Http.Posts); Assert.Equal(0, fixture.Runtime.Starts); Assert.Equal(0, fixture.Runtime.Launches);
        using var body = JsonDocument.Parse(Assert.Single(fixture.Http.Bodies));
        Assert.Equal("Preserve this exact API text.", body.RootElement.GetProperty("text").GetString());
        Assert.Equal(fixture.Settings.Voice, body.RootElement.GetProperty("voice").GetString()); Assert.Equal(fixture.Fingerprint, body.RootElement.GetProperty("fingerprint").GetString());
        Assert.All(fixture.Http.Uris, uri => { Assert.Equal("http", uri.Scheme); Assert.Equal("127.0.0.1", uri.Host); Assert.Equal(engine == "kokoro" ? 8765 : 8766, uri.Port); });
        Assert.Empty(Directory.GetFiles(fixture.Test.Workspace.Root, "*.partial"));
    }
    [Theory]
    [InlineData("owner")] [InlineData("project")] [InlineData("name")] [InlineData("image")] [InlineData("contract")]
    [InlineData("address")] [InlineData("port")] [InlineData("extra-port")] [InlineData("command")]
    [InlineData("entrypoint")] [InlineData("engine")] [InlineData("privileged")] [InlineData("mount")]
    [InlineData("network")] [InlineData("cpu")] [InlineData("memory")] [InlineData("security")]
    [InlineData("oom")] [InlineData("paused")] [InlineData("restarting")]
    public async Task ForeignOrUnsafeContainerNeverStartsAndNeverReceivesSource(string defect)
    {
        using var fixture = new Fixture(); var node = fixture.Runtime.Container;
        switch (defect)
        {
            case "owner": node["Config"]!["Labels"]!["com.commutecast.owner"] = "Other application"; break;
            case "project": node["Config"]!["Labels"]!["com.docker.compose.project"] = "other"; break;
            case "name": node["Name"] = "/foreign"; break;
            case "image": node["Image"] = "sha256:" + new string('b', 64); break;
            case "contract": node["Config"]!["Labels"]!["com.commutecast.contract"] = "2"; break;
            case "address": node["HostConfig"]!["PortBindings"]!["8765/tcp"]![0]!["HostIp"] = "0.0.0.0"; break;
            case "port": node["HostConfig"]!["PortBindings"]!["8765/tcp"]![0]!["HostPort"] = "9999"; break;
            case "extra-port": node["HostConfig"]!["PortBindings"]!["8080/tcp"] = new JsonArray(); break;
            case "command": node["Config"]!["Cmd"] = new JsonArray("foreign-program"); break;
            case "entrypoint": node["Config"]!["Entrypoint"] = new JsonArray("sh", "-c"); break;
            case "engine": node["Config"]!["Env"] = new JsonArray("COMMUTECAST_ENGINE=piper"); break;
            case "privileged": node["HostConfig"]!["Privileged"] = true; break;
            case "mount": node["Mounts"] = new JsonArray(new JsonObject { ["Source"] = "private host directory" }); break;
            case "network": node["HostConfig"]!["NetworkMode"] = "host"; break;
            case "cpu": node["HostConfig"]!["NanoCpus"] = 0; break;
            case "memory": node["HostConfig"]!["Memory"] = 0; break;
            case "security": node["HostConfig"]!["SecurityOpt"] = new JsonArray(); break;
            case "oom": node["State"]!["OOMKilled"] = true; break;
            case "paused": node["State"]!["Paused"] = true; break;
            case "restarting": node["State"]!["Restarting"] = true; break;
        }
        node["State"]!["Running"] = false; using var provider = fixture.Provider();
        await Assert.ThrowsAsync<IOException>(() => provider.ReadyAsync("kokoro", default));
        await Assert.ThrowsAsync<IOException>(() => provider.SynthesizeAsync(fixture.Settings, "Sensitive source", fixture.Output, default));
        Assert.Equal(0, fixture.Runtime.Starts); Assert.Equal(0, fixture.Runtime.Launches); Assert.Empty(fixture.Http.Uris); Assert.False(File.Exists(fixture.Output));
    }
    [Fact] public async Task RemoteContextBlocksDesktopAndContainerMutationBeforeHttp()
    {
        using var fixture = new Fixture(); fixture.Runtime.Context = "ssh://other-host"; using var provider = fixture.Provider();
        await Assert.ThrowsAsync<IOException>(() => provider.ReadyAsync("kokoro", default));
        Assert.Single(fixture.Runtime.Calls); Assert.Empty(fixture.Http.Uris); Assert.Equal(0, fixture.Runtime.Launches); Assert.Equal(0, fixture.Runtime.Starts);
    }
    [Fact] public async Task MissingContainerIsDiagnosedWithoutPullBuildOrCreation()
    {
        using var fixture = new Fixture(); fixture.Runtime.Missing = true; using var provider = fixture.Provider();
        var error = await Assert.ThrowsAsync<IOException>(() => provider.ReadyAsync("kokoro", default)); Assert.Contains("missing", error.Message);
        Assert.Equal(0, fixture.Runtime.Starts); Assert.Empty(fixture.Http.Uris); Assert.DoesNotContain(fixture.Runtime.Calls, args => args[0] is "pull" or "build" or "compose" or "run");
    }
    [Fact] public async Task StoppedOwnedContainerStartsOnceAndIsReinspected()
    {
        using var fixture = new Fixture(); fixture.Runtime.Container["State"]!["Running"] = false; using var provider = fixture.Provider();
        await provider.ReadyAsync("kokoro", default);
        Assert.Equal(1, fixture.Runtime.Starts); Assert.Equal(2, fixture.Runtime.Calls.Count(c => c[0] == "inspect"));
        Assert.All(fixture.Runtime.Calls.Where(c => c[0] == "start"), c => Assert.Equal(new[] { "start", "commutecast-kokoro" }, c));
    }
    [Fact] public async Task ContainerIdentityChangeAfterStartBlocksHttp()
    {
        using var fixture = new Fixture(); fixture.Runtime.Container["State"]!["Running"] = false;
        fixture.Runtime.OnStart = () => fixture.Runtime.Container["Config"]!["Labels"]!["com.commutecast.owner"] = "foreign";
        using var provider = fixture.Provider(); await Assert.ThrowsAsync<IOException>(() => provider.ReadyAsync("kokoro", default));
        Assert.Equal(1, fixture.Runtime.Starts); Assert.Empty(fixture.Http.Uris);
    }
    [Fact] public async Task StoppedContainerCannotRestartDuringSynthesis()
    {
        using var fixture = new Fixture(); fixture.Runtime.Container["State"]!["Running"] = false; using var provider = fixture.Provider();
        await Assert.ThrowsAsync<IOException>(() => provider.SynthesizeAsync(fixture.Settings, "text", fixture.Output, default));
        Assert.Equal(0, fixture.Runtime.Starts); Assert.Empty(fixture.Http.Uris);
    }
    [Fact] public async Task DaemonOutageGetsOneLaunchAndSharedExhaustionAcrossJobs()
    {
        using var fixture = new Fixture(); fixture.Runtime.DaemonFailures = 100; using var provider = fixture.Provider();
        await Assert.ThrowsAsync<IOException>(() => provider.ReadyAsync("kokoro", default));
        Assert.Equal(1, fixture.Runtime.Launches); Assert.Equal(11, fixture.Runtime.Calls.Count(c => c[0] == "version"));
        var count = fixture.Runtime.Calls.Count;
        await Assert.ThrowsAsync<IOException>(() => provider.ReadyAsync("kokoro", default)); Assert.Equal(count, fixture.Runtime.Calls.Count); Assert.Empty(fixture.Http.Uris);
        fixture.Runtime.DaemonFailures = 0; await provider.ReadyAsync("kokoro", default, true);
        Assert.Equal(1, fixture.Runtime.Launches); Assert.Equal(0, fixture.Runtime.Starts);
    }
    [Fact] public async Task LoadingAndBusyStatesOnlyWaitAndDoNotRestart()
    {
        using var fixture = new Fixture(); fixture.Http.Health = index => index switch { 1 => fixture.Health("loading", 0), 2 => fixture.Health("ready", 1), _ => fixture.Health() };
        using var provider = fixture.Provider(); await provider.ReadyAsync("kokoro", default);
        Assert.Equal(3, fixture.Http.Gets); Assert.Equal(0, fixture.Runtime.Starts); Assert.Equal(0, fixture.Runtime.Launches);
        fixture.Http.Health = index => fixture.Health("ready", index < 6 ? 1 : 0);
        await provider.SynthesizeAsync(fixture.Settings, "text", fixture.Output, default); Assert.Equal(7, fixture.Http.Gets); Assert.Equal(1, fixture.Http.Posts);
    }
    [Fact] public async Task LoadingTimeoutDoesNotCreateANewAllowanceForNextJob()
    {
        using var fixture = new Fixture(); fixture.Http.Health = _ => fixture.Health("loading", 0); using var provider = fixture.Provider(Fixture.ShortLimits with { Readiness = TimeSpan.FromMilliseconds(100) });
        await Assert.ThrowsAsync<TimeoutException>(() => provider.ReadyAsync("kokoro", default));
        var calls = fixture.Runtime.Calls.Count; await Assert.ThrowsAsync<IOException>(() => provider.ReadyAsync("kokoro", default));
        Assert.Equal(calls, fixture.Runtime.Calls.Count); Assert.Equal(0, fixture.Runtime.Starts);
    }
    [Theory] [InlineData("service")] [InlineData("contract")] [InlineData("engine")] [InlineData("fingerprint")]
    [InlineData("state")] [InlineData("active")] [InlineData("empty-voices")] [InlineData("voice-name")]
    [InlineData("duplicate-voices")] [InlineData("missing-field")] [InlineData("invalid-json")] [InlineData("oversize")]
    public async Task IncompatibleHealthBlocksSourceAndDoesNotEchoResponse(string defect)
    {
        using var fixture = new Fixture(); var health = JsonNode.Parse(fixture.Health())!;
        switch (defect)
        {
            case "service": health["service"] = "Private response echo"; break;
            case "contract": health["contract"] = 2; break;
            case "engine": health["engine"] = "piper"; break;
            case "fingerprint": health["fingerprint"] = "Private response echo"; break;
            case "state": health["state"] = "unknown"; break;
            case "active": health["active"] = -1; break;
            case "empty-voices": health["voices"] = new JsonArray(); break;
            case "voice-name": health["voices"] = new JsonArray("Private response echo"); break;
            case "duplicate-voices": health["voices"] = new JsonArray("af_heart", "af_heart"); break;
            case "missing-field": health.AsObject().Remove("service"); break;
        }
        fixture.Http.Health = _ => defect == "invalid-json" ? "Private response echo" : defect == "oversize" ? new string('x', 66000) : health.ToJsonString();
        using var provider = fixture.Provider(); var error = await Assert.ThrowsAsync<IOException>(() => provider.SynthesizeAsync(fixture.Settings, "private source", fixture.Output, default));
        Assert.DoesNotContain("Private response echo", error.Message); Assert.Equal(0, fixture.Http.Posts); Assert.False(File.Exists(fixture.Output));
    }
    [Theory] [InlineData(429)] [InlineData(500)] [InlineData(502)] [InlineData(503)] [InlineData(504)]
    public async Task TransientFailureRetriesOnlyCompatibleRequest(int status)
    {
        using var fixture = new Fixture(); fixture.Http.Speech = (index, _) => Task.FromResult(index == 1 ? new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("Private response echo") } : fixture.Audio());
        using var provider = fixture.Provider(); await provider.SynthesizeAsync(fixture.Settings, "original text", fixture.Output, default);
        Assert.Equal(2, fixture.Http.Posts);
        using var first = JsonDocument.Parse(fixture.Http.Bodies[0]); using var second = JsonDocument.Parse(fixture.Http.Bodies[1]);
        Assert.Equal(first.RootElement.GetProperty("text").GetString(), second.RootElement.GetProperty("text").GetString());
        Assert.True(second.RootElement.GetProperty("sequence").GetInt64() > first.RootElement.GetProperty("sequence").GetInt64()); Assert.Equal(0, fixture.Runtime.Starts);
    }
    [Fact] public async Task RetryExhaustionStopsAfterThreeRequestsAndDiscardsPartials()
    {
        using var fixture = new Fixture(); fixture.Http.Speech = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("Private response echo") });
        using var provider = fixture.Provider(); var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.SynthesizeAsync(fixture.Settings, "text", fixture.Output, default));
        Assert.Equal(3, fixture.Http.Posts); Assert.DoesNotContain("Private response echo", error.Message); Assert.False(File.Exists(fixture.Output)); Assert.Empty(Directory.GetFiles(fixture.Test.Workspace.Root, "*.partial"));
    }
    [Theory] [InlineData(302)] [InlineData(400)] [InlineData(401)] [InlineData(403)] [InlineData(409)] [InlineData(413)] [InlineData(422)]
    public async Task PermanentErrorsAndRedirectsAreNotRetriedOrEchoed(int status)
    {
        using var fixture = new Fixture(); fixture.Http.Speech = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("Private response echo"), Headers = { Location = new Uri("https://unapproved.invalid/") } });
        using var provider = fixture.Provider(); var error = await Assert.ThrowsAsync<IOException>(() => provider.SynthesizeAsync(fixture.Settings, "private text", fixture.Output, default));
        Assert.Equal(1, fixture.Http.Posts); Assert.DoesNotContain("Private response echo", error.Message); Assert.All(fixture.Http.Uris, uri => Assert.Equal("127.0.0.1", uri.Host));
    }
    [Theory] [InlineData("mime")] [InlineData("truncated")] [InlineData("compressed")] [InlineData("oversize-header")] [InlineData("length-mismatch")]
    public async Task InvalidAudioPreservesPriorOutputAndRemovesAttemptFile(string defect)
    {
        using var fixture = new Fixture(); await File.WriteAllTextAsync(fixture.Output, "prior valid output");
        fixture.Http.Speech = (_, _) =>
        {
            var bytes = fixture.Wave.ToArray(); if (defect == "truncated") bytes = bytes[..100]; if (defect == "compressed") bytes[20] = 3;
            var response = fixture.Audio(bytes); if (defect == "mime") response.Content.Headers.ContentType = new("audio/mpeg");
            if (defect == "oversize-header") response.Content.Headers.ContentLength = 65 * 1024 * 1024;
            if (defect == "length-mismatch") response.Content.Headers.ContentLength = bytes.Length + 1;
            return Task.FromResult(response);
        };
        using var provider = fixture.Provider(); await Assert.ThrowsAsync<IOException>(() => provider.SynthesizeAsync(fixture.Settings, "text", fixture.Output, default));
        Assert.Equal("prior valid output", await File.ReadAllTextAsync(fixture.Output)); Assert.Equal(1, fixture.Http.Posts); Assert.Empty(Directory.GetFiles(fixture.Test.Workspace.Root, "*.partial"));
    }
    [Fact] public async Task StreamDisconnectRetriesFromFreshAttemptAndPreservesOccupiedOutput()
    {
        using var fixture = new Fixture(); await File.WriteAllTextAsync(fixture.Output, "prior output");
        fixture.Http.Speech = (index, _) => Task.FromResult(index == 1 ? fixture.AudioContent(new StreamContent(new InterruptedStream())) : fixture.Audio());
        using var provider = fixture.Provider(); await Assert.ThrowsAsync<IOException>(() => provider.SynthesizeAsync(fixture.Settings, "text", fixture.Output, default));
        Assert.Equal(2, fixture.Http.Posts); Assert.Equal("prior output", await File.ReadAllTextAsync(fixture.Output)); Assert.Empty(Directory.GetFiles(fixture.Test.Workspace.Root, "*.partial"));
    }
    [Fact] public async Task CompetingOutputDuringBodyReadIsPreservedAndAttemptRemainsExclusivelyHeld()
    {
        using var fixture = new Fixture(); var observed = false;
        fixture.Http.Speech = (_, _) => Task.FromResult(fixture.AudioContent(new StreamContent(new ObservedStream(fixture.Wave, () =>
        {
            observed = true;
            var attempt = Assert.Single(Directory.GetFiles(fixture.Test.Workspace.Root, "*.partial"));
            Assert.Throws<IOException>(() => File.Delete(attempt));
            Assert.Throws<IOException>(() => File.WriteAllText(attempt, "Replacement"));
            File.WriteAllText(fixture.Output, "Competing output");
        }))));
        using var provider = fixture.Provider(); var error = await Assert.ThrowsAsync<IOException>(() => provider.SynthesizeAsync(fixture.Settings, "text", fixture.Output, default));
        Assert.DoesNotContain("MP3", error.Message);
        Assert.True(observed); Assert.Equal(1, fixture.Http.Posts);
        Assert.Equal("Competing output", await File.ReadAllTextAsync(fixture.Output));
        Assert.Empty(Directory.GetFiles(fixture.Test.Workspace.Root, "*.partial"));
    }
    [Fact] public async Task AttemptPathReusedAfterPublicationIsNeverRemovedBySettlementCleanup()
    {
        using var fixture = new Fixture(); string? attempt = null;
        fixture.Http.Speech = (_, _) => Task.FromResult(fixture.AudioContent(new StreamContent(new ObservedStream(fixture.Wave,
            () => attempt = Assert.Single(Directory.GetFiles(fixture.Test.Workspace.Root, "*.partial"))))));
        fixture.Http.Control = (route, body, _) =>
        {
            fixture.Http.Sequence = Math.Max(fixture.Http.Sequence, body.GetProperty("sequence").GetInt64());
            if (route == "/settle" && attempt is not null) File.WriteAllText(attempt, "Unknown later file");
            return Task.FromResult(fixture.Http.ControlResponse(body, route == "/reserve" ? "reserved" : "settled"));
        };
        using var provider = fixture.Provider(); await provider.SynthesizeAsync(fixture.Settings, "text", fixture.Output, default);
        Assert.NotNull(attempt); Assert.Equal(fixture.Wave, await File.ReadAllBytesAsync(fixture.Output));
        Assert.Equal("Unknown later file", await File.ReadAllTextAsync(attempt));
    }
    private sealed class ObservedStream(byte[] bytes, Action observe) : MemoryStream(bytes)
    {
        private bool observed;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!observed) { observed = true; observe(); }
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
    [Fact] public async Task CancelledLateResponseCannotPromoteOrReplacePriorOutput()
    {
        using var fixture = new Fixture(); await File.WriteAllTextAsync(fixture.Output, "prior output");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Http.Speech = (_, _) => { entered.TrySetResult(); return release.Task; };
        using var provider = fixture.Provider(); using var cancel = new CancellationTokenSource();
        var synthesis = provider.SynthesizeAsync(fixture.Settings, "text", fixture.Output, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => synthesis.WaitAsync(TimeSpan.FromSeconds(1)));
        release.TrySetResult(fixture.Audio()); await Task.Delay(50);
        Assert.Equal("prior output", await File.ReadAllTextAsync(fixture.Output)); Assert.Empty(Directory.GetFiles(fixture.Test.Workspace.Root, "*.partial")); Assert.Equal(1, fixture.Http.Posts);
    }
    [Fact] public async Task NoncooperativeBodyReadStillHasADeadline()
    {
        using var fixture = new Fixture(); var stream = new BlockedStream(); fixture.Http.Speech = (_, _) => Task.FromResult(fixture.AudioContent(new StreamContent(stream)));
        using var provider = fixture.Provider(Fixture.ShortLimits with { Synthesis = TimeSpan.FromMilliseconds(150) });
        try { await Assert.ThrowsAsync<TimeoutException>(() => provider.SynthesizeAsync(fixture.Settings, "text", fixture.Output, default).WaitAsync(TimeSpan.FromSeconds(1))); }
        finally { stream.Release.TrySetResult(0); }
        Assert.False(File.Exists(fixture.Output)); Assert.Empty(Directory.GetFiles(fixture.Test.Workspace.Root, "*.partial"));
    }
    [Theory] [InlineData("kokoro")] [InlineData("piper")]
    public async Task UnsupportedPronunciationIsRefusedBeforeRuntimeOrHttp(string engine)
    {
        using var fixture = new Fixture(engine); using var provider = fixture.Provider();
        foreach (var profile in new[] { new PronunciationProfile(Version: "future"), new(Language: "fr"), new(Numbers: (NumberReading)999) })
            await Assert.ThrowsAsync<ArgumentException>(() => provider.SynthesizeAsync(fixture.Settings with { Profile = profile }, "API 24", fixture.Output, default));
        Assert.Empty(fixture.Runtime.Calls); Assert.Empty(fixture.Http.Uris); Assert.False(File.Exists(fixture.Output));
    }
    [Theory] [InlineData("kokoro")] [InlineData("piper")]
    public async Task SupportedProfilesSendOnlyTheExactAlreadyReviewedScript(string engine)
    {
        using var fixture = new Fixture(engine); using var provider = fixture.Provider(); var profile = new PronunciationProfile(Numbers: NumberReading.NumberWords, Acronyms: AcronymReading.SpellUppercaseWords);
        var prepared = TextPreparation.Prepare("API 24", profile: profile);
        await provider.SynthesizeAsync(fixture.Settings with { Profile = profile }, prepared.Script, fixture.Output, default);
        Assert.Equal(1, fixture.Http.Posts); Assert.True(File.Exists(fixture.Output));
        using var body = JsonDocument.Parse(Assert.Single(fixture.Http.Bodies));
        Assert.Equal("A P I twenty four", body.RootElement.GetProperty("text").GetString());
        Assert.False(body.RootElement.TryGetProperty("profile", out _));
    }
    private sealed class Fixture : IDisposable
    {
        public TestWorkspace Test { get; } = new(); public FakeRuntime Runtime { get; } public Handler Http { get; }
        public string Engine { get; } public string Fingerprint => Engine + ":contract-v1:" + new string('c', 64);
        public NarrationSettings Settings => new(Engine, Engine == "kokoro" ? "af_heart" : "en_US-lessac-medium", 1, false, "", Fingerprint);
        public string Output => Path.Combine(Test.Workspace.Root, "response.wav"); public byte[] Wave { get; }
        public static SpeechProviderLimits ShortLimits => new() { Readiness = TimeSpan.FromSeconds(2), Health = TimeSpan.FromSeconds(1), Synthesis = TimeSpan.FromSeconds(3), Quiescence = TimeSpan.FromMilliseconds(200), Poll = TimeSpan.FromMilliseconds(5), RetryBackoff = TimeSpan.FromMilliseconds(5) };
        public Fixture(string engine = "kokoro")
        {
            Engine = engine; Runtime = new(engine); Http = new(this);
            File.WriteAllText(Path.Combine(Test.Workspace.Root, "provider-lock.local.json"), JsonSerializer.Serialize(new { ImageId = FakeRuntime.Image, Contract = 1 }));
            var wave = Path.Combine(Test.Workspace.Root, "fixture.wav"); TestWorkspace.WriteWave(wave); Wave = File.ReadAllBytes(wave);
        }
        public LocalSpeechProvider Provider(SpeechProviderLimits? limits = null) => new(Test.Workspace, Runtime, Http, limits ?? ShortLimits);
        public string Health(string state = "ready", int active = 0) => JsonSerializer.Serialize(new { service = "CommuteCast", contract = 1, engine = Engine, fingerprint = state == "loading" ? "" : Fingerprint, voices = state == "loading" ? Array.Empty<string>() : new[] { Settings.Voice }, state, active, admission = 1, instance = Http.Instance, sequence = Http.Sequence });
        public HttpResponseMessage Audio(byte[]? bytes = null) => AudioContent(new ByteArrayContent(bytes ?? Wave));
        public HttpResponseMessage AudioContent(HttpContent content) { content.Headers.ContentType = new("audio/wav"); return new(HttpStatusCode.OK) { Content = content }; }
        public void Dispose() => Test.Dispose();
    }
    private sealed class Handler(Fixture fixture) : HttpMessageHandler
    {
        public int Gets { get; private set; } public int Posts { get; private set; } public List<Uri> Uris { get; } = []; public List<string> Bodies { get; } = [];
        public Func<int, string>? Health { get; set; } public Func<int, CancellationToken, Task<HttpResponseMessage>>? Speech { get; set; }
        public Func<CancellationToken, Task>? BeforeHealth { get; set; }
        public string Instance { get; set; } = new('d', 32); public long Sequence { get; set; }
        public Func<string, JsonElement, CancellationToken, Task<HttpResponseMessage>>? Control { get; set; }
        public int Settles { get; private set; } public int Reserves { get; private set; }
        public HttpResponseMessage ControlResponse(JsonElement body, string state) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { service = "CommuteCast", contract = 1, engine = fixture.Engine, instance = Instance, sequence = body.GetProperty("sequence").GetInt64(), state }), System.Text.Encoding.UTF8, "application/json") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uris.Add(request.RequestUri!);
            if (request.Method == HttpMethod.Get) { Gets++; if (BeforeHealth is not null) await BeforeHealth(ct); return new(HttpStatusCode.OK) { Content = new StringContent(Health?.Invoke(Gets) ?? fixture.Health(), System.Text.Encoding.UTF8, "application/json") }; }
            if (request.RequestUri!.AbsolutePath is "/reserve" or "/settle")
            {
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); var body = document.RootElement;
                if (request.RequestUri.AbsolutePath == "/reserve") Reserves++; else Settles++;
                if (Control is not null) return await Control(request.RequestUri.AbsolutePath, body, ct);
                Sequence = Math.Max(Sequence, body.GetProperty("sequence").GetInt64());
                return ControlResponse(body, request.RequestUri.AbsolutePath == "/reserve" ? "reserved" : "settled");
            }
            Posts++; Bodies.Add(await request.Content!.ReadAsStringAsync(ct)); return await (Speech?.Invoke(Posts, ct) ?? Task.FromResult(fixture.Audio()));
        }
    }
    private sealed class FakeRuntime : ILocalSpeechRuntime
    {
        public const string Image = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        public JsonNode Container { get; } public List<string[]> Calls { get; } = []; public string Context { get; set; } = "npipe:////./pipe/dockerDesktopLinuxEngine";
        public int Starts { get; private set; } public int Launches { get; private set; } public int DaemonFailures { get; set; } public bool Missing { get; set; } public Action? OnStart { get; set; } public string? MetadataOverride { get; set; }
        public FakeRuntime(string engine)
        {
            Container = JsonSerializer.SerializeToNode(new
            {
                Name = "/commutecast-" + engine, Image,
                Config = new { Labels = new Dictionary<string, string> { ["com.commutecast.owner"] = "CommuteCast", ["com.docker.compose.project"] = "commutecast", ["com.commutecast.contract"] = "1" }, Cmd = new[] { "uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8765", "--no-access-log" }, Entrypoint = (string[]?)null, Env = new[] { "COMMUTECAST_ENGINE=" + engine } },
                HostConfig = new { PortBindings = new Dictionary<string, object> { ["8765/tcp"] = new[] { new { HostIp = "127.0.0.1", HostPort = engine == "kokoro" ? "8765" : "8766" } } }, Privileged = false, NetworkMode = "bridge", NanoCpus = 2_000_000_000L, Memory = (engine == "kokoro" ? 2L : 1L) * 1024 * 1024 * 1024, SecurityOpt = new[] { "no-new-privileges:true" } },
                Mounts = Array.Empty<object>(), State = new { Running = true, OOMKilled = false, Paused = false, Restarting = false }
            })!;
        }
        public Task<ProcessResult> DockerAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls.Add(arguments.ToArray());
            if (arguments[0] == "context") return Task.FromResult(new ProcessResult(0, Context, ""));
            if (arguments[0] == "version") return Task.FromResult(DaemonFailures-- > 0 ? new ProcessResult(1, "", "daemon unavailable") : new ProcessResult(0, "29.2.1", ""));
            if (arguments[0] == "start") { Starts++; Container["State"]!["Running"] = true; OnStart?.Invoke(); return Task.FromResult(new ProcessResult(0, arguments[1], "")); }
            return Task.FromResult(Missing ? new ProcessResult(1, "", "missing") : new ProcessResult(0, MetadataOverride ?? "[" + Container.ToJsonString() + "]", ""));
        }
        public void LaunchInstalledDesktop() => Launches++;
    }
    private class BlockedStream : Stream
    {
        public TaskCompletionSource<int> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => new(Release.Task);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class InterruptedStream : BlockedStream
    {
        private int reads;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (reads++ == 0) { buffer.Span[..100].Clear(); return ValueTask.FromResult(100); }
            return ValueTask.FromException<int>(new IOException("Private provider echo from disconnect"));
        }
    }
    private sealed class OversizedStream : BlockedStream
    {
        private long remaining = 64L * 1024 * 1024 + 1;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); var length = (int)Math.Min(buffer.Length, remaining); buffer.Span[..length].Clear(); remaining -= length; return ValueTask.FromResult(length);
        }
    }
}
