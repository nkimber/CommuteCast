using System.Text.Json;

namespace CommuteCast.Core;

/// <summary>User defaults and one-narration choices, independent of queued job snapshots.</summary>
public sealed record NarrationOptions(string Engine, string Voice, double Speed, bool ExcludeCode, string Pronunciation, PronunciationProfile Profile,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] LocalVoiceOptions? LocalVoice = null)
{
    public static NarrationOptions Capture(AppSettings settings) => new(settings.Engine, settings.Voice, settings.Speed,
        settings.ExcludeCode, settings.Pronunciation, settings.PronunciationProfile ?? new(),
        SpeechProviders.IsHosted(settings.Engine) ? null : settings.LocalVoice);

    public void ApplyTo(AppSettings settings)
    {
        settings.Engine = Engine; settings.Voice = Voice; settings.Speed = Speed;
        settings.ExcludeCode = ExcludeCode; settings.Pronunciation = Pronunciation; settings.PronunciationProfile = Profile;
        settings.LocalVoice = LocalVoice;
    }

    public void Validate()
    {
        if (!SpeechProviders.IsKnown(Engine) || string.IsNullOrWhiteSpace(Voice) || !double.IsFinite(Speed) || Speed is < .7 or > 1.4)
            throw new ArgumentException("Choose a speech engine, voice and pace between 0.70 and 1.40 before saving defaults.");
        Profile.Validate(Engine); TextPreparation.ValidateDictionary(Pronunciation); LocalVoice?.Validate(Engine, Voice);
    }
}

public sealed class NarrationPreferences
{
    private readonly AppSettings settings;
    private readonly Dictionary<string, string> sessionVoices = [];
    private readonly Dictionary<string, LocalVoiceOptions?> sessionDelivery = [];
    public NarrationOptions Defaults { get; private set; }
    public NarrationOptions Current => NarrationOptions.Capture(settings);
    public bool UsingDefaults => Current == Defaults;

    public NarrationPreferences(AppSettings settings)
    {
        this.settings = settings;
        Defaults = settings.NarrationDefaults ?? NarrationOptions.Capture(settings);
        Defaults.ApplyTo(settings);
        settings.NarrationDefaults = Defaults;
        settings.DefaultVoices ??= [];
        foreach (var entry in settings.DefaultVoices) sessionVoices[entry.Key] = entry.Value;
        sessionVoices[Defaults.Engine] = Defaults.Voice;
        sessionDelivery[Defaults.Engine] = Defaults.LocalVoice;
    }

    public void SelectEngine(string engine)
    {
        SpeechProviders.Get(engine);
        if (settings.Engine == engine) return;
        sessionVoices[settings.Engine] = settings.Voice;
        sessionDelivery[settings.Engine] = settings.LocalVoice;
        settings.Engine = engine;
        settings.Voice = sessionVoices.TryGetValue(engine, out var selected) ? selected : SpeechVoiceCatalog.DefaultVoice(engine);
        settings.LocalVoice = SpeechProviders.IsHosted(engine) ? null : sessionDelivery.TryGetValue(engine, out var delivery) ? delivery : new();
    }

    public void UseDefaults() => Defaults.ApplyTo(settings);
    public void RestoreSavedDefaults(NarrationOptions previous) { Defaults = previous; settings.NarrationDefaults = previous; }

    public void SaveDefaults()
    {
        var selected = Current;
        selected.Validate();
        Defaults = selected;
        settings.NarrationDefaults = selected;
        settings.DefaultVoices[selected.Engine] = selected.Voice;
        sessionVoices[selected.Engine] = selected.Voice;
    }

    /// <summary>General settings/readiness writes retain saved defaults, never transient overrides.</summary>
    public AppSettings ForPersistence()
    {
        var snapshot = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Defaults.ApplyTo(snapshot);
        snapshot.NarrationDefaults = Defaults;
        return snapshot;
    }
}
