using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Desktop;

public sealed class ComparisonCandidate(string name, NarrationOptions recipe) : Observable
{
    public string Name { get; } = name;
    public NarrationOptions Recipe { get; } = recipe;
    public string Description => $"{Recipe.Engine} · {SpeechVoiceCatalog.Describe(Recipe.Engine, Recipe.Voice).DisplayName} · {Recipe.Speed:0.00}× · " +
        (Recipe.LocalVoice?.NaturalPhrasing == true ? "natural phrasing and volume" : "legacy phrasing and native volume") +
        (Recipe.LocalVoice is { BlendVoice.Length: > 0 } local ? $" · blend {local.BlendVoice} {local.BlendWeight:P0}" : "") +
        (Recipe.LocalVoice is { NoiseScale: not null } variation ? $" · variation {variation.NoiseScale:0.00}/{variation.NoiseWidth:0.00}" : "") +
        (Recipe.LocalVoice is { NaturalPhrasing: true } pauses ? $" · pauses {pauses.SentencePauseMs}/{pauses.ParagraphPauseMs} ms" : "");
    public string Notes { get; set; } = "";
    internal AuditionAudio? Audio { get; set; }
    private string status = "Ready to render this sample";
    public string Status { get => status; internal set => Set(ref status, value); }
}

public sealed partial class MainViewModel
{
    public string[] ComparisonEngines { get; } = ["kokoro", "piper"];
    public ObservableCollection<ComparisonCandidate> ComparisonCandidates { get; } = [];
    private string comparisonPassage = "", comparisonSpoken = "", comparisonPresetName = "My commute narrator";
    private bool comparisonBusy, comparisonPrepared;
    private ComparisonCandidate? selectedComparison;
    private CancellationTokenSource? comparisonCancellation;
    private Task? comparisonTask;
    public string ComparisonPassage { get => comparisonPassage; set { if (ComparisonLocked) return; if (Set(ref comparisonPassage, value)) comparisonPrepared = false; } }
    public string ComparisonPresetName { get => comparisonPresetName; set => Set(ref comparisonPresetName, value); }
    public ComparisonCandidate? SelectedComparison { get => selectedComparison; set => Set(ref selectedComparison, value); }
    public bool ComparisonLocked => ComparisonCandidates.Count > 0;
    public bool ComparisonBusy { get => comparisonBusy; private set { Set(ref comparisonBusy, value); Raise(nameof(ComparisonIdle)); } }
    public bool ComparisonIdle => !ComparisonBusy;
    public string ComparisonSummary => ComparisonLocked
        ? $"{ComparisonCandidates.Count} candidates · {NarrationEstimate.CountWords(comparisonSpoken):N0} spoken words. Every sample uses this same prepared passage."
        : "Choose a passage from your script (up to 3,600 characters), then add up to four voice setups. No audio is generated until you choose Render selected sample.";
    public ICommand CompareVoicesCommand { get; private set; } = null!;
    public ICommand ComparisonDraftCommand { get; private set; } = null!;
    public ICommand ComparisonSelectionCommand { get; private set; } = null!;
    public ICommand AddComparisonCommand { get; private set; } = null!;
    public ICommand RenderComparisonCommand { get; private set; } = null!;
    public ICommand PlayComparisonCommand { get; private set; } = null!;
    public ICommand SaveComparisonCommand { get; private set; } = null!;
    public ICommand ClearComparisonCommand { get; private set; } = null!;
    private VoiceComparisonWindow? comparisonWindow;

    private void InitializeComparison()
    {
        CompareVoicesCommand = Command(_ =>
        {
            if (comparisonWindow is not null) { comparisonWindow.Activate(); return Task.CompletedTask; }
            comparisonWindow = new() { DataContext = this, Owner = Application.Current.MainWindow };
            comparisonWindow.Closed += (_, _) => comparisonWindow = null;
            comparisonWindow.Show(); return Task.CompletedTask;
        });
        ComparisonDraftCommand = Command(_ => { LoadComparisonPassage(false); return Task.CompletedTask; });
        ComparisonSelectionCommand = Command(_ => { LoadComparisonPassage(true); return Task.CompletedTask; });
        AddComparisonCommand = Command(_ => { AddComparison(); return Task.CompletedTask; });
        RenderComparisonCommand = Command(async _ => { if (ComparisonBusy) return; comparisonTask = RenderSelectedComparisonAsync(); await comparisonTask; });
        PlayComparisonCommand = Command(async _ =>
        {
            if (ComparisonBusy) throw new IOException("Stop rendering before switching samples.");
            var candidate = SelectedComparison ?? throw new ArgumentException("Choose a comparison candidate.");
            var audio = candidate.Audio ?? throw new ArgumentException("Render this sample first. Replaying a completed sample does not generate speech again.");
            await StopPlaybackAsync(false);
            var path = OwnedFileRemoval.Resolve(Workspace.Root, audio.RelativePath);
            Workspace.RejectFileReparsePoint(path);
            if (await Infrastructure.Workspace.HashFileAsync(path, shutdown.Token) != audio.Hash) throw new IOException("The comparison sample changed or is unavailable. Start a new comparison.");
            Playback.Open(path, candidate.Name); StatusMessage = "Playing " + candidate.Name + ". Samples stay available until you clear the comparison or close CommuteCast.";
        });
        SaveComparisonCommand = Command(async _ =>
        {
            if (ComparisonBusy) throw new IOException("Wait for the sample to finish before saving a preferred setup.");
            var candidate = SelectedComparison ?? throw new ArgumentException("Choose your preferred comparison candidate.");
            var previous = settings.NarrationPresets!.ToList();
            presetLibrary.Save(ComparisonPresetName, candidate.Recipe);
            try { await SaveSettingsAsync(); } catch { settings.NarrationPresets = previous; throw; }
            RefreshPresets(ComparisonPresetName.Trim()); ApplyNarrationOptions(candidate.Recipe);
            StatusMessage = "Preferred voice setup saved as a named preset and applied to this draft. Use Save as my defaults if you want it for every new narration.";
        });
        ClearComparisonCommand = Command(_ => ClearComparisonAsync());
    }

    private void LoadComparisonPassage(bool selection)
    {
        if (ComparisonLocked) throw new ArgumentException("Choose Start a new comparison before replacing its passage.");
        var snapshot = CapturePreviewSettings();
        string spoken;
        if (selection)
        {
            // Prepare in the full source context so excluded code cannot reappear in a cropped selection.
            if (selectionLength < 1) throw new ArgumentException("Select a passage in your script first.");
            if (selectionSource != Source) throw new ArgumentException("Your script changed. Select the passage again before comparing it.");
            var prepared = TextPreparation.Prepare(Source, snapshot.ExcludeCode, snapshot.Pronunciation, snapshot.Profile);
            var end = selectionStart + selectionLength;
            var selected = prepared.Spans.Where(span => span.Start < end && span.Start + span.Length > selectionStart).ToArray();
            if (selected.Any(span => span.Start < selectionStart || span.Start + span.Length > end))
            {
                // Short exact selections can use the established source-aware audition preparer.
                spoken = AuditionRequest.Selection(Source, selectionStart, selectionLength, snapshot).Prepare().Script;
            }
            else spoken = string.Concat(selected.Select(span => span.Narration));
        }
        else spoken = TextPreparation.Prepare(Source, snapshot.ExcludeCode, snapshot.Pronunciation, snapshot.Profile).Script;
        SetComparisonPassage(spoken);
    }
    private void SetComparisonPassage(string spoken)
    {
        if (string.IsNullOrWhiteSpace(spoken) || spoken.Length > AuditionGenerator.MaximumComparisonCharacters)
            throw new ArgumentException("Choose a shorter passage of up to 3,600 spoken characters. The current script is retained; nothing was truncated.");
        ComparisonPassage = spoken; comparisonPrepared = true;
    }
    internal void AddComparison()
    {
        if (ComparisonBusy) throw new IOException("Wait for the sample to finish.");
        if (!IsLocalVoice || PodcastMode) throw new ArgumentException("Compare a single local narrator using Kokoro or Piper.");
        if (ComparisonCandidates.Count >= 4) throw new ArgumentException("Compare up to four setups. Start a new comparison for another shortlist.");
        var recipe = narrationPreferences.Current; recipe.Validate();
        if (ComparisonCandidates.FirstOrDefault()?.Recipe is { } preparedWith &&
            (recipe.ExcludeCode != preparedWith.ExcludeCode || recipe.Pronunciation != preparedWith.Pronunciation || recipe.Profile != preparedWith.Profile))
            throw new ArgumentException("Pronunciation or text preparation changed. Start a new comparison so every sample uses the same spoken words and saved pronunciation.");
        if (ComparisonCandidates.Any(c => c.Recipe == recipe)) throw new ArgumentException("This voice setup is already in the comparison. Change its voice, pace or delivery before adding another.");
        if (!ComparisonLocked)
        {
            var spoken = comparisonPrepared ? ComparisonPassage : TextPreparation.Prepare(ComparisonPassage, recipe.ExcludeCode, recipe.Pronunciation, recipe.Profile).Script;
            SetComparisonPassage(spoken); comparisonSpoken = spoken;
        }
        SelectedComparison = new("Sample " + (ComparisonCandidates.Count + 1), recipe);
        ComparisonCandidates.Add(SelectedComparison); Raise(nameof(ComparisonLocked)); Raise(nameof(ComparisonSummary));
    }
    private async Task RenderSelectedComparisonAsync()
    {
        var candidate = SelectedComparison ?? throw new ArgumentException("Add and select a voice setup first.");
        if (queue.PowerSuspended) throw new IOException("Recheck wake recovery before rendering a comparison.");
        if (candidate.Audio is not null) throw new ArgumentException("This sample is ready. Choose Play selected sample; replay does not generate new audio.");
        ComparisonBusy = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        try
        {
            comparisonCancellation = cancellation; await StopPlaybackAsync(false, false);
            candidate.Status = "Waiting for local speech…";
            var options = candidate.Recipe;
            var snapshot = new NarrationSettings(options.Engine, options.Voice, options.Speed, options.ExcludeCode, options.Pronunciation, "", options.Profile, LocalVoice: options.LocalVoice);
            var audio = await auditions.GenerateComparisonAsync(comparisonSpoken, snapshot, Ffmpeg, cancellation.Token,
                notice => Application.Current.Dispatcher.InvokeAsync(() => candidate.Status = notice));
            if (cancellation.IsCancellationRequested) { await auditions.RemoveAsync(audio); cancellation.Token.ThrowIfCancellationRequested(); }
            candidate.Audio = audio; candidate.Status = "Ready · " + WaveAudio.Inspect(OwnedFileRemoval.Resolve(Workspace.Root, audio.RelativePath)).Duration.ToString("0.0") + " seconds · replay without regeneration";
            StatusMessage = "Comparison sample ready. Select it and choose Play selected sample.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { candidate.Status = "Stopped · choose Render selected sample to try again"; }
        catch { candidate.Status = "Rendering failed · see the message below, then retry"; throw; }
        finally { comparisonCancellation = null; ComparisonBusy = false; }
    }
    internal async Task ClearComparisonAsync()
    {
        comparisonCancellation?.Cancel();
        if (comparisonTask is { IsCompleted: false } pending) { try { await pending; } catch { /* The render command reports its own failure. */ } }
        await StopPlaybackAsync(false);
        foreach (var candidate in ComparisonCandidates)
            if (candidate.Audio is { } audio) { await auditions.RemoveAsync(audio); candidate.Audio = null; }
        ComparisonCandidates.Clear(); SelectedComparison = null; comparisonSpoken = "";
        Raise(nameof(ComparisonLocked)); Raise(nameof(ComparisonSummary));
    }
    private void InvalidateComparisonAudio()
    {
        foreach (var candidate in ComparisonCandidates) { candidate.Audio = null; candidate.Status = "Cleared after wake recovery · render again"; }
    }
}
