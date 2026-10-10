using CommuteCast.Core;

namespace CommuteCast.Desktop;

public sealed partial class MainViewModel
{
    public bool IsLocalVoice => Engine is "kokoro" or "piper";
    public bool IsKokoroVoice => Engine == "kokoro";
    public bool IsPiperVoice => Engine == "piper";
    public bool IsKokoroNarrator => IsKokoroVoice && !PodcastMode;
    public bool IsPiperNarrator => IsPiperVoice && !PodcastMode;
    private LocalVoiceOptions LocalDelivery => settings.LocalVoice ?? new(NaturalPhrasing: false);
    private void UpdateLocal(LocalVoiceOptions value) { settings.LocalVoice = value; RaiseLocalVoice(); RaiseNarrationChoices();  }
    public bool NaturalPhrasing { get => LocalDelivery.NaturalPhrasing; set => UpdateLocal(LocalDelivery with { NaturalPhrasing = value }); }
    public string BlendVoice { get => LocalDelivery.BlendVoice; set { if (value is not null) UpdateLocal(LocalDelivery with { BlendVoice = value }); } }
    public double BlendWeight { get => LocalDelivery.BlendWeight; set => UpdateLocal(LocalDelivery with { BlendWeight = Math.Round(value, 2) }); }
    public string BlendLabel => $"{BlendWeight:P0} of the second voice";
    public IReadOnlyList<SpeechVoiceChoice> BlendVoices => VoiceBlends(Voice, BlendVoice);
    private IReadOnlyList<SpeechVoiceChoice> VoiceBlends(string voice, string saved) => new[] { new SpeechVoiceChoice("", "No blend", true) }
        .Concat(SpeechVoiceCatalog.Choices("kokoro", Voices.Where(v => v.Available && v.Id != voice && v.Id.Length > 0 && voice.Length > 0 && v.Id[0] == voice[0]).Select(v => v.Id).ToArray(), saved).Where(v => v.Id.Length > 0)).ToArray();
    public bool TunePiperVariation { get => LocalDelivery.NoiseScale is not null || LocalDelivery.NoiseWidth is not null; set => UpdateLocal(LocalDelivery with { NoiseScale = value ? .667 : null, NoiseWidth = value ? .8 : null }); }
    public double PiperNoiseScale { get => LocalDelivery.NoiseScale ?? .667; set => UpdateLocal(LocalDelivery with { NoiseScale = Math.Round(value, 3) }); }
    public double PiperNoiseWidth { get => LocalDelivery.NoiseWidth ?? .8; set => UpdateLocal(LocalDelivery with { NoiseWidth = Math.Round(value, 3) }); }
    public string PiperVariationLabel => $"Sound {PiperNoiseScale:0.000} · timing {PiperNoiseWidth:0.000}";
    public double SentencePauseMs { get => LocalDelivery.SentencePauseMs; set => UpdateLocal(LocalDelivery with { SentencePauseMs = (int)Math.Round(value) }); }
    public double ParagraphPauseMs { get => LocalDelivery.ParagraphPauseMs; set => UpdateLocal(LocalDelivery with { ParagraphPauseMs = (int)Math.Round(value) }); }
    public string LocalPauseLabel => $"Sentence {SentencePauseMs:0} ms · paragraph {ParagraphPauseMs:0} ms";
    public double VoiceGainDb { get => LocalDelivery.GainDb; set => UpdateLocal(LocalDelivery with { GainDb = Math.Round(value, 1) }); }
    public string VoiceGainLabel => $"Input level {VoiceGainDb:0.0} dB";
    private void RaiseLocalVoice()
    {
        foreach (var name in new[] { nameof(IsKokoroNarrator), nameof(IsPiperNarrator), nameof(IsLocalVoice), nameof(IsKokoroVoice), nameof(IsPiperVoice), nameof(NaturalPhrasing), nameof(BlendVoice), nameof(BlendWeight), nameof(BlendLabel), nameof(BlendVoices), nameof(TunePiperVariation), nameof(PiperNoiseScale), nameof(PiperNoiseWidth), nameof(PiperVariationLabel), nameof(SentencePauseMs), nameof(ParagraphPauseMs), nameof(LocalPauseLabel), nameof(VoiceGainDb), nameof(VoiceGainLabel) }) Raise(name);
        foreach (var row in Cast) row.RefreshLocal();
    }
}
