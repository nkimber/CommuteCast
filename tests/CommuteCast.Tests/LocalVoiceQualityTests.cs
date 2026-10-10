using System.Text.Json;
using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public class LocalVoiceQualityTests
{
    [Fact] public void RecipesRoundTripRemainFrozenAndAffectJobIdentity()
    {
        var options = new LocalVoiceOptions(BlendVoice: "af_bella", BlendWeight: .3, GainDb: -2);
        var settings = new AppSettings { LocalVoice = options }; var preferences = new NarrationPreferences(settings);
        var preset = new NarrationPresets(settings); preset.Save("My narrator", preferences.Current);
        var job = new Job { Settings = new("kokoro", "af_heart", 1, false, "", "fixture", LocalVoice: options) };
        var fingerprint = job.Fingerprint; settings.LocalVoice = options with { BlendWeight = .6 };
        Assert.Equal(options, preferences.ForPersistence().LocalVoice);
        var reopened = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(job))!;
        Assert.Equal(fingerprint, reopened.Fingerprint); Assert.Equal(options, reopened.Settings.LocalVoice);
        Assert.NotEqual(fingerprint, new Job { Settings = job.Settings with { LocalVoice = settings.LocalVoice } }.Fingerprint);
        Assert.Equal(options, preset.Items.Single(p => p.Name == "My narrator").Options.LocalVoice);
        preferences.SelectEngine("piper"); Assert.Equal("", settings.LocalVoice!.BlendVoice);
        preferences.SelectEngine("kokoro"); Assert.Equal(.6, settings.LocalVoice!.BlendWeight);
    }
    [Fact] public void LegacySnapshotsOmitNewFieldsAndRetainTheOriginalFingerprint()
    {
        var job = JsonSerializer.Deserialize<Job>("""{"Settings":{"Engine":"kokoro","Voice":"af_heart","Speed":1,"ExcludeCode":false,"Pronunciation":"","ProviderFingerprint":"fixture"},"Prepared":{"Script":"Hello.","Spans":[],"Version":"prepare-v1"}}""")!;
        var old = Job.Hash(JsonSerializer.Serialize(new { Settings = new { job.Settings.Engine, job.Settings.Voice, job.Settings.Speed, job.Settings.ExcludeCode, job.Settings.Pronunciation, job.Settings.ProviderFingerprint }, job.Prepared.Version, job.Prepared.Script, job.AudioContractVersion, job.ChunkingVersion }));
        Assert.Null(job.Settings.LocalVoice); Assert.Equal(old, job.Fingerprint);
        Assert.DoesNotContain("LocalVoice", JsonSerializer.Serialize(job.Settings));
        Assert.DoesNotContain("Pronunciation", JsonSerializer.Serialize(new PodcastSpeaker("Alex", "Host", "", "", "af_heart")));
    }
    [Theory] [InlineData("bf_emma")] [InlineData("af_heart")] [InlineData("unknown")]
    public void CrossAccentSameVoiceAndInvalidBlendsAreRejected(string blend) =>
        Assert.Throws<ArgumentException>(() => new LocalVoiceOptions(BlendVoice: blend).Validate("kokoro", "af_heart"));
    [Fact] public void UnsupportedOrNonfiniteControlsAreRejected()
    {
        foreach (var recipe in new[] { new LocalVoiceOptions(BlendWeight: double.NaN), new(NoiseScale: double.PositiveInfinity), new(GainDb: 1), new(SentencePauseMs: 1201), new(Version: 2) })
            Assert.Throws<ArgumentException>(() => recipe.Validate("piper", "en_US-lessac-medium"));
        Assert.Throws<ArgumentException>(() => new LocalVoiceOptions(BlendVoice: "af_bella").Validate("piper", "en_US-lessac-medium"));
        Assert.Throws<ArgumentException>(() => new LocalVoiceOptions(NoiseWidth: .5).Validate("kokoro", "af_heart"));
    }
    [Fact] public void OldServicesAndMissingBlendVoicesAreRejectedBeforeSynthesis()
    {
        var options = new LocalVoiceOptions(BlendVoice: "af_bella");
        var provider = new ProviderInfo("kokoro", "fixture", ["af_heart"], "ready", 0);
        Assert.Throws<IOException>(() => options.RequireAvailable(provider, "af_heart"));
        Assert.Throws<ArgumentException>(() => options.RequireAvailable(provider with { LocalVoiceContract = 1 }, "af_heart"));
        options.RequireAvailable(provider with { LocalVoiceContract = 1, Voices = ["af_heart", "af_bella"] }, "af_heart");
    }
    [Fact] public void NaturalPlanPreservesParagraphsAbbreviationsAndUnicode()
    {
        var script = "First paragraph.\n\n" + new string('x', 420) + " Dr. Smith studies the U.S. railway. " + new string('y', 420) + "?\n\n" + new string('z', 899) + "😀 End.";
        var chunks = NaturalChunker.Split(script); Assert.Equal("First paragraph.\n\n", chunks[0].Text);
        Assert.Equal(script, string.Concat(chunks.Select(c => c.Text)));
        Assert.DoesNotContain(chunks, c => c.Text.TrimEnd().EndsWith("Dr.") || c.Text.TrimEnd().EndsWith("U.S."));
        Assert.All(chunks, c => { Assert.InRange(c.Length, 1, 900); Assert.False(char.IsHighSurrogate(c.Text[^1])); Assert.False(char.IsLowSurrogate(c.Text[0])); });
        Assert.Equal(500, NaturalChunker.PauseMilliseconds(chunks[0], new()));
        Assert.Equal(220, NaturalChunker.PauseMilliseconds(new(0, 0, 6, "Why?\" ", false), new()));
        Assert.Equal(0, NaturalChunker.PauseMilliseconds(new(0, 0, 4, "and ", true), new()));
    }
    [Theory] [InlineData(0, 0, 5280)] [InlineData(100, 80, 960)] [InlineData(150, 100, 0)]
    public void JoinsTopUpExistingSilenceWithoutTrimmingSamples(int trailing, int leading, int expected)
    {
        using var test = new TestWorkspace(); var left = Path.Combine(test.Workspace.Root, "left.wav"); var right = Path.Combine(test.Workspace.Root, "right.wav");
        WriteWave(left, 0, trailing); WriteWave(right, leading, 0);
        Assert.Equal(expected, NaturalAudio.AddedGapSamples(left, right, 220));
    }
    internal static void WriteWave(string path, int leading = 0, int trailing = 0, double seconds = 1)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); using var file = File.Create(path);
        var count = (int)(24000 * seconds); WaveAudio.WriteHeader(file, count + (leading + trailing) * 24);
        file.Write(new byte[leading * 48]); using var writer = new BinaryWriter(file);
        for (var i = 0; i < count; i++) writer.Write((short)(6000 * Math.Sin(i * 2 * Math.PI * 220 / 24000)));
        file.Write(new byte[trailing * 48]);
    }
    [Theory] [InlineData(.1)] [InlineData(2)]
    public async Task MeasuredFinalNormalizationProducesValidatedMp3IncludingShortUtterances(double seconds)
    {
        using var test = new TestWorkspace(); var job = new Job { Prepared = new("Hello.", []), Settings = new("kokoro", "af_heart", 1, false, "", "fixture", LocalVoice: new()) };
        NaturalChunker.ConfigureNew(job); job.Chunks = NaturalChunker.Split(job.Prepared.Script);
        var directory = test.Workspace.JobDirectory(job.Id); var path = test.Workspace.ChunkPath(job, 0); WriteWave(path, seconds: seconds);
        job.Receipts.Add(new(0, await Workspace.HashFileAsync(path), job.Fingerprint, WaveAudio.Inspect(path).Duration));
        var pipeline = new AudioPipeline(new()); await pipeline.AssembleAsync(job, directory, default);
        var audio = await pipeline.ValidateFinalAsync(job, Path.Combine(directory, "complete.mp3"), default); Assert.InRange(audio.Duration, seconds, seconds + .1);
    }
    [Fact] public void SpeakerOverridesRemainIndependentAndAreAppliedOnce()
    {
        var settings = PodcastTests.Settings("kokoro") with { Pronunciation = "API=global api\nGPU=graphics chip", LocalVoice = new() };
        var episode = PodcastTests.Episode() with { Speakers = [new("Alex", "Host", "", "", "af_heart", LocalVoice: new(BlendVoice: "af_bella"), Pronunciation: "API=Alex api"), new("Casey", "Host", "", "", "am_adam", Pronunciation: "API=Casey api")] };
        var source = "Alex: API and GPU.\nCasey: API and GPU."; var prepared = PodcastScript.Prepare(source, episode, settings);
        Assert.Equal("Alex api and graphics chip.\nCasey api and graphics chip.\n", prepared.Prepared.Script);
        Assert.Equal("af_bella", PodcastScript.LocalDelivery(episode.Speakers[0], settings)!.BlendVoice);
        Assert.Equal("", PodcastScript.LocalDelivery(episode.Speakers[1], settings)!.BlendVoice);
        var job = new Job { Source = source, Settings = settings, Episode = episode, Prepared = prepared.Prepared, Chunks = prepared.Units };
        NaturalChunker.ConfigureNew(job); PodcastScript.ValidateManifest(job);
        Assert.True(NaturalChunker.IsNatural(job)); Assert.Equal(PodcastScript.NaturalAudioVersion, job.AudioContractVersion);
    }
    [Fact] public void LegacyPodcastFallbackManifestKeepsItsOriginalHardSplitFlags()
    {
        var settings = PodcastTests.Settings("kokoro");
        var source = "Alex: " + string.Join(' ', Enumerable.Repeat("long", 150)) + ".\nCasey: Yes.";
        var prepared = PodcastScript.Prepare(source, PodcastTests.Episode(), settings);
        Assert.True(prepared.Units.Count > 2);
        Assert.All(prepared.Units, unit => Assert.False(unit.HardSplit));
    }
}
