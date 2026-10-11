using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Windows.Input;

namespace CommuteCast.Desktop;

public sealed partial class MainViewModel
{
    private NarrationVersion? draftVersion;
    public ICommand OpenOriginalVersionCommand { get; }
    public bool HasVersionDraft => draftVersion is not null;
    public string VersionDraftSummary => draftVersion is { } version
        ? $"New version of {version.BaseTitle}. Choose a model and voice, review the spoken text, then create a separate MP3."
        : "";
    private static string SuggestedVersionTitle(NarrationVersion version) => $"{version.BaseTitle} — Version {version.Number}";
    private int NextVersionNumber(string root) => Jobs.Where(j => j.Id == root || j.Job.Version?.RootJobId == root)
        .Select(j => j.Job.Version?.Number ?? 1).DefaultIfEmpty(1).Max() + 1;

    internal void CreateVersionDraft(Job job)
    {
        if (job.DeletionRequested || job.Stage == JobStage.Deleting) throw new ArgumentException("This narration is pending removal.");
        var root = job.Version?.RootJobId ?? job.Id;
        draftVersion = new(root, job.Id, job.Version?.BaseTitle ?? job.Title, Math.Max(NextVersionNumber(root), (job.Version?.Number ?? 1) + 1), Job.Hash(job.Source));
        DraftTitle = SuggestedVersionTitle(draftVersion); Source = job.Source;
        ApplyVersionSettings(job.Settings);
        PodcastMode = job.Episode is not null;
        if (job.Episode is { } episode) LoadEpisode(episode);
        ProviderChanged(); Navigate("compose"); Raise(nameof(HasVersionDraft)); Raise(nameof(VersionDraftSummary));
        ScheduleDraftSave();
        StatusMessage = "New version draft opened with the saved script and settings. Choose another model or voice, then review before creating the MP3.";
    }
    private void ApplyVersionSettings(NarrationSettings saved)
    {
        new NarrationOptions(saved.Engine, saved.Voice, saved.Speed, saved.ExcludeCode, saved.Pronunciation,
            saved.Profile ?? new(), saved.LocalVoice).ApplyTo(settings);
        if (saved.Speech is { } speech) settings.SpeechDefaults[saved.Engine] = speech;
        RefreshVoiceChoices(); Raise(null); ScheduleEstimate();
    }
    private VersionDraft? CaptureVersionDraft() => draftVersion is null ? null : new(draftVersion,
        new(Engine, Voice, Speed, ExcludeCode, Pronunciation, "", settings.PronunciationProfile,
            Speech: IsHosted ? CurrentSpeech : null, LocalVoice: settings.LocalVoice));
    internal void RestoreVersionDraft(VersionDraft? saved)
    {
        draftVersion = saved?.Version;
        if (saved is not null) ApplyVersionSettings(saved.Settings);
        Raise(nameof(HasVersionDraft)); Raise(nameof(VersionDraftSummary));
    }
    internal NarrationVersion? CaptureSubmittedVersion() => draftVersion is { } version
        ? version with { Number = Math.Max(NextVersionNumber(version.RootJobId), version.Number) } : null;
    internal static void AssignSubmittedVersion(Job job, NarrationVersion? version, string? suggestedTitle)
    {
        job.Version = version;
        if (version is not null && job.Title == suggestedTitle)
            job.Title = $"{version.BaseTitle} — {job.Settings.Speech?.Model ?? job.Settings.Engine} — Version {version.Number}";
    }
    private void ClearVersionDraft()
    {
        draftVersion = null; Raise(nameof(HasVersionDraft)); Raise(nameof(VersionDraftSummary));
    }
}
