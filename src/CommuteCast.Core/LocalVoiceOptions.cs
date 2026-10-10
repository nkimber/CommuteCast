namespace CommuteCast.Core;

/// <summary>Immutable local delivery recipe, captured with every job and audition.</summary>
public sealed record LocalVoiceOptions(bool NaturalPhrasing = true, string BlendVoice = "", double BlendWeight = .25,
    double? NoiseScale = null, double? NoiseWidth = null, double GainDb = 0,
    int SentencePauseMs = 220, int ParagraphPauseMs = 500, int TurnPauseMs = 140, int Version = 1)
{
    public void Validate(string engine, string voice)
    {
        if (engine is not ("kokoro" or "piper") || Version != 1)
            throw new ArgumentException("Choose a supported local voice profile.");
        if (BlendVoice is null || !double.IsFinite(BlendWeight) || BlendWeight is < 0 or > 1 ||
            !double.IsFinite(GainDb) || GainDb is < -12 or > 0 ||
            SentencePauseMs is < 0 or > 1200 || ParagraphPauseMs is < 0 or > 2000 || TurnPauseMs is < 0 or > 1200 ||
            NoiseScale is { } noise && (!double.IsFinite(noise) || noise is < 0 or > 1.5) ||
            NoiseWidth is { } width && (!double.IsFinite(width) || width is < 0 or > 1.5))
            throw new ArgumentException("Check local voice variation, blend, volume and pause settings.");
        if (engine == "piper" && BlendVoice.Length > 0 || engine == "kokoro" && (NoiseScale is not null || NoiseWidth is not null))
            throw new ArgumentException("Voice blending requires Kokoro; variation controls require Piper.");
        if (BlendVoice.Length > 0 && (!IsKokoroVoice(voice) || !IsKokoroVoice(BlendVoice) || voice[0] != BlendVoice[0] || voice == BlendVoice))
            throw new ArgumentException("Blend two different Kokoro voices with the same English accent.");
    }
    private static bool IsKokoroVoice(string voice) => voice.Length is > 3 and <= 80 &&
        voice[0] is 'a' or 'b' && voice[1] is 'f' or 'm' && voice[2] == '_' &&
        voice[3..].All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    public void RequireAvailable(ProviderInfo provider, string voice)
    {
        Validate(provider.Engine, voice);
        if (provider.LocalVoiceContract != 1)
            throw new IOException("The installed speech service needs the local voice-quality update. Provision the updated image, refresh voices and try again.");
        if (!provider.Voices.Contains(voice) || BlendVoice.Length > 0 && !provider.Voices.Contains(BlendVoice))
            throw new ArgumentException("A voice in this local profile is unavailable. Refresh voices and choose an installed voice.");
    }
}
