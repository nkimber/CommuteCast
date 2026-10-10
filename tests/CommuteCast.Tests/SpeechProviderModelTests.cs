using System.Text.Json;
using CommuteCast.Core;

namespace CommuteCast.Tests;

public class SpeechProviderModelTests
{
    [Fact]
    public void LegacySettingsSerializationAndFingerprintStayCompatible()
    {
        var settings = new NarrationSettings("kokoro", "af_heart", 1, false, "", "original");
        Assert.DoesNotContain("Speech", JsonSerializer.Serialize(settings));
        var job = new Job { Settings = settings };
        var before = job.Fingerprint;
        var roundtrip = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(job))!;
        Assert.Equal(before, roundtrip.Fingerprint);
        job.Settings = settings with { Speech = new("gpt-4o-mini-tts") };
        Assert.NotEqual(before, job.Fingerprint);
    }
    [Fact]
    public void CapabilitiesDependOnModelAndDoNotPromiseJointFiveSpeakerSupportEverywhere()
    {
        Assert.Equal(2, SpeechProviders.Capabilities("gemini").DialogueSpeakers);
        Assert.Equal(5, SpeechProviders.Capabilities("elevenlabs", "eleven_v4").DialogueSpeakers);
        Assert.Equal(0, SpeechProviders.Capabilities("elevenlabs", "eleven_multilingual_v2").DialogueSpeakers);
        Assert.False(SpeechProviders.Capabilities("openai", "tts-1").DeliveryInstructions);
        Assert.False(SpeechProviders.IsHosted("piper"));
        Assert.Throws<ArgumentException>(() => SpeechProviders.Get("unregistered"));
    }
    [Fact]
    public void UnsupportedControlsFailRatherThanBecomingSpokenText()
    {
        Assert.Throws<ArgumentException>(() => new SpeechConfiguration("tts-1", "whisper").Validate("openai"));
        Assert.Throws<ArgumentException>(() => new SpeechConfiguration("sonic-3.6", Emotion: "[invented]").Validate("cartesia"));
        new SpeechConfiguration("gpt-4o-mini-tts", "Warm and curious").Validate("openai");
        Assert.Equal(new SpeechConfiguration("gpt-4o-mini-tts").Identity("openai"), new SpeechConfiguration("gpt-4o-mini-tts", "Curious").Identity("openai"));
    }
    [Fact]
    public void HostedPreferencesRememberVoicesAndUnknownCostIsNotZero()
    {
        var preferences = new NarrationPreferences(new());
        preferences.SelectEngine("openai"); Assert.Equal("coral", preferences.Current.Voice);
        preferences.SaveDefaults(); Assert.Equal("openai", preferences.ForPersistence().Engine);
        Assert.Contains("unavailable", new HostedConnection("gpt-4o-mini-tts").Estimate("openai", 100));
    }
}
