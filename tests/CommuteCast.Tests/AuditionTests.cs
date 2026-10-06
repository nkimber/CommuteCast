using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public class AuditionTests
{
    private static NarrationSettings Settings(string engine = "kokoro", bool excludeCode = false) => new(engine,
        engine == "kokoro" ? "af_heart" : "en_US-lessac-medium", 1, excludeCode, "API=application interface", "old-model", new(), "sha256:" + new string('b', 64));
    private static AuditionRequest Excerpt(string source, NarrationSettings? settings = null) => AuditionRequest.Selection(source, 0, source.Length, settings ?? Settings());

    [Theory] [InlineData("kokoro")] [InlineData("piper")]
    public void SelectionAndPreferencesRemainFrozenAfterEditorChanges(string engine)
    {
        var source = "Before. API café 😀 24. After."; var settings = Settings(engine);
        var start = source.IndexOf("API", StringComparison.Ordinal); const string clip = "API café 😀 24.";
        var request = AuditionRequest.Selection(source, start, clip.Length, settings);
        source = "A replacement editor draft."; settings = settings with { Engine = "other", Voice = "other", Speed = 1.4, Pronunciation = "API=changed" };
        var prepared = request.Prepare();
        Assert.Equal(clip, request.Source); Assert.Equal(start, request.SelectionStart); Assert.Equal(engine, request.Settings.Engine);
        Assert.Equal(1, request.Settings.Speed); Assert.Equal("", request.Settings.ProviderFingerprint); Assert.Null(request.Settings.ProviderImageId);
        Assert.Equal("application interface café 😀 24.", prepared.Script);
        Assert.Equal(clip, string.Concat(prepared.Spans.Select(s => s.Original))); Assert.Equal(clip.Length, prepared.Spans.Sum(s => s.Length));
    }
    [Fact] public void StandardSampleIsDistinctFromTheUserSelectionAndUsesTheCapturedProfile()
    {
        var settings = Settings() with { Pronunciation = "", Profile = new(Numbers: NumberReading.ScientificWords, Acronyms: AcronymReading.SpellUppercaseWords) };
        var request = AuditionRequest.Standard(settings); var prepared = request.Prepare();
        Assert.Null(request.SelectionStart); Assert.Equal(AuditionRequest.StandardSample, request.Source);
        Assert.Contains("A P I", prepared.Script); Assert.Contains("times ten to the power of minus three", prepared.Script);
        Assert.True(prepared.Script.Length <= AuditionRequest.MaximumCharacters); Assert.Equal(settings.Profile, prepared.ProfileReview!.Profile);
    }
    [Theory] [InlineData(-1, 1)] [InlineData(0, 0)] [InlineData(3, 1)] [InlineData(0, 4)] [InlineData(int.MaxValue, 1)] [InlineData(1, int.MaxValue)]
    public void InvalidSelectionRangesNeverFallBackToTheStandardSample(int start, int length) =>
        Assert.Throws<ArgumentException>(() => AuditionRequest.Selection("abc", start, length, Settings()));
    [Theory] [InlineData(1, 1)] [InlineData(2, 1)]
    public void HalfOfASurrogatePairIsRefused(int start, int length) =>
        Assert.Throws<ArgumentException>(() => AuditionRequest.Selection("A😀B", start, length, Settings()));
    [Fact] public void AWholeUnicodeCharacterIsKept() => Assert.Equal("😀", AuditionRequest.Selection("A😀B", 1, 2, Settings()).Prepare().Script);
    [Theory] [InlineData(0xD800)] [InlineData(0xDC00)]
    public void MalformedUnicodeIsRefused(int codeUnit) => Assert.Throws<ArgumentException>(() => Excerpt(((char)codeUnit).ToString()));
    [Fact] public void EmptyAndOversizedInputsAreRefusedWithoutTruncation()
    {
        Assert.Throws<ArgumentException>(() => Excerpt(" \r\n "));
        Assert.Throws<ArgumentException>(() => Excerpt(new string('x', 901)));
        Assert.Throws<ArgumentException>(() => AuditionRequest.Selection(new string('x', TextPreparation.MaximumCharacters + 1), 0, 10, Settings()));
        Assert.Equal(new string('x', 900), Excerpt(new string('x', 900)).Prepare().Script);
    }
    [Fact] public void DictionaryExpansionIsBoundedAfterPreparation()
    {
        var request = Excerpt("API", Settings() with { Pronunciation = "API=" + new string('x', 901) });
        var error = Assert.Throws<ArgumentException>(() => request.Prepare()); Assert.Contains("Nothing was truncated", error.Message);
    }
    [Theory] [InlineData(.69)] [InlineData(1.41)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void UnsupportedPacesAreRefused(double speed) => Assert.Throws<ArgumentException>(() => AuditionRequest.Standard(Settings() with { Speed = speed }));
    [Fact] public void UnsupportedEngineVoiceProfileAndDictionaryAreRefusedBeforePreparation()
    {
        Assert.Throws<ArgumentException>(() => AuditionRequest.Standard(Settings() with { Engine = "unknown" }));
        Assert.Throws<ArgumentException>(() => AuditionRequest.Standard(Settings() with { Voice = " " }));
        Assert.Throws<ArgumentException>(() => AuditionRequest.Standard(Settings() with { Profile = new(Language: "fr") }));
        Assert.Throws<ArgumentException>(() => AuditionRequest.Standard(Settings() with { Pronunciation = "not a dictionary rule" }));
    }
    [Theory] [InlineData("```")] [InlineData("~~~")]
    public void CompletePassageHonorsExclusionWhileACroppedCodeBodyIsRefused(string fence)
    {
        var source = $"Read this.\n{fence}text\nsecret payload\n{fence}\nEnd.";
        var excluded = Settings(excludeCode: true);
        Assert.DoesNotContain("secret", Excerpt(source, excluded).Prepare().Script);
        var start = source.IndexOf("secret", StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => AuditionRequest.Selection(source, start, 6, excluded).Prepare());
        Assert.Equal("secret", AuditionRequest.Selection(source, start, 6, Settings()).Prepare().Script);
        Assert.Throws<ArgumentException>(() => AuditionRequest.Selection(source, source.IndexOf(fence, StringComparison.Ordinal) + 1, 4, excluded).Prepare());
        Assert.Throws<ArgumentException>(() => AuditionRequest.Selection(source, source.LastIndexOf(fence, StringComparison.Ordinal), fence.Length + 5, excluded).Prepare());
    }
    [Fact] public void AlreadyCancelledPreparationStopsBeforeAnyWork()
    { using var stop = new CancellationTokenSource(); stop.Cancel(); Assert.ThrowsAny<OperationCanceledException>(() => AuditionRequest.Standard(Settings()).Prepare(stop.Token)); }

    [Theory] [InlineData("kokoro")] [InlineData("piper")]
    public async Task GenerationBindsLiveModelAndImageWithoutQueueExportOrLegacySampleReplacement(string engine)
    {
        using var test = new TestWorkspace(); using var gate = new SemaphoreSlim(1); var provider = new ToneProvider();
        var legacy = Path.Combine(test.Workspace.Root, "audition.wav"); await File.WriteAllTextAsync(legacy, "Preserve untracked legacy sample");
        var generator = new AuditionGenerator(test.Workspace, provider, gate); var request = Excerpt("API café 24.", Settings(engine));
        var audio = await generator.GenerateAsync(request, default);
        Assert.Equal(engine, audio.Settings.Engine); Assert.Equal(ToneProvider.Image, audio.Settings.ProviderImageId); Assert.Equal("current-model", audio.Settings.ProviderFingerprint);
        Assert.Equal(audio.Settings, provider.ReceivedSettings); Assert.Equal("application interface café 24.", provider.ReceivedText);
        Assert.Equal(audio.Hash, await Workspace.HashFileAsync(OwnedFileRemoval.Resolve(test.Workspace.Root, audio.RelativePath)));
        Assert.False(File.Exists(Path.Combine(test.Workspace.Root, "queue.db"))); Assert.Empty(Directory.GetFiles(test.Destination)); Assert.Equal(1, gate.CurrentCount);
        Assert.True(await generator.RemoveAsync(audio)); Assert.False(await generator.RemoveAsync(audio)); Assert.Equal("Preserve untracked legacy sample", await File.ReadAllTextAsync(legacy));
    }
    [Fact] public async Task CancelledGateWaitCannotInspectOrStartSpeechAndDoesNotReleaseTheOtherOwner()
    {
        using var test = new TestWorkspace(); using var gate = new SemaphoreSlim(1); await gate.WaitAsync();
        var provider = new ToneProvider(); var generator = new AuditionGenerator(test.Workspace, provider, gate); using var stop = new CancellationTokenSource();
        var pending = generator.GenerateAsync(Excerpt("Selected passage."), stop.Token);
        await Task.Delay(150); Assert.False(pending.IsCompleted); Assert.Equal(0, provider.ReadinessCalls); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.Equal(0, gate.CurrentCount); Assert.Equal(0, provider.ReadinessCalls);
        Assert.False(Directory.Exists(Path.Combine(test.Workspace.Root, "auditions"))); gate.Release();
    }
    [Fact] public async Task CancelledLateResponseStaysFencedUntilTheProviderReturnsAndCannotBecomePlayable()
    {
        using var test = new TestWorkspace(); using var gate = new SemaphoreSlim(1); var provider = new LateProvider();
        var generator = new AuditionGenerator(test.Workspace, provider, gate); using var stop = new CancellationTokenSource();
        var pending = generator.GenerateAsync(Excerpt("Stop this synthetic audition."), stop.Token);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); stop.Cancel(); await provider.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var nextOwner = gate.WaitAsync(); await Task.Delay(150); Assert.False(nextOwner.IsCompleted); Assert.False(pending.IsCompleted);
        provider.Release.TrySetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await nextOwner.WaitAsync(TimeSpan.FromSeconds(10)); gate.Release();
        Assert.Empty(Directory.GetFiles(Path.Combine(test.Workspace.Root, "auditions"))); Assert.Empty(Directory.GetFiles(test.Destination));
    }
    [Theory] [InlineData("engine")] [InlineData("voice")] [InlineData("busy")] [InlineData("loading")] [InlineData("image")]
    public async Task IncompatibleReadinessCannotReceiveSelectionText(string defect)
    {
        using var test = new TestWorkspace(); using var gate = new SemaphoreSlim(1); var provider = new ToneProvider();
        var info = provider.Info("kokoro");
        info = defect switch { "engine" => info with { Engine = "piper" }, "voice" => info with { Voices = ["different"] }, "busy" => info with { Active = 1 }, "loading" => info with { State = "loading" }, _ => info with { ImageId = "sha256:invalid" } };
        var generator = new AuditionGenerator(test.Workspace, provider, gate, (_, _) => Task.FromResult(info));
        var failure = await Record.ExceptionAsync(() => generator.GenerateAsync(Excerpt("Do not send this selection."), default));
        Assert.True(failure is IOException or ArgumentException); Assert.Null(provider.ReceivedText); Assert.Equal(1, gate.CurrentCount);
        Assert.False(Directory.Exists(Path.Combine(test.Workspace.Root, "auditions")));
    }
    [Fact] public async Task ConcurrentAuditionsShareAdmissionAndKeepDistinctCapturedSelections()
    {
        using var test = new TestWorkspace(); using var gate = new SemaphoreSlim(1); var provider = new LateProvider();
        var generator = new AuditionGenerator(test.Workspace, provider, gate);
        var first = generator.GenerateAsync(Excerpt("First selected passage."), default);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = generator.GenerateAsync(Excerpt("Second selected passage."), default);
        await Task.Delay(150); Assert.Equal(1, provider.ReadinessCalls); Assert.False(second.IsCompleted);
        provider.Release.TrySetResult();
        var clips = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, provider.ReadinessCalls); Assert.NotEqual(clips[0].RelativePath, clips[1].RelativePath);
        Assert.Equal("First selected passage.", clips[0].Prepared.Script); Assert.Equal("Second selected passage.", clips[1].Prepared.Script);
        foreach (var clip in clips) await generator.RemoveAsync(clip); Assert.Equal(1, gate.CurrentCount);
    }
    [Fact] public async Task MalformedProviderAudioCannotReturnAPlayableReceiptOrEraseUncertainBytes()
    {
        using var test = new TestWorkspace(); using var gate = new SemaphoreSlim(1);
        var generator = new AuditionGenerator(test.Workspace, new MalformedProvider(), gate);
        await Assert.ThrowsAsync<IOException>(() => generator.GenerateAsync(Excerpt("Malformed audio fixture."), default));
        Assert.Equal(1, gate.CurrentCount);
        var unknown = Assert.Single(Directory.GetFiles(Path.Combine(test.Workspace.Root, "auditions")));
        Assert.Equal("Invalid incomplete provider output", await File.ReadAllTextAsync(unknown));
    }
    [Fact] public async Task ChangedCompletedAuditionAndUnknownSiblingArePreserved()
    {
        using var test = new TestWorkspace(); using var gate = new SemaphoreSlim(1); var generator = new AuditionGenerator(test.Workspace, new ToneProvider(), gate);
        var audio = await generator.GenerateAsync(Excerpt("A short completed sample."), default); var path = OwnedFileRemoval.Resolve(test.Workspace.Root, audio.RelativePath);
        var sibling = Path.Combine(Path.GetDirectoryName(path)!, "untracked.wav"); await File.WriteAllTextAsync(sibling, "Untracked"); await File.WriteAllTextAsync(path, "Changed");
        await Assert.ThrowsAsync<IOException>(() => generator.RemoveAsync(audio)); Assert.Equal("Changed", await File.ReadAllTextAsync(path)); Assert.Equal("Untracked", await File.ReadAllTextAsync(sibling));
    }
    [Fact] public async Task NativePiperPcmRateDoesNotRequireFfmpegNormalizationForAudition()
    {
        using var test = new TestWorkspace(); using var gate = new SemaphoreSlim(1); var provider = new ToneProvider { NativeRate = 22050 };
        var generator = new AuditionGenerator(test.Workspace, provider, gate); var audio = await generator.GenerateAsync(Excerpt("Native PCM sample.", Settings("piper")), default);
        var path = OwnedFileRemoval.Resolve(test.Workspace.Root, audio.RelativePath); WaveAudio.DataRegion(path, false);
        await generator.RemoveAsync(audio);
    }
    private class ToneProvider : ISpeechProvider
    {
        public const string Image = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        public int ReadinessCalls; public NarrationSettings? ReceivedSettings; public string? ReceivedText; public int NativeRate = 24000;
        public ProviderInfo Info(string engine) => new(engine, "current-model", [engine == "kokoro" ? "af_heart" : "en_US-lessac-medium"], "ready", 0, Image);
        public Task<ProviderInfo> ReadyAsync(string engine, CancellationToken ct) { ct.ThrowIfCancellationRequested(); ReadinessCalls++; return Task.FromResult(Info(engine)); }
        public virtual Task SynthesizeAsync(NarrationSettings settings, string text, string output, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); ReceivedSettings = settings; ReceivedText = text; TestWorkspace.WriteWave(output);
            if (NativeRate != 24000)
            { using var file = File.OpenWrite(output); using var writer = new BinaryWriter(file); file.Position = 24; writer.Write(NativeRate); writer.Write(NativeRate * 2); }
            return Task.CompletedTask;
        }
    }
    private sealed class LateProvider : ToneProvider
    {
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task SynthesizeAsync(NarrationSettings settings, string text, string output, CancellationToken ct)
        { using var registration = ct.Register(() => Cancelled.TrySetResult()); Entered.TrySetResult(); await Release.Task; TestWorkspace.WriteWave(output); }
    }
    private sealed class MalformedProvider : ToneProvider
    {
        public override Task SynthesizeAsync(NarrationSettings settings, string text, string output, CancellationToken ct) => File.WriteAllTextAsync(output, "Invalid incomplete provider output", ct);
    }
}
