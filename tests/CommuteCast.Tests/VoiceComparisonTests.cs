using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public sealed class LocalComparisonFactAttribute : FactAttribute
{
    public LocalComparisonFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("COMMUTECAST_TEST_LOCAL_COMPARISON") != "1")
            Skip = "Opt-in installed local speech comparison: set COMMUTECAST_TEST_LOCAL_COMPARISON=1.";
    }
}

public class VoiceComparisonTests
{
    private static NarrationSettings Settings(string engine = "kokoro", bool natural = true) => new(engine,
        engine == "kokoro" ? "af_heart" : "en_US-lessac-medium", 1, false, "API=should not be applied twice", "",
        LocalVoice: new(NaturalPhrasing: natural));

    [LocalComparisonFact] public async Task InstalledKokoroAndPiperProduceProcessedComparisonSamples()
    {
        using var test = new TestWorkspace(); using var gate = new SemaphoreSlim(1);
        var pin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CommuteCast", "provider-lock.local.json");
        File.Copy(pin, Path.Combine(test.Workspace.Root, "provider-lock.local.json"));
        using var provider = new LocalSpeechProvider(test.Workspace);
        var generator = new AuditionGenerator(test.Workspace, provider, gate);
        const string passage = "At first, the answer seems obvious. A faster train should mean a shorter journey. But what happens when the station is farther from home? Let's follow one commuter through an ordinary morning.\n\nShe leaves at seven thirty, walks past the bakery, and reaches the platform with a minute to spare. The small details matter: a clear announcement, a comfortable seat, and a quiet moment before the working day begins. That is the difference between saving time and enjoying the journey.";
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        foreach (var engine in new[] { "kokoro", "piper" })
        {
            var before = await provider.ReadyAsync(engine, limit.Token, explicitRetry: true); Assert.Equal("ready", before.State); Assert.Equal(0, before.Active);
            var clip = await generator.GenerateComparisonAsync(passage, Settings(engine), "ffmpeg", limit.Token);
            var path = OwnedFileRemoval.Resolve(test.Workspace.Root, clip.RelativePath);
            var audio = WaveAudio.Inspect(path); Assert.InRange(audio.Duration, 10, 120);
            var evidence = Path.GetFullPath("artifacts/local-comparison-live"); Directory.CreateDirectory(evidence);
            File.Copy(path, Path.Combine(evidence, engine + "-comparison.wav"), true);
            await generator.RemoveAsync(clip);
            var after = await provider.ProbeAsync(engine, limit.Token); Assert.Equal(before.ImageId, after.ImageId); Assert.Equal(0, after.Active);
        }
        Assert.Empty(await new AuditionOwnershipStore(test.Workspace).LoadAsync());
    }

    [Theory] [InlineData("kokoro", true)] [InlineData("piper", true)] [InlineData("kokoro", false)]
    public async Task OwnPassageUsesProductionSegmentsAndKeepsOnlyOneRecoverablePrivateClip(string engine, bool natural)
    {
        using var test = new TestWorkspace(); using var gate = new SemaphoreSlim(1);
        var provider = new ToneProvider(); var generator = new AuditionGenerator(test.Workspace, provider, gate);
        var text = string.Join("\n\n", Enumerable.Repeat("API café. " + string.Join(" ", Enumerable.Repeat("Here is another complete sentence.", 12)), 3));
        var result = await generator.GenerateComparisonAsync(text, Settings(engine, natural), "ffmpeg", default);
        var expected = natural ? NaturalChunker.Split(text) : Chunker.Split(text, 450);
        Assert.Equal(expected.Select(chunk => chunk.Text), provider.Texts);
        Assert.Equal(text, result.Prepared.Script); Assert.DoesNotContain("should not", string.Concat(provider.Texts));
        Assert.Equal("current-model", result.Settings.ProviderFingerprint);
        var path = OwnedFileRemoval.Resolve(test.Workspace.Root, result.RelativePath);
        Assert.Equal(result.Hash, await Workspace.HashFileAsync(path));
        var audio = WaveAudio.Inspect(path); Assert.True(audio.Duration > 1); Assert.InRange(audio.Peak, .01, 1);
        Assert.Single(Directory.GetFiles(Path.Combine(test.Workspace.Root, "auditions")));
        Assert.Single(await new AuditionOwnershipStore(test.Workspace).LoadAsync());
        Assert.Empty(await new SqliteJobStore(test.Workspace).LoadAsync()); Assert.Empty(Directory.GetFiles(test.Destination));
        Assert.Equal(1, gate.CurrentCount);
        Assert.True(await generator.RemoveAsync(result)); Assert.Empty(await new AuditionOwnershipStore(test.Workspace).LoadAsync());
    }

    [Fact] public async Task ChangedModelBetweenSegmentsIsRefusedAndAllCompletedClipsAreCleaned()
    {
        using var test = new TestWorkspace(); using var gate = new SemaphoreSlim(1);
        var provider = new ToneProvider { ChangeModel = true }; var generator = new AuditionGenerator(test.Workspace, provider, gate);
        var error = await Assert.ThrowsAsync<IOException>(() => generator.GenerateComparisonAsync("First sentence.\n\nSecond sentence.", Settings(), "ffmpeg", default));
        Assert.Contains("model changed", error.Message);
        Assert.Empty(Directory.GetFiles(Path.Combine(test.Workspace.Root, "auditions")));
        Assert.Empty(await new AuditionOwnershipStore(test.Workspace).LoadAsync()); Assert.Equal(1, gate.CurrentCount);
    }

    [Fact] public async Task CancelledWaitDoesNotStartSpeechOrReleaseAnotherOwnersGate()
    {
        using var test = new TestWorkspace(); using var gate = new SemaphoreSlim(1); await gate.WaitAsync();
        var provider = new ToneProvider(); var generator = new AuditionGenerator(test.Workspace, provider, gate);
        using var stop = new CancellationTokenSource();
        var pending = generator.GenerateComparisonAsync("Hello.", Settings(), "ffmpeg", stop.Token);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(provider.Texts); Assert.Equal(0, gate.CurrentCount); gate.Release();
    }

    [Fact] public async Task CancellationDuringConversionCleansDurableClipsAndReleasesAdmission()
    {
        using var test = new TestWorkspace(); using var gate = new SemaphoreSlim(1); using var stop = new CancellationTokenSource();
        var generator = new AuditionGenerator(test.Workspace, new ToneProvider(), gate);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generator.GenerateComparisonAsync("One passage.\n\nAnother passage.", Settings(), "ffmpeg", stop.Token,
            notice => { if (notice.StartsWith("Joining")) stop.Cancel(); }));
        Assert.Equal(1, gate.CurrentCount); Assert.Empty(await new AuditionOwnershipStore(test.Workspace).LoadAsync());
        Assert.Empty(Directory.GetFiles(Path.Combine(test.Workspace.Root, "auditions")));
    }

    [Fact] public async Task RecoveryRemovesCompletedComparisonButPreservesUnrecordedFiles()
    {
        using var test = new TestWorkspace(); using var gate = new SemaphoreSlim(1);
        var generator = new AuditionGenerator(test.Workspace, new ToneProvider(), gate);
        var result = await generator.GenerateComparisonAsync("A short sample.", Settings(), "ffmpeg", default);
        var other = Path.Combine(test.Workspace.Root, "auditions", "other.wav"); await File.WriteAllTextAsync(other, "preserve");
        Assert.Equal(1, await generator.RecoverAsync()); Assert.Equal(0, await generator.RecoverAsync());
        Assert.False(File.Exists(OwnedFileRemoval.Resolve(test.Workspace.Root, result.RelativePath))); Assert.Equal("preserve", await File.ReadAllTextAsync(other));
    }

    [Theory] [InlineData("")] [InlineData(" ")]
    public async Task InvalidPassagesAreRefusedBeforeReadinessOrInference(string text)
    {
        using var test = new TestWorkspace(); using var gate = new SemaphoreSlim(1); var provider = new ToneProvider();
        await Assert.ThrowsAsync<ArgumentException>(() => new AuditionGenerator(test.Workspace, provider, gate).GenerateComparisonAsync(text, Settings(), "ffmpeg", default));
        Assert.Empty(provider.Texts); Assert.Equal(0, provider.ReadinessCalls);
    }

    [Fact] public async Task MalformedUnicodeIsRejectedBeforeInference()
    {
        using var test = new TestWorkspace(); using var gate = new SemaphoreSlim(1); var provider = new ToneProvider();
        await Assert.ThrowsAsync<ArgumentException>(() => new AuditionGenerator(test.Workspace, provider, gate)
            .GenerateComparisonAsync(((char)0xD800).ToString(), Settings(), "ffmpeg", default));
        Assert.Equal(0, provider.ReadinessCalls);
    }

    [Fact] public async Task ComparisonNeverSwitchesToHostedOrTruncatesLargePassages()
    {
        using var test = new TestWorkspace(); using var gate = new SemaphoreSlim(1); var provider = new ToneProvider();
        var generator = new AuditionGenerator(test.Workspace, provider, gate);
        await Assert.ThrowsAsync<ArgumentException>(() => generator.GenerateComparisonAsync(new string('a', 3601), Settings(), "ffmpeg", default));
        await Assert.ThrowsAsync<ArgumentException>(() => generator.GenerateComparisonAsync("Hi.", Settings() with { Engine = "openai" }, "ffmpeg", default));
        Assert.Equal(0, provider.ReadinessCalls);
    }

    private sealed class ToneProvider : ISpeechProvider
    {
        public List<string> Texts { get; } = [];
        public bool ChangeModel { get; init; }
        public int ReadinessCalls { get; private set; }
        public Task<ProviderInfo> ReadyAsync(string engine, CancellationToken ct)
        {
            ReadinessCalls++;
            return Task.FromResult(new ProviderInfo(engine, ChangeModel ? "model-" + ReadinessCalls : "current-model",
                engine == "kokoro" ? ["af_heart"] : ["en_US-lessac-medium"], "ready", 0, LocalVoiceContract: 1));
        }
        public Task SynthesizeAsync(NarrationSettings settings, string text, string output, CancellationToken ct)
        {
            Texts.Add(text); LocalVoiceQualityTests.WriteWave(output, leading: 100, trailing: 100, seconds: Math.Max(.4, NarrationEstimate.CountWords(text) * .12));
            return Task.CompletedTask;
        }
    }
}
