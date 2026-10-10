using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Text.Json;

namespace CommuteCast.Tests;

public partial class ProviderContractTests
{
    private const string RevisedImage = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static void WritePin(Fixture fixture, string image, int contract = 1) =>
        File.WriteAllText(Path.Combine(fixture.Test.Workspace.Root, "provider-lock.local.json"), JsonSerializer.Serialize(new { ImageId = image, Contract = contract }));

    [Theory] [InlineData("kokoro")] [InlineData("piper")]
    public async Task UnknownAndLegacyMetadataCanCaptureAReadyBusyModelWithoutWaitingForQuiescence(string engine)
    {
        using var fixture = new Fixture(engine); fixture.Http.Health = _ => fixture.Health("ready", 1);
        var legacy = new ProviderInfo(engine, engine + ":contract-v1:" + new string('b', 64), [fixture.Settings.Voice], "ready", 0);
        using var provider = fixture.Provider();
        foreach (var cached in new ProviderInfo?[] { null, legacy })
        {
            var captured = await provider.CaptureForSubmissionAsync(engine, cached).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(fixture.Fingerprint, captured.Fingerprint); Assert.Equal(FakeRuntime.Image, captured.ImageId); Assert.Equal(1, captured.Active);
        }
        Assert.Equal(0, fixture.Runtime.Starts); Assert.Equal(0, fixture.Runtime.Launches); Assert.Equal(0, fixture.Http.Posts);
        Assert.False(File.Exists(Path.Combine(fixture.Test.Workspace.Root, "recovery-" + engine + ".json")));
    }

    [Theory] [InlineData("kokoro")] [InlineData("piper")]
    public async Task MatchingPersistedImageMetadataAllowsOfflineQueueCaptureWithoutSpendingRecovery(string engine)
    {
        using var fixture = new Fixture(engine); using var provider = fixture.Provider();
        var cached = JsonSerializer.Deserialize<ProviderInfo>(JsonSerializer.Serialize(await provider.ProbeAsync(engine)))!;
        await new RecoveryBudget(fixture.Test.Workspace).BeginAsync(engine, default);
        var budget = Path.Combine(fixture.Test.Workspace.Root, "recovery-" + engine + ".json"); var before = await Workspace.HashFileAsync(budget);
        fixture.Runtime.Calls.Clear(); fixture.Http.Uris.Clear(); fixture.Runtime.DaemonFailures = 100; fixture.Runtime.Missing = true;
        var captured = await provider.CaptureForSubmissionAsync(engine, cached);
        Assert.Equal(cached.Fingerprint, captured.Fingerprint); Assert.Equal(cached.ImageId, captured.ImageId); Assert.NotSame(cached.Voices, captured.Voices);
        captured.Voices[0] = "changed-returned-array"; Assert.Equal(fixture.Settings.Voice, cached.Voices[0]);
        Assert.Empty(fixture.Runtime.Calls); Assert.Empty(fixture.Http.Uris); Assert.Equal(before, await Workspace.HashFileAsync(budget));
        Assert.Equal(0, fixture.Runtime.Starts); Assert.Equal(0, fixture.Runtime.Launches);
    }

    [Fact] public async Task CaptureDoesNotWaitForAConcurrentReadinessCheckToReleaseItsRecoveryGate()
    {
        using var fixture = new Fixture(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Http.Health = _ => { entered.TrySetResult(); return fixture.Health("ready", 1); };
        using var provider = fixture.Provider(); using var stopCheck = new CancellationTokenSource();
        var checking = provider.ReadyAsync("kokoro", stopCheck.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var budget = Path.Combine(fixture.Test.Workspace.Root, "recovery-kokoro.json"); var before = await Workspace.HashFileAsync(budget);
            var captured = await provider.CaptureForSubmissionAsync("kokoro", null);
            Assert.Equal(1, captured.Active); Assert.Equal(FakeRuntime.Image, captured.ImageId); Assert.False(checking.IsCompleted);
            Assert.Equal(before, await Workspace.HashFileAsync(budget)); Assert.Equal(0, fixture.Http.Posts);
        }
        finally { stopCheck.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checking); }
    }

    [Theory] [InlineData("kokoro")] [InlineData("piper")]
    public async Task ReprovisionRefreshesNewCaptureEvenWhenModelFingerprintIsUnchangedAndPreservesOldJob(string engine)
    {
        using var fixture = new Fixture(engine); using var provider = fixture.Provider(); var cached = await provider.ProbeAsync(engine);
        var old = new Job { Source = "Immutable original API source.", Prepared = TextPreparation.Prepare("Immutable original API source."),
            Settings = fixture.Settings with { ProviderImageId = cached.ImageId }, Destination = fixture.Test.Destination };
        old.Chunks = Chunker.Split(old.Prepared.Script, 450); old.Receipts.Add(new(0, "original-hash", old.Fingerprint, 1));
        var oldFingerprint = old.Fingerprint; var store = new SqliteJobStore(fixture.Test.Workspace); await store.SaveAsync(old);
        WritePin(fixture, RevisedImage); fixture.Runtime.Container["Image"] = RevisedImage;
        fixture.Http.Health = _ => fixture.Health("ready", 1);
        var captured = await provider.CaptureForSubmissionAsync(engine, cached);
        Assert.Equal(RevisedImage, captured.ImageId); Assert.Equal(cached.Fingerprint, captured.Fingerprint);
        var next = new Job { Source = old.Source, Prepared = old.Prepared, Settings = old.Settings with { ProviderImageId = captured.ImageId } };
        Assert.NotEqual(oldFingerprint, next.Fingerprint);
        var retained = Assert.Single(await store.LoadAsync()); Assert.Equal(oldFingerprint, retained.Fingerprint); Assert.Equal(old.Settings, retained.Settings); Assert.Equal(old.Receipts, retained.Receipts);
        Assert.Equal(FakeRuntime.Image, cached.ImageId); Assert.Equal(0, fixture.Http.Posts); Assert.Equal(0, fixture.Runtime.Starts);
    }

    [Theory] [InlineData("kokoro")] [InlineData("piper")]
    public async Task CachedMetadataForAnotherEngineNeverChangesTheCapturedEngine(string engine)
    {
        using var fixture = new Fixture(engine); using var provider = fixture.Provider();
        var other = engine == "kokoro" ? "piper" : "kokoro";
        var cached = new ProviderInfo(other, other + ":contract-v1:" + new string('c', 64), ["other_voice"], "ready", 0, FakeRuntime.Image);
        var captured = await provider.CaptureForSubmissionAsync(engine, cached);
        Assert.Equal(engine, captured.Engine); Assert.Equal(fixture.Settings.Voice, Assert.Single(captured.Voices));
        Assert.All(fixture.Http.Uris, uri => Assert.Equal(engine == "kokoro" ? 8765 : 8766, uri.Port));
        Assert.Contains(fixture.Runtime.Calls, call => call.SequenceEqual(new[] { "inspect", "commutecast-" + engine })); Assert.Equal(0, fixture.Http.Posts);
    }

    [Fact] public async Task PinChangingDuringHealthCaptureRefusesSnapshotWithoutSendingSourceOrStartingServices()
    {
        using var fixture = new Fixture(); fixture.Http.Health = _ => { WritePin(fixture, RevisedImage); return fixture.Health(); };
        using var provider = fixture.Provider(); var error = await Assert.ThrowsAsync<IOException>(() => provider.CaptureForSubmissionAsync("kokoro", null));
        Assert.Contains("pin changed", error.Message); Assert.Equal(0, fixture.Http.Posts); Assert.Equal(0, fixture.Runtime.Starts); Assert.Equal(0, fixture.Runtime.Launches);
    }

    [Fact] public async Task PinChangingDuringOwnedStartRefusesMetadataBeforeHealthOrSpeech()
    {
        using var fixture = new Fixture(); fixture.Runtime.Container["State"]!["Running"] = false;
        fixture.Runtime.OnStart = () => WritePin(fixture, RevisedImage);
        using var provider = fixture.Provider(); await Assert.ThrowsAsync<IOException>(() => provider.CaptureForSubmissionAsync("kokoro", null));
        Assert.Equal(1, fixture.Runtime.Starts); Assert.Empty(fixture.Http.Uris); Assert.Equal(0, fixture.Http.Posts);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task UnknownMetadataForAnOwnedStoppedServiceUsesBoundedReadiness(bool daemonUnavailable)
    {
        using var fixture = new Fixture(); fixture.Runtime.Container["State"]!["Running"] = false;
        if (daemonUnavailable) fixture.Runtime.DaemonFailures = 2;
        using var provider = fixture.Provider(); var captured = await provider.CaptureForSubmissionAsync("kokoro", null);
        Assert.Equal(FakeRuntime.Image, captured.ImageId); Assert.Equal(fixture.Fingerprint, captured.Fingerprint); Assert.Equal(1, fixture.Runtime.Starts);
        Assert.Equal(daemonUnavailable ? 1 : 0, fixture.Runtime.Launches); Assert.Equal(0, fixture.Http.Posts);
    }

    [Theory] [InlineData("state")] [InlineData("fingerprint")] [InlineData("voices")] [InlineData("active")]
    public async Task InvalidCachedMetadataCannotBypassFreshVerification(string defect)
    {
        using var fixture = new Fixture(); using var provider = fixture.Provider(); var cached = await provider.ProbeAsync("kokoro");
        cached = defect switch { "state" => cached with { State = "loading" }, "fingerprint" => cached with { Fingerprint = "incompatible" },
            "voices" => cached with { Voices = [] }, _ => cached with { Active = 2 } };
        var before = fixture.Http.Gets; var captured = await provider.CaptureForSubmissionAsync("kokoro", cached);
        Assert.Equal(before + 1, fixture.Http.Gets); Assert.Equal("ready", captured.State); Assert.Equal(fixture.Fingerprint, captured.Fingerprint);
        Assert.Equal(fixture.Settings.Voice, Assert.Single(captured.Voices)); Assert.Equal(0, captured.Active); Assert.Equal(0, fixture.Http.Posts);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ForeignOrRemoteIdentityFailsCaptureWithoutRecoveryOrHttp(bool remote)
    {
        using var fixture = new Fixture();
        if (remote) fixture.Runtime.Context = "tcp://foreign-host:2375"; else fixture.Runtime.Container["Config"]!["Labels"]!["com.commutecast.owner"] = "foreign";
        using var provider = fixture.Provider(); await Assert.ThrowsAnyAsync<IOException>(() => provider.CaptureForSubmissionAsync("kokoro", null));
        Assert.Equal(0, fixture.Runtime.Starts); Assert.Equal(0, fixture.Runtime.Launches); Assert.Empty(fixture.Http.Uris);
        Assert.False(File.Exists(Path.Combine(fixture.Test.Workspace.Root, "recovery-kokoro.json")));
    }

    [Theory] [InlineData("missing")] [InlineData("contract")] [InlineData("invalid")] [InlineData("oversized")]
    public async Task AValidLookingCacheCannotBypassMissingOrIncompatiblePin(string defect)
    {
        using var fixture = new Fixture(); using var provider = fixture.Provider(); var cached = await provider.ProbeAsync("kokoro");
        var path = Path.Combine(fixture.Test.Workspace.Root, "provider-lock.local.json");
        if (defect == "missing") File.Delete(path); else if (defect == "contract") WritePin(fixture, FakeRuntime.Image, 2);
        else if (defect == "invalid") File.WriteAllText(path, "private invalid pin content"); else File.WriteAllText(path, new string('x', 65537));
        fixture.Runtime.Calls.Clear(); fixture.Http.Uris.Clear();
        var error = await Assert.ThrowsAnyAsync<IOException>(() => provider.CaptureForSubmissionAsync("kokoro", cached));
        Assert.DoesNotContain("private invalid pin content", error.Message); Assert.Empty(fixture.Runtime.Calls); Assert.Empty(fixture.Http.Uris);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CaptureHonorsCallerCancellationAndItsOverallDeadline(bool callerCancellation)
    {
        using var fixture = new Fixture(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Http.BeforeHealth = async ct => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); };
        using var provider = fixture.Provider(Fixture.ShortLimits with { Readiness = TimeSpan.FromMilliseconds(500) });
        using var cancellation = new CancellationTokenSource(); var pending = provider.CaptureForSubmissionAsync("kokoro", null, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (callerCancellation) { cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); }
        else await Assert.ThrowsAsync<TimeoutException>(() => pending);
        Assert.Equal(0, fixture.Http.Posts); Assert.Equal(0, fixture.Runtime.Starts);
    }

    [Theory] [InlineData("kokoro")] [InlineData("piper")]
    public async Task SameModelOnAnotherImageCannotSynthesizeAnOldFrozenJob(string engine)
    {
        using var fixture = new Fixture(engine); using var provider = fixture.Provider();
        await File.WriteAllBytesAsync(fixture.Output, fixture.Wave); var before = await Workspace.HashFileAsync(fixture.Output);
        await Assert.ThrowsAsync<IOException>(() => provider.SynthesizeAsync(fixture.Settings with { ProviderImageId = RevisedImage }, "source", fixture.Output, default));
        Assert.Empty(fixture.Http.Uris); Assert.Equal(0, fixture.Http.Posts); Assert.Equal(before, await Workspace.HashFileAsync(fixture.Output));
    }

    [Fact] public async Task PinChangingWhileSynthesisWaitsForBusyServicePreventsSourcePost()
    {
        using var fixture = new Fixture(); fixture.Http.Health = index => { if (index == 1) return fixture.Health("ready", 1); WritePin(fixture, RevisedImage); return fixture.Health(); };
        using var provider = fixture.Provider(); await Assert.ThrowsAsync<IOException>(() => provider.SynthesizeAsync(fixture.Settings with { ProviderImageId = FakeRuntime.Image }, "source", fixture.Output, default));
        Assert.Equal(0, fixture.Http.Posts); Assert.False(File.Exists(fixture.Output));
    }

    [Fact] public async Task QueueRejectsAChangedImageBeforeSynthesisEvenWhenModelFingerprintMatches()
    {
        using var fixture = new Fixture(); using var provider = fixture.Provider(); var store = new SqliteJobStore(fixture.Test.Workspace);
        await using var queue = new QueueCoordinator(fixture.Test.Workspace, store, provider, new(new()), new(fixture.Test.Workspace, store));
        var job = new Job { Source = "Original source.", Prepared = TextPreparation.Prepare("Original source."),
            Settings = fixture.Settings with { ProviderImageId = RevisedImage }, Destination = fixture.Test.Destination };
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Changed += jobs => { if (jobs.Any(j => j.Id == job.Id && j.Stage == JobStage.Failed)) failed.TrySetResult(); };
        await queue.InitializeAsync(); await queue.AddAsync(job); await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var retained = Assert.Single(await store.LoadAsync()); Assert.Equal(JobStage.Failed, retained.Stage); Assert.Equal(job.Source, retained.Source);
        Assert.Equal(RevisedImage, retained.Settings.ProviderImageId); Assert.Empty(retained.Receipts); Assert.Equal(0, fixture.Http.Posts);
    }

    [Theory] [InlineData("")] [InlineData("sha256:invalid")] [InlineData("foreign-path")]
    public async Task InvalidCapturedImageIsRejectedBeforeRuntimeOrDurableAdmission(string image)
    {
        using var fixture = new Fixture(); using var provider = fixture.Provider(); var settings = fixture.Settings with { ProviderImageId = image };
        await Assert.ThrowsAsync<ArgumentException>(() => provider.SynthesizeAsync(settings, "source", fixture.Output, default));
        var store = new SqliteJobStore(fixture.Test.Workspace);
        await using var queue = new QueueCoordinator(fixture.Test.Workspace, store, provider, new(new()), new(fixture.Test.Workspace, store)) { Paused = true };
        await queue.InitializeAsync(); await Assert.ThrowsAsync<ArgumentException>(() => queue.AddAsync(new Job { Source = "source", Prepared = TextPreparation.Prepare("source"), Settings = settings }));
        Assert.Empty(await store.LoadAsync()); Assert.Empty(fixture.Runtime.Calls); Assert.Empty(fixture.Http.Uris);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void AddingOptionalImageIdentityPreservesAllExistingFingerprintBytes(bool profile)
    {
        using var fixture = new Fixture(); var settings = fixture.Settings with { Profile = profile ? new() : null };
        var job = new Job { Prepared = TextPreparation.Prepare("API 24", profile: settings.Profile), Settings = settings };
        object previous = profile ? new { settings.Engine, settings.Voice, settings.Speed, settings.ExcludeCode, settings.Pronunciation, settings.ProviderFingerprint, settings.Profile }
            : new { settings.Engine, settings.Voice, settings.Speed, settings.ExcludeCode, settings.Pronunciation, settings.ProviderFingerprint };
        var expected = Job.Hash(JsonSerializer.Serialize(new { Settings = previous, job.Prepared.Version, job.Prepared.Script, job.AudioContractVersion, job.ChunkingVersion }));
        Assert.Equal(expected, job.Fingerprint); Assert.DoesNotContain("ProviderImageId", JsonSerializer.Serialize(settings));
        job.Settings = settings with { ProviderImageId = FakeRuntime.Image }; var pinned = job.Fingerprint; Assert.NotEqual(expected, pinned);
        job.Settings = job.Settings with { ProviderImageId = RevisedImage }; Assert.NotEqual(pinned, job.Fingerprint);
    }

    [Fact] public async Task OfflineBackupRestoreRetainsBoundMetadataAndImageBearingReceipts()
    {
        using var fixture = new Fixture(); using var target = new TestWorkspace(); using var provider = fixture.Provider();
        var info = await provider.ProbeAsync("kokoro"); var settings = new AppSettings(); settings.Providers["kokoro"] = info;
        await fixture.Test.Workspace.SaveSettingsAsync(settings);
        var job = new Job { Source = "Frozen image source.", Prepared = TextPreparation.Prepare("Frozen image source."), Settings = fixture.Settings with { ProviderImageId = info.ImageId } };
        job.Chunks = Chunker.Split(job.Prepared.Script, 450); job.Receipts.Add(new(0, "frozen-hash", job.Fingerprint, 1));
        await new SqliteJobStore(fixture.Test.Workspace).SaveAsync(job);
        using var sourceLease = WorkspaceLease.Acquire(fixture.Test.Workspace); var backup = await WorkspaceBackup.CreateAsync(sourceLease);
        using var targetLease = WorkspaceLease.Acquire(target.Workspace); await WorkspaceBackup.RestoreAsync(targetLease, backup);
        var restored = Assert.Single(await new SqliteJobStore(target.Workspace).LoadAsync()); var restoredSettings = await target.Workspace.LoadSettingsAsync();
        Assert.Equal(info.ImageId, restoredSettings.Providers["kokoro"].ImageId); Assert.Equal(job.Settings, restored.Settings);
        Assert.Equal(job.Fingerprint, restored.Fingerprint); Assert.Equal(job.Receipts, restored.Receipts);
    }
}
