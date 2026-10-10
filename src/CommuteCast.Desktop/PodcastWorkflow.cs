using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Desktop;

public sealed class SpeakerEditor : Observable
{
    private PodcastSpeaker value;
    private readonly Action changed;
    private readonly Func<string, string, IReadOnlyList<SpeechVoiceChoice>>? blends;
    private readonly Action<SpeakerEditor, SpeakerPersonality>? bindPersonality;
    public SpeakerEditor(PodcastSpeaker speaker, Action changed, Action<SpeakerEditor, SpeakerPersonality>? bindPersonality = null, Func<string, string, IReadOnlyList<SpeechVoiceChoice>>? blends = null) { value = speaker; this.changed = changed; this.bindPersonality = bindPersonality; this.blends = blends; }
    public PodcastSpeaker Capture() => value;
    private void Update(PodcastSpeaker next) { value = next; Raise(null); changed(); }
    public string Name { get => value.Name; set => Update(this.value with { Name = value }); }
    public string Role { get => value.Role; set => Update(this.value with { Role = value }); }
    public string Expertise { get => value.Expertise; set => Update(this.value with { Expertise = value }); }
    public string Style { get => value.Style; set => Update(this.value with { Style = value }); }
    public string Voice { get => value.Voice; set { if (value is not null) Update(this.value with { Voice = value }); } }
    public double Speed { get => value.Speed; set => Update(this.value with { Speed = Math.Round(value, 2) }); }
    public string Delivery { get => value.Delivery; set => Update(this.value with { Delivery = value }); }
    public string Emotion { get => value.Emotion; set => Update(this.value with { Emotion = value }); }
    public LocalVoiceOptions? LocalVoice { get => value.LocalVoice; set => Update(this.value with { LocalVoice = value }); }
    private LocalVoiceOptions Local => value.LocalVoice ?? new();
    public string LocalBlendVoice { get => Local.BlendVoice; set { if (value is not null) LocalVoice = Local with { BlendVoice = value }; } }
    public double LocalBlendWeight { get => Local.BlendWeight; set => LocalVoice = Local with { BlendWeight = Math.Round(value, 2) }; }
    public IReadOnlyList<SpeechVoiceChoice> AvailableBlends => blends?.Invoke(value.Voice, Local.BlendVoice) ?? [];
    public bool TuneLocalVariation { get => Local.NoiseScale is not null || Local.NoiseWidth is not null; set => LocalVoice = Local with { NoiseScale = value ? .667 : null, NoiseWidth = value ? .8 : null }; }
    public double LocalNoiseScale { get => Local.NoiseScale ?? .667; set => LocalVoice = Local with { NoiseScale = Math.Round(value, 3) }; }
    public double LocalNoiseWidth { get => Local.NoiseWidth ?? .8; set => LocalVoice = Local with { NoiseWidth = Math.Round(value, 3) }; }
    public double LocalGainDb { get => Local.GainDb; set => LocalVoice = Local with { GainDb = Math.Round(value, 1) }; }
    public double LocalTurnPauseMs { get => Local.TurnPauseMs; set => LocalVoice = Local with { TurnPauseMs = (int)Math.Round(value) }; }
    public string Pronunciation { get => value.Pronunciation ?? ""; set => Update(this.value with { Pronunciation = string.IsNullOrEmpty(value) ? null : value }); }
    internal void RefreshLocal() => Raise(null);
    private SpeakerPersonality? personality;
    public SpeakerPersonality? Personality { get => personality; set { personality = value; if (value is not null) { Update(this.value with { Name = value.Name, Expertise = value.Expertise, Style = value.Style }); bindPersonality?.Invoke(this, value); } Raise(); } }
    public string[] Roles { get; } = ["Host", "Guest"];
}

public sealed partial class MainViewModel
{
    private readonly ISpeechSecrets speechSecrets = new WindowsSpeechSecrets();
    private bool podcastMode, dialogueMode = true;
    private bool podcastTouched;
    private PodcastFormat podcastFormat = PodcastDefaults.Formats[0];
    private string podcastValidation = "Validate the pasted dialogue before creating an MP3.";
    private string? validatedPodcastIdentity;
    private string? promptPodcastIdentity;
    public bool PodcastMode { get => podcastMode; set { if (Set(ref podcastMode, value)) PodcastChanged(); } }
    public bool DialogueMode { get => dialogueMode; set { if (Set(ref dialogueMode, value)) PodcastChanged(); } }
    public ObservableCollection<SpeakerEditor> Cast { get; } = [];
    public ObservableCollection<SpeakerPersonality> SpeakerLibrary { get; private set; } = [];
    public ObservableCollection<PodcastFormat> PodcastFormats { get; private set; } = [];
    public PodcastFormat PodcastFormat { get => podcastFormat; set { if (value is not null && Set(ref podcastFormat, value)) PodcastChanged(); } }
    public string PodcastValidation { get => podcastValidation; private set => Set(ref podcastValidation, value); }
    public string PodcastValidationHeading => validatedPodcastIdentity is null ? "Dialogue needs validation" : "Dialogue validated · show details";
    public string CastSummary => $"{PodcastFormat.Name} · {Cast.Count} speakers · {Engine}";
    public bool IsHosted => SpeechProviders.IsHosted(Engine);
    public string[] SpeechModels => SpeechProviders.Get(Engine).Capabilities.Models;
    private SpeechConfiguration CurrentSpeech => settings.SpeechDefaults.TryGetValue(Engine, out var config) ? config : new(settings.HostedConnections.TryGetValue(Engine, out var connection) ? connection.Model : SpeechProviders.Get(Engine).DefaultModel);
    public string SpeechModel { get => CurrentSpeech.Model; set { if (value is not null && IsHosted) { var caps = SpeechProviders.Capabilities(Engine, value); settings.SpeechDefaults[Engine] = CurrentSpeech with { Model = value, Delivery = caps.DeliveryInstructions ? CurrentSpeech.Delivery : "", Emotion = caps.Emotion ? CurrentSpeech.Emotion : "" }; ClearUnsupportedCastControls(); HostedChanged(); RefreshVoiceChoices(); ServiceStatus = "Model selected · refresh voices to check availability"; } } }
    public string Delivery { get => CurrentSpeech.Delivery; set { settings.SpeechDefaults[Engine] = CurrentSpeech with { Delivery = value }; HostedChanged(); } }
    public string Emotion { get => CurrentSpeech.Emotion; set { if (value is not null) { settings.SpeechDefaults[Engine] = CurrentSpeech with { Emotion = value }; HostedChanged(); } } }
    public string[] Emotions => SpeechProviders.Emotions;
    public bool SupportsDelivery => IsHosted && SpeechProviders.Capabilities(Engine, SpeechModel).DeliveryInstructions;
    public bool SupportsEmotion => IsHosted && SpeechProviders.Capabilities(Engine, SpeechModel).Emotion;
    public string ProviderHelp => IsHosted ? "Hosted speech sends spoken text to your selected provider and may be billed. Readiness checks credentials; samples generate billable audio. Keys are kept in Windows Credential Manager and excluded from backups." : "Local speech uses installed Kokoro or Piper services. Docker is required only for local providers.";
    public string HostedKeyStatus { get { if (!IsHosted) return "No API key required."; try { return speechSecrets.Get(Engine) is null ? "No API key saved." : "API key saved in Windows credential storage."; } catch { return "Credential storage needs attention."; } } }
    public string EffectiveRate { get => settings.HostedConnections.TryGetValue(Engine, out var c) && c.Model == SpeechModel ? c.CostPerMillionCharacters?.ToString(CultureInfo.InvariantCulture) ?? "" : ""; set { if (!IsHosted) return; decimal? rate = string.IsNullOrWhiteSpace(value) ? null : decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0 ? parsed : throw new ArgumentException("Enter a nonnegative rate, or leave it blank for unknown cost."); settings.HostedConnections[Engine] = new(SpeechModel, rate, RateCurrency, DateOnly.FromDateTime(DateTime.Today)); Raise(); Raise(nameof(HostedEstimate)); } }
    public string RateCurrency { get => settings.HostedConnections.TryGetValue(Engine, out var c) ? c.Currency : "USD"; set { if (IsHosted) { var current = settings.HostedConnections.TryGetValue(Engine, out var c) ? c : new HostedConnection(SpeechModel); var next = current with { Currency = value.ToUpperInvariant() }; next.Validate(Engine); settings.HostedConnections[Engine] = next; Raise(); Raise(nameof(HostedEstimate)); } } }
    public string HostedEstimate => IsHosted ? (settings.HostedConnections.TryGetValue(Engine, out var connection) && connection.Model == SpeechModel ? connection : new HostedConnection(SpeechModel)).Estimate(Engine, HostedCharacters) : "Local inference has no hosted API charge.";
    private int HostedCharacters { get { if (!PodcastMode) return Source.Length; try { return PodcastScript.Parse(Source, CaptureEpisode()).Sum(t => t.Text.Length); } catch (ArgumentException) { return Source.Length; } } }
    private string hostedUsageSummary = "Hosted audition request history has not been refreshed. Provider billing is authoritative.";
    public string HostedUsageSummary { get => hostedUsageSummary; private set => Set(ref hostedUsageSummary, value); }
    public ICommand RefreshHostedUsageCommand { get; private set; } = null!;
    public ICommand EditCastCommand { get; private set; } = null!;
    public ICommand ApplyFormatCommand { get; private set; } = null!;
    public ICommand AddSpeakerCommand { get; private set; } = null!;
    public ICommand RemoveSpeakerCommand { get; private set; } = null!;
    public ICommand SavePersonalityCommand { get; private set; } = null!;
    public ICommand RemovePersonalityCommand { get; private set; } = null!;
    public ICommand SaveFormatCommand { get; private set; } = null!;
    public ICommand RemoveFormatCommand { get; private set; } = null!;
    public ICommand ValidatePodcastCommand { get; private set; } = null!;
    public ICommand AuditionSpeakerCommand { get; private set; } = null!;
    public ICommand RemoveHostedKeyCommand { get; private set; } = null!;
    public string FormatName { get; set; } = "My podcast format";
    public int FormatHosts { get; set; } = 1;
    public int FormatMinimumGuests { get; set; } = 1;
    public int FormatMaximumGuests { get; set; } = 4;
    public string FormatStructure { get; set; } = "A host guides a thoughtful discussion with guests.";
    private void InitializePodcast()
    {
        SpeakerLibrary = new(settings.SpeakerLibrary); PodcastFormats = new(settings.PodcastFormats);
        LoadEpisode(new(PodcastDefaults.Formats[0], PodcastDefaults.Personalities.Take(2).Select(p => new PodcastSpeaker(p.Name, "Host", p.Expertise, p.Style, "")).ToArray()));
        EditCastCommand = Command(_ => { new PodcastCastWindow { DataContext = this, Owner = Application.Current.MainWindow }.ShowDialog(); return Task.CompletedTask; }, false);
        ApplyFormatCommand = Command(_ => { ApplyFormat(); return Task.CompletedTask; });
        AddSpeakerCommand = Command(_ => { if (Cast.Count >= 5 || Cast.Count >= PodcastFormat.Hosts + PodcastFormat.MaximumGuests) throw new ArgumentException("The selected format cannot add another speaker. Choose a larger panel format."); AddDefaultSpeaker(Cast.Count); PodcastChanged(); return Task.CompletedTask; });
        RemoveSpeakerCommand = Command(p => { if (p is SpeakerEditor row) { if (Cast.Count <= Math.Max(2, PodcastFormat.Hosts + PodcastFormat.MinimumGuests)) throw new ArgumentException("The selected format needs this many participants."); Cast.Remove(row); PodcastChanged(); } return Task.CompletedTask; });
        SavePersonalityCommand = Command(async p =>
        {
            if (p is not SpeakerEditor row) return; var s = row.Capture();
            var validation = new PodcastEpisode(PodcastDefaults.Formats[0], [s with { Role = "Host" }, new(s.Name == "Validation" ? "Other" : "Validation", "Host", "", "", "")]); validation.Validate(false);
            if (!IsHosted) s.LocalVoice?.Validate(Engine, s.Voice);
            TextPreparation.ValidateDictionary(s.Pronunciation ?? "");
            if (SpeakerLibrary.Count >= 100 && row.Personality is null) throw new ArgumentException("The personality library supports 100 entries.");
            var entry = new SpeakerPersonality(row.Personality?.Id ?? Guid.NewGuid().ToString("N"), s.Name, s.Expertise, s.Style, s.Delivery, s.Emotion);
            var old = SpeakerLibrary.FirstOrDefault(x => x.Id == entry.Id); if (old is not null) SpeakerLibrary.Remove(old); SpeakerLibrary.Add(entry); settings.SpeakerLibrary = SpeakerLibrary.ToList();
            if (s.Voice.Length > 0)
            {
                var model = IsHosted ? SpeechModel : ""; settings.SpeakerVoiceBindings.RemoveAll(b => b.PersonalityId == entry.Id && b.Provider == Engine && b.Model == model);
                settings.SpeakerVoiceBindings.Add(new(entry.Id, Engine, model, s.Voice, s.Speed, s.Delivery, s.Emotion, IsHosted ? null : s.LocalVoice, s.Pronunciation));
            }
            row.Personality = entry; await SaveSettingsAsync(); StatusMessage = "Speaker personality and provider voice binding saved. Existing episodes retain their captured cast.";
        });
        RemovePersonalityCommand = Command(async p => { if (p is SpeakerEditor { Personality: { } entry }) { SpeakerLibrary.Remove(entry); settings.SpeakerLibrary = SpeakerLibrary.ToList(); await SaveSettingsAsync(); } });
        SaveFormatCommand = Command(async _ => { var existing = PodcastFormats.FirstOrDefault(f => f.Name == FormatName.Trim()); var format = new PodcastFormat(existing?.Id ?? Guid.NewGuid().ToString("N"), FormatName.Trim(), FormatHosts, FormatMinimumGuests, FormatMaximumGuests, FormatStructure); format.Validate(); if (existing is not null) PodcastFormats.Remove(existing); PodcastFormats.Add(format); settings.PodcastFormats = PodcastFormats.ToList(); PodcastFormat = format; await SaveSettingsAsync(); StatusMessage = "Podcast format saved. Apply it to update the draft cast."; });
        RemoveFormatCommand = Command(async _ => { if (PodcastFormats.Count <= 1) throw new ArgumentException("Keep at least one podcast format."); PodcastFormats.Remove(PodcastFormat); settings.PodcastFormats = PodcastFormats.ToList(); PodcastFormat = PodcastFormats[0]; await SaveSettingsAsync(); });
        ValidatePodcastCommand = Command(_ =>
        {
            var (episode, prepared, units) = PreparePodcast(Source, CapturePreviewSettings()); var turns = PodcastScript.Parse(Source, episode);
            validatedPodcastIdentity = PodcastValidationIdentity(episode);
            Raise(nameof(PodcastValidationHeading));
            var total = NarrationEstimate.CountWords(prepared.Script);
            var balance = string.Join(" · ", episode.Speakers.Select(s => $"{s.Name}: {NarrationEstimate.CountWords(string.Concat(units.SelectMany(u => u.Turns!).Where(t => t.Speaker == s.Name).Select(t => t.Text))) * 100.0 / Math.Max(1, total):0}%"));
            PodcastValidation = $"Valid dialogue · {turns.Count} turns · {units.Count} render blocks · {total:N0} spoken words (about {total / 160.0:0.0}–{total / 140.0:0.0} minutes).\nSpeaker balance: {balance}.\n" + (episode.Speakers.Select(s => s.Voice).Distinct().Count() < episode.Speakers.Count ? "Some speakers share a voice; audition for recognizability.\n" : "") + "Review spoken text before generation. Validation checks format, not factual accuracy.";
            StatusMessage = PodcastValidation; return Task.CompletedTask;
        });
        AuditionSpeakerCommand = Command(async p => { if (p is not SpeakerEditor row) return; var s = row.Capture(); var current = CapturePreviewSettings(); var snapshot = current with { Voice = s.Voice, Speed = s.Speed, Pronunciation = PodcastScript.PronunciationFor(s, current), LocalVoice = PodcastScript.LocalDelivery(s, current), Speech = IsHosted ? CurrentSpeech with { Delivery = s.Delivery, Emotion = s.Emotion } : null }; await AuditionAsync(AuditionRequest.Expressive(snapshot), false); });
        RemoveHostedKeyCommand = Command(_ => { if (IsHosted) speechSecrets.Remove(Engine); Raise(nameof(HostedKeyStatus)); return Task.CompletedTask; });
        RefreshHostedUsageCommand = Command(async _ => { var requests = await new HostedAuditionHistory(Workspace).RecentAsync(shutdown.Token); HostedUsageSummary = requests.Count == 0 ? "No hosted audition requests recorded." : "Recent hosted auditions (provider billing is authoritative):\n" + string.Join("\n", requests.Take(20).Select(a => $"{a.StartedUtc:MMM d HH:mm} · {a.Provider} · {a.State} · request {a.RequestId ?? "ID unavailable"}")); });
    }
    public async Task SaveHostedKeyAsync(string key)
    {
        if (!IsHosted) throw new ArgumentException("Select a hosted speech provider first.");
        CurrentSpeech.Validate(Engine); speechSecrets.Set(Engine, key); Raise(nameof(HostedKeyStatus)); await SaveSettingsAsync(); StatusMessage = "API key saved. Check speech readiness to discover voices.";
    }
    private void AddDefaultSpeaker(int index)
    {
        var p = PodcastDefaults.Personalities[index % 5]; var name = p.Name; var n = 2; while (Cast.Any(s => s.Name == name)) name = p.Name + " " + n++;
        Cast.Add(new(new(name, index < PodcastFormat.Hosts ? "Host" : "Guest", p.Expertise, p.Style, ""), PodcastChanged, BindPersonality, VoiceBlends));
    }
    private void ApplyFormat()
    {
        var count = Math.Clamp(Cast.Count, PodcastFormat.Hosts + PodcastFormat.MinimumGuests, PodcastFormat.Hosts + PodcastFormat.MaximumGuests);
        while (Cast.Count > count) Cast.RemoveAt(Cast.Count - 1); while (Cast.Count < count) AddDefaultSpeaker(Cast.Count);
        for (var i = 0; i < Cast.Count; i++) Cast[i].Role = i < PodcastFormat.Hosts ? "Host" : "Guest"; PodcastChanged();
    }
    internal PodcastEpisode CaptureEpisode() => new(PodcastFormat, Cast.Select(s => s.Capture()).ToArray(), DialogueMode);
    internal void LoadEpisode(PodcastEpisode episode)
    {
        podcastFormat = episode.Format; dialogueMode = episode.Dialogue; Cast.Clear(); foreach (var s in episode.Speakers) Cast.Add(new(s, PodcastChanged, BindPersonality, VoiceBlends)); Raise(null);
    }
    private string PodcastValidationIdentity(PodcastEpisode episode) => Job.Hash(Source + episode.Identity + Engine + System.Text.Json.JsonSerializer.Serialize(CapturePreviewSettings()));
    private (PodcastEpisode Episode, PreparedText Prepared, List<TextChunk> Units) PreparePodcast(string text, NarrationSettings snapshot)
    {
        var episode = CaptureEpisode(); var (prepared, units) = PodcastScript.Prepare(text, episode, snapshot); return (episode, prepared, units);
    }
    private void PodcastChanged()
    {
        if (PodcastMode) podcastTouched = true;
        validatedPodcastIdentity = null; PodcastValidation = "Cast or text changed. Validate dialogue before creating an MP3."; Raise(nameof(CastSummary)); RaisePromptState(); ScheduleDraftSave();
        Raise(nameof(PodcastValidationHeading)); Raise(nameof(IsKokoroNarrator)); Raise(nameof(IsPiperNarrator));
    }
    private void HostedChanged() { Raise(null); PodcastChanged(); }
    private void ProviderChanged()
    {
        // A voice ID belongs to its provider. Preserve it visibly as unavailable until explicitly replaced.
        ClearUnsupportedCastControls(); Raise(null); PodcastChanged();
    }
    private void ClearUnsupportedCastControls()
    {
        foreach (var row in Cast) { if ((IsHosted || IsPiperVoice && row.LocalVoice?.BlendVoice.Length > 0 || IsKokoroVoice && (row.LocalVoice?.NoiseScale is not null || row.LocalVoice?.NoiseWidth is not null)) && row.LocalVoice is not null) row.LocalVoice = null; if (!SupportsDelivery && row.Delivery.Length > 0) row.Delivery = ""; if (!SupportsEmotion && row.Emotion.Length > 0) row.Emotion = ""; }
    }
    private void BindPersonality(SpeakerEditor row, SpeakerPersonality personality)
    {
        var model = IsHosted ? SpeechModel : ""; var binding = settings.SpeakerVoiceBindings.FirstOrDefault(b => b.PersonalityId == personality.Id && b.Provider == Engine && b.Model == model);
        row.Voice = binding?.Voice ?? ""; row.Speed = binding?.Speed ?? 1;
        row.LocalVoice = IsHosted ? null : binding?.LocalVoice; row.Pronunciation = binding?.Pronunciation ?? "";
        row.Delivery = SupportsDelivery ? binding?.Delivery ?? personality.Delivery : ""; row.Emotion = SupportsEmotion ? binding?.Emotion ?? personality.Emotion : "";
    }
    internal void RestorePodcastDraft(PodcastDraft? draft) { if (draft is null) return; podcastTouched = true; LoadEpisode(draft.Episode); PodcastMode = draft.Enabled; }
}
