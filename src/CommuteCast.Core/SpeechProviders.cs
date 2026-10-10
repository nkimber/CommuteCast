using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CommuteCast.Core;

public sealed record SpeechCapabilities(bool Hosted, int MaximumCharacters, int DialogueSpeakers,
    bool DeliveryInstructions, bool Emotion, string[] Models);
public sealed record SpeechProviderDefinition(string Id, string Name, string DefaultModel, string DefaultVoice, SpeechCapabilities Capabilities);

/// <summary>Verified built-in transports. Additional adapters register against the same rendering boundary.</summary>
public static class SpeechProviders
{
    public static IReadOnlyList<SpeechProviderDefinition> All { get; } =
    [
        new("kokoro", "Kokoro · local", "", "af_heart", new(false, 900, 0, false, false, [])),
        new("piper", "Piper · local", "", "en_US-lessac-medium", new(false, 900, 0, false, false, [])),
        new("elevenlabs", "ElevenLabs · hosted", "eleven_v4", "JBFqnCBsd6RMkjVDRZzb", new(true, 2000, 5, false, true, ["eleven_v4", "eleven_v3", "eleven_multilingual_v2", "eleven_flash_v2_5"])),
        new("openai", "OpenAI · hosted", "gpt-4o-mini-tts-2025-12-15", "coral", new(true, 4096, 0, true, false, ["gpt-4o-mini-tts-2025-12-15", "gpt-4o-mini-tts", "tts-1", "tts-1-hd"])),
        new("cartesia", "Cartesia · hosted", "sonic-3.6-2026-08-27", "db6b0ed5-d5d3-463d-ae85-518a07d3c2b4", new(true, 2000, 0, false, true, ["sonic-3.6-2026-08-27", "sonic-3.6", "sonic-3.5", "sonic-3"])),
        new("gemini", "Google Gemini · hosted", "gemini-3.8-flash-tts", "Kore", new(true, 2000, 2, true, false, ["gemini-3.8-flash-tts", "gemini-3.8-flash-lite-tts"]))
    ];
    public static bool IsKnown(string id) => All.Any(p => p.Id == id);
    public static bool IsHosted(string id) => Get(id).Capabilities.Hosted;
    public static SpeechProviderDefinition Get(string id) => All.FirstOrDefault(p => p.Id == id)
        ?? throw new ArgumentException("Choose a supported speech provider.");
    public static SpeechCapabilities Capabilities(string id, string? model = null)
    {
        var capabilities = Get(id).Capabilities;
        if (id == "elevenlabs" && model is not (null or "eleven_v3" or "eleven_v4")) return capabilities with { DialogueSpeakers = 0, Emotion = false };
        if (id == "openai" && model is "tts-1" or "tts-1-hd") return capabilities with { DeliveryInstructions = false };
        return capabilities;
    }
    public static string[] Emotions { get; } = ["", "neutral", "calm", "curious", "happy", "excited", "sad", "skeptical", "confident", "contemplative"];
}

public sealed record SpeechConfiguration(string Model, string Delivery = "", string Emotion = "", bool Dialogue = true)
{
    public void Validate(string provider)
    {
        var definition = SpeechProviders.Get(provider);
        if (!definition.Capabilities.Hosted || !definition.Capabilities.Models.Contains(Model)) throw new ArgumentException("Choose a supported model for this provider.");
        if (Delivery is null || Delivery.Length > 2000 || Delivery.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new ArgumentException("Delivery instructions must fit 2,000 characters.");
        if (!SpeechProviders.Emotions.Contains(Emotion)) throw new ArgumentException("Choose a supported delivery emotion.");
        var capabilities = SpeechProviders.Capabilities(provider, Model);
        if (!capabilities.DeliveryInstructions && Delivery.Length > 0) throw new ArgumentException("This model uses voice and emotion controls rather than delivery instructions.");
        if (!capabilities.Emotion && Emotion.Length > 0) throw new ArgumentException("This model does not support this emotion control.");
    }
    public string Identity(string provider) { Validate(provider); return provider + ":hosted-v1:" + Job.Hash(Model); }
}

public sealed record HostedConnection(string Model, decimal? CostPerMillionCharacters = null, string Currency = "USD", DateOnly? RateDate = null)
{
    public void Validate(string provider)
    {
        new SpeechConfiguration(Model).Validate(provider);
        if (CostPerMillionCharacters is < 0 or > 1_000_000 || !Regex.IsMatch(Currency ?? "", "^[A-Z]{3}$")) throw new ArgumentException("Enter a nonnegative effective rate and a three-letter currency.");
    }
    public string Estimate(string provider, int characters) => CostPerMillionCharacters is { } rate
        ? $"Estimated {characters * rate / 1_000_000m:0.0000} {Currency} · effective character rate recorded {RateDate?.ToString("yyyy-MM-dd") ?? "without a date"}; provider billing may differ."
        : $"Hosted {provider}: {characters:N0} text characters; cost unavailable. Auditions and regenerations may be billed.";
}

public sealed record SpeechAttempt(string Id, DateTimeOffset StartedUtc, string Provider, string Model, string TextHash,
    string State, string? RequestId = null, long? BilledCharacters = null, string? Usage = null);
