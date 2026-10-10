using System.Text;
using System.Text.Json;
using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public class NarrationToolsTests
{
    private static NarrationOptions Options(double speed = 1) => new("kokoro", "af_heart", speed, true, "API=A P I", new());
    [Fact]
    public void PresetsRoundTripWithoutChangingDefaultsOrFrozenJobSettings()
    {
        var settings = new AppSettings(); var preferences = new NarrationPreferences(settings); var presets = new NarrationPresets(settings);
        var frozen = new Job { Settings = new(settings.Engine, settings.Voice, settings.Speed, false, "", "original") };
        presets.Save(" My technical reading ", Options(1.2));
        Assert.True(preferences.UsingDefaults); Assert.Equal(1, frozen.Settings.Speed);
        var reopened = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(preferences.ForPersistence()))!;
        var saved = Assert.Single(new NarrationPresets(reopened).Items, p => p.Name == "My technical reading");
        Assert.Equal(Options(1.2), saved.Options);
        presets.Save("MY TECHNICAL READING", Options(.8)); Assert.Equal(4, presets.Items.Count);
        presets.Remove(presets.Items.Single(p => p.Name == "MY TECHNICAL READING"));
        Assert.Equal(3, presets.Items.Count);
    }
    [Fact]
    public void RemovingEveryPresetPersistsAnEmptyLibraryInsteadOfRecreatingExamples()
    {
        var settings = new AppSettings(); var presets = new NarrationPresets(settings);
        foreach (var preset in presets.Items.ToArray()) presets.Remove(preset);
        Assert.Empty(new NarrationPresets(JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!).Items);
        Assert.Throws<ArgumentException>(() => presets.Save("", Options()));
        Assert.Throws<ArgumentException>(() => presets.Save("bad\nname", Options()));
        Assert.Throws<ArgumentException>(() => presets.Save("invalid", Options(2)));
    }
    private static Job Sample(double wait = 2) => new()
    {
        Prepared = new(string.Join(" ", Enumerable.Repeat("word", 100)), []),
        Settings = new("kokoro", "af_heart", 1, false, "", "model"), Stage = JobStage.Exported, ExportCommitted = true,
        DurationSeconds = 60, Attempts = 1, RunTiming = new((long)(30 + wait) * 1000, null, 0), SpeechReadinessMilliseconds = (long)wait * 1000
    };
    private static ProviderInfo Provider => new("kokoro", "model", ["af_heart"], "ready", 0);
    [Fact]
    public void EstimatesRequireEnoughSuccessfulMatchingVoiceAndModelHistory()
    {
        var other = Sample(); other.Settings = other.Settings with { Voice = "am_michael" };
        var retried = Sample(); retried.Attempts = 2;
        var failed = Sample(); failed.Stage = JobStage.Failed;
        var changed = Sample(); changed.Settings = changed.Settings with { ProviderFingerprint = "new-model" };
        var estimate = NarrationEstimate.Calculate("one two three", Options(), Provider, [Sample(), Sample(), other, retried, failed, changed]);
        Assert.Equal(2, estimate.SampleCount); Assert.Null(estimate.Listening); Assert.Null(estimate.Generation);
    }
    [Fact]
    public void PaceAndFixedServiceWaitAreAccountedSeparatelyFromTextLength()
    {
        var estimate = NarrationEstimate.Calculate(string.Join(" ", Enumerable.Repeat("word", 200)), Options(), Provider, [Sample(), Sample(), Sample(20)]);
        Assert.Equal(96, estimate.Listening!.LowerSeconds); Assert.Equal(150, estimate.Listening.UpperSeconds);
        Assert.Equal(50, estimate.Generation!.LowerSeconds); Assert.Equal(100, estimate.Generation.UpperSeconds);
        var faster = NarrationEstimate.Calculate(string.Join(" ", Enumerable.Repeat("word", 200)), Options(1.25), Provider, [Sample(), Sample(), Sample(20)]);
        Assert.Equal(76.8, faster.Listening!.LowerSeconds, 3);
        Assert.Equal(85, faster.Generation!.UpperSeconds, 3); // fixed loading allowance is not divided by pace
    }
    [Fact]
    public void LegacyHistoryCalibratesListeningButDoesNotInventServiceTiming()
    {
        var history = Enumerable.Range(0, 3).Select(_ => Sample()).ToArray();
        foreach (var job in history) job.SpeechReadinessMilliseconds = null;
        var estimate = NarrationEstimate.Calculate("one two three", Options(), Provider, history);
        Assert.NotNull(estimate.Listening); Assert.Null(estimate.Generation);
        Assert.Equal(4, NarrationEstimate.CountWords("Don't lose Unicode: café."));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ImportPreservesUnicodeLineBreaksAndMarkdownExactly(bool utf16)
    {
        using var test = new TestWorkspace(); var path = Path.Combine(test.Workspace.Root, "source.MD");
        var source = "# Café 😀\r\nAPI reading.\n";
        await File.WriteAllTextAsync(path, source, utf16 ? new UnicodeEncoding(false, true, true) : new UTF8Encoding(true, true));
        Assert.Equal(source, await TextFileImport.ReadAsync(path));
    }
    [Fact]
    public async Task ImportRefusesInvalidOversizedAndBinaryFiles()
    {
        using var test = new TestWorkspace(); var path = Path.Combine(test.Workspace.Root, "source.txt");
        await File.WriteAllBytesAsync(path, [0xC3, 0x28]); await Assert.ThrowsAsync<ArgumentException>(() => TextFileImport.ReadAsync(path));
        await File.WriteAllTextAsync(path, "binary\0payload"); await Assert.ThrowsAsync<ArgumentException>(() => TextFileImport.ReadAsync(path));
        await File.WriteAllTextAsync(path, new string('a', TextPreparation.MaximumCharacters + 1)); await Assert.ThrowsAsync<ArgumentException>(() => TextFileImport.ReadAsync(path));
        await Assert.ThrowsAsync<ArgumentException>(() => TextFileImport.ReadAsync(Path.ChangeExtension(path, ".pdf")));
    }
}
