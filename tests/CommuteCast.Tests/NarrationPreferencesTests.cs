using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Text.Json;

namespace CommuteCast.Tests;

public class NarrationPreferencesTests
{
    [Fact] public async Task HostedRecipesRestoreModelDeliveryAndEmotionAcrossPresetsAndRelaunch()
    {
        using var test = new TestWorkspace();
        var settings = new AppSettings { Engine = "cartesia", Voice = "voice-a" };
        var first = new SpeechConfiguration("sonic-3", Emotion: "calm", Dialogue: false);
        settings.SpeechDefaults["cartesia"] = first;
        var preferences = new NarrationPreferences(settings);
        var presets = new NarrationPresets(settings);
        presets.Save("Calm", preferences.Current);
        settings.SpeechDefaults["cartesia"] = new("sonic-3.5", Emotion: "curious");
        settings.Voice = "voice-b";
        presets.Save("Curious", preferences.Current);
        Assert.False(preferences.UsingDefaults);
        preferences.UseDefaults();
        Assert.Equal(first, settings.SpeechDefaults["cartesia"]);
        Assert.Equal("voice-a", settings.Voice);
        presets.Items.Single(p => p.Name == "Curious").Options.ApplyTo(settings);
        Assert.Equal("sonic-3.5", settings.SpeechDefaults["cartesia"].Model);
        Assert.Equal("curious", settings.SpeechDefaults["cartesia"].Emotion);
        await test.Workspace.SaveSettingsAsync(preferences.ForPersistence());
        var reopened = await test.Workspace.LoadSettingsAsync();
        var restored = new NarrationPreferences(reopened);
        Assert.True(restored.UsingDefaults);
        Assert.Equal(first, reopened.SpeechDefaults["cartesia"]);
        reopened.NarrationPresets!.Single(p => p.Name == "Curious").Options.ApplyTo(reopened);
        Assert.Equal("voice-b", reopened.Voice);
        Assert.Equal("curious", reopened.SpeechDefaults["cartesia"].Emotion);
        Assert.False(restored.UsingDefaults);
    }

    [Fact] public void HostedDeliveryChangesAffectDefaultComparisonAndAreRestored()
    {
        var settings = new AppSettings { Engine = "openai", Voice = "cedar" };
        settings.SpeechDefaults["openai"] = new("gpt-4o-mini-tts", "Warm conversational explanation.");
        var preferences = new NarrationPreferences(settings);
        settings.SpeechDefaults["openai"] = new("gpt-4o-mini-tts", "Quiet documentary narration.");
        Assert.False(preferences.UsingDefaults);
        preferences.SaveDefaults();
        settings.SpeechDefaults["openai"] = new("tts-1");
        preferences.UseDefaults();
        Assert.Equal("Quiet documentary narration.", preferences.Current.Speech!.Delivery);
        Assert.True(preferences.UsingDefaults);
    }

    [Fact] public void LegacyHostedDefaultsAdoptExistingProviderSettingsOnce()
    {
        var settings = new AppSettings { NarrationDefaults = new("openai", "coral", 1, false, "", new()) };
        settings.SpeechDefaults["openai"] = new("gpt-4o-mini-tts", "Warm.");
        var preferences = new NarrationPreferences(settings);
        Assert.Equal("Warm.", preferences.Defaults.Speech!.Delivery);
        Assert.True(preferences.UsingDefaults);
        var legacyPreset = JsonSerializer.Deserialize<NarrationOptions>("""{"Engine":"openai","Voice":"coral","Speed":1,"ExcludeCode":false,"Pronunciation":"","Profile":{}}""")!;
        legacyPreset.ApplyTo(settings);
        Assert.Equal("Warm.", settings.SpeechDefaults["openai"].Delivery);
    }

    [Fact] public void LegacySettingsBecomeDefaultsWithoutReplacingTheUsersVoiceOrRules()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"Engine":"piper","Voice":"en_GB-alba-medium","Speed":1.15,"ExcludeCode":true,"Pronunciation":"API=A P I"}""")!;
        var preferences = new NarrationPreferences(settings);
        Assert.True(preferences.UsingDefaults);
        Assert.Equal("en_GB-alba-medium", preferences.Defaults.Voice);
        Assert.Equal(1.15, preferences.Defaults.Speed);
        Assert.True(preferences.Defaults.ExcludeCode);
        Assert.Equal("API=A P I", preferences.Defaults.Pronunciation);
    }

    [Fact] public async Task ReadinessAndOtherSettingsWritesDoNotTurnOneOffOverridesIntoDefaults()
    {
        using var test = new TestWorkspace();
        var settings = new AppSettings { Destination = test.Destination };
        var preferences = new NarrationPreferences(settings);
        preferences.SelectEngine("piper"); settings.Voice = "en_GB-alba-medium"; settings.Speed = 1.25;
        settings.ExcludeCode = true; settings.Pronunciation = "API=A P I";
        settings.PronunciationProfile = new() { Acronyms = AcronymReading.SpellUppercaseWords };
        settings.QueuePaused = true; settings.CacheQuotaMiB = 2048;
        settings.Providers["piper"] = new("piper", "fixture", [settings.Voice], "ready", 0);
        await test.Workspace.SaveSettingsAsync(preferences.ForPersistence());
        var saved = await test.Workspace.LoadSettingsAsync();
        Assert.Equal("kokoro", saved.Engine); Assert.Equal("af_heart", saved.Voice); Assert.Equal(1, saved.Speed);
        Assert.False(saved.ExcludeCode); Assert.Equal("", saved.Pronunciation);
        Assert.Equal(AcronymReading.AsWritten, saved.PronunciationProfile.Acronyms);
        Assert.True(saved.QueuePaused); Assert.Equal(2048, saved.CacheQuotaMiB);
        Assert.Equal(test.Destination, saved.Destination); Assert.Contains("piper", saved.Providers.Keys);
        Assert.False(preferences.UsingDefaults); Assert.Equal("en_GB-alba-medium", settings.Voice);
        var reopened = new NarrationPreferences(saved);
        Assert.True(reopened.UsingDefaults); Assert.Equal("af_heart", reopened.Current.Voice);
    }

    [Fact] public async Task ExplicitlySavedDefaultsSurviveRelaunchAndRemainIndependentOfLaterChoices()
    {
        using var test = new TestWorkspace(); var settings = new AppSettings(); var preferences = new NarrationPreferences(settings);
        preferences.SelectEngine("piper"); settings.Voice = "en_GB-alba-medium"; settings.Speed = .9;
        preferences.SaveDefaults();
        Assert.True(preferences.UsingDefaults);
        preferences.SelectEngine("kokoro"); settings.Voice = "am_michael"; settings.Speed = 1.2;
        await test.Workspace.SaveSettingsAsync(preferences.ForPersistence());
        var restoredSettings = await test.Workspace.LoadSettingsAsync();
        var restored = new NarrationPreferences(restoredSettings);
        Assert.Equal("piper", restored.Current.Engine); Assert.Equal("en_GB-alba-medium", restored.Current.Voice); Assert.Equal(.9, restored.Current.Speed);
        preferences.UseDefaults(); Assert.True(preferences.UsingDefaults); Assert.Equal(.9, settings.Speed);
    }

    [Fact] public void EngineSwitchRemembersEachSessionsVoiceWithoutSilentlySavingIt()
    {
        var settings = new AppSettings(); var preferences = new NarrationPreferences(settings);
        settings.Voice = "bf_emma"; preferences.SelectEngine("piper"); settings.Voice = "en_US-amy-medium";
        preferences.SelectEngine("kokoro"); Assert.Equal("bf_emma", settings.Voice);
        preferences.SelectEngine("piper"); Assert.Equal("en_US-amy-medium", settings.Voice);
        Assert.Equal("af_heart", preferences.Defaults.Voice);
        Assert.Empty(preferences.ForPersistence().DefaultVoices);
    }

    [Fact] public void SavingAnotherEnginesDefaultRemembersExplicitChoicesForBothEngines()
    {
        var settings = new AppSettings(); var preferences = new NarrationPreferences(settings);
        settings.Voice = "bf_emma"; preferences.SaveDefaults();
        preferences.SelectEngine("piper"); settings.Voice = "en_GB-jenny_dioco-medium"; preferences.SaveDefaults();
        var persisted = preferences.ForPersistence(); var reopened = new NarrationPreferences(persisted);
        reopened.SelectEngine("kokoro"); Assert.Equal("bf_emma", persisted.Voice);
        reopened.SelectEngine("piper"); Assert.Equal("en_GB-jenny_dioco-medium", persisted.Voice);
    }

    [Theory] [InlineData(.69)] [InlineData(1.41)] [InlineData(double.NaN)]
    public void InvalidDefaultsAreRefusedWithoutChangingPreviouslySavedChoices(double speed)
    {
        var settings = new AppSettings(); var preferences = new NarrationPreferences(settings); settings.Speed = speed;
        Assert.Throws<ArgumentException>(preferences.SaveDefaults);
        Assert.Equal(1, preferences.Defaults.Speed);
    }

    [Fact] public void QueuedSnapshotsStayFrozenWhenDefaultsOrCurrentChoicesChange()
    {
        var settings = new AppSettings(); var preferences = new NarrationPreferences(settings);
        var captured = preferences.Current;
        var job = new Job { Settings = new(captured.Engine, captured.Voice, captured.Speed, captured.ExcludeCode, captured.Pronunciation, "fixture", captured.Profile) };
        var before = job.Fingerprint;
        preferences.SelectEngine("piper"); settings.Voice = "en_US-amy-medium"; settings.Speed = 1.15; preferences.SaveDefaults();
        Assert.Equal("af_heart", job.Settings.Voice); Assert.Equal(before, job.Fingerprint);
    }

    [Theory]
    [InlineData("kokoro", "af_heart", "Heart · US English · female")]
    [InlineData("kokoro", "bf_emma", "Emma · British English · female")]
    [InlineData("kokoro", "bm_george", "George · British English · male")]
    [InlineData("piper", "en_GB-jenny_dioco-medium", "Jenny · British English")]
    [InlineData("piper", "en_US-bryce-medium", "Bryce · US English")]
    public void FriendlyLabelsKeepTheExactProviderVoiceIdentity(string engine, string id, string label)
    {
        var choice = SpeechVoiceCatalog.Describe(engine, id);
        Assert.Equal(id, choice.Id); Assert.Equal(label, choice.DisplayName);
    }

    [Fact] public void MissingSavedVoiceStaysVisibleAndIsNeverSilentlyReplaced()
    {
        var choices = SpeechVoiceCatalog.Choices("kokoro", ["af_heart", "af_heart", "bf_emma"], "am_michael");
        Assert.Equal(3, choices.Count);
        Assert.Equal("am_michael", choices[0].Id); Assert.False(choices[0].Available);
        Assert.Contains("refresh to verify", choices[0].DisplayName);
    }
}
