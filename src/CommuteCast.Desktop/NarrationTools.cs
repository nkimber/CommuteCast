using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using CommuteCast.Core;
using CommuteCast.Infrastructure;
using Microsoft.Win32;

namespace CommuteCast.Desktop;

public sealed partial class MainViewModel
{
    private NarrationPresets presetLibrary = null!;
    private CancellationTokenSource? estimateCancellation;
    private NarrationPreset? selectedPreset;
    private string presetName = "", estimateSummary = "Paste text to see its word count and calibrated estimates.";
    public ObservableCollection<NarrationPreset> Presets { get; } = [];
    public NarrationPreset? SelectedPreset { get => selectedPreset; set { if (Set(ref selectedPreset, value) && value is not null) PresetName = value.Name; } }
    public string PresetName { get => presetName; set => Set(ref presetName, value); }
    public string EstimateSummary { get => estimateSummary; private set => Set(ref estimateSummary, value); }
    public ICommand ApplyPresetCommand { get; private set; } = null!;
    public ICommand SavePresetCommand { get; private set; } = null!;
    public ICommand RemovePresetCommand { get; private set; } = null!;
    public ICommand ImportCommand { get; private set; } = null!;

    private void InitializeNarrationTools()
    {
        presetLibrary = new(settings); RefreshPresets();
        ApplyPresetCommand = Command(_ =>
        {
            var preset = SelectedPreset ?? throw new ArgumentException("Choose a narration preset.");
            ApplyNarrationOptions(preset.Options); StatusMessage = $"Applied {preset.Name} for this narration. Save as my defaults to use it for future narrations.";
            return Task.CompletedTask;
        });
        SavePresetCommand = Command(async _ =>
        {
            var previous = settings.NarrationPresets!.ToList();
            presetLibrary.Save(PresetName, narrationPreferences.Current);
            try { await SaveSettingsAsync(); }
            catch { settings.NarrationPresets = previous; throw; }
            RefreshPresets(PresetName.Trim()); StatusMessage = "Preset saved with the displayed voice, pace and pronunciation choices. Saved defaults and queued narrations are unchanged.";
        });
        RemovePresetCommand = Command(async _ =>
        {
            var preset = SelectedPreset ?? throw new ArgumentException("Choose a preset to remove.");
            var previous = settings.NarrationPresets!.ToList(); presetLibrary.Remove(preset);
            try { await SaveSettingsAsync(); }
            catch { settings.NarrationPresets = previous; throw; }
            RefreshPresets(); StatusMessage = "Preset removed. Your current narration choices remain available.";
        });
        ImportCommand = Command(async parameter =>
        {
            var path = parameter as string;
            if (path is null)
            {
                var dialog = new OpenFileDialog { Title = "Import narration text", Filter = "Text and Markdown|*.txt;*.md", Multiselect = false };
                if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;
                path = dialog.FileName;
            }
            await ImportTextAsync(path, () => MessageBox.Show(Application.Current.MainWindow, "Replace the current draft with the imported text?", "Import text", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK);
        });
    }
    private void RefreshPresets(string? name = null)
    {
        SelectedPreset = null; Presets.Clear();
        foreach (var preset in presetLibrary.Items) Presets.Add(preset);
        SelectedPreset = Presets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? Presets.FirstOrDefault();
    }
    internal void ApplyNarrationOptions(NarrationOptions options)
    {
        options.Validate(); var previousEngine = Engine;
        if (settings.Providers.TryGetValue(options.Engine, out var installed) && installed.State == "ready" && !installed.Voices.Contains(options.Voice))
            throw new ArgumentException("This preset's voice is unavailable. Refresh the voice library or update the preset to an installed voice.");
        options.ApplyTo(settings);
        RefreshVoiceChoices(); Raise(nameof(Engine)); Raise(nameof(Speed)); Raise(nameof(SpeedLabel));
        Raise(nameof(ExcludeCode)); Raise(nameof(Pronunciation)); RaiseProfile();
        if (previousEngine != Engine && !loading) _ = RefreshChangedEngineAsync(Engine);
    }
    internal async Task<bool> ImportTextAsync(string path, Func<bool> confirmReplace)
    {
        var text = await TextFileImport.ReadAsync(path, shutdown.Token);
        shutdown.Token.ThrowIfCancellationRequested();
        if ((Source.Length > 0 || DraftTitle.Length > 0) && !confirmReplace()) return false;
        DraftTitle = TextPreparation.SuggestTitle(text); Source = text; Navigate("compose");
        StatusMessage = "Text imported into your local draft. Review the spoken text, then choose Create MP3 when ready.";
        return true;
    }
    private void ScheduleEstimate()
    {
        estimateCancellation?.Cancel(); estimateCancellation?.Dispose();
        if (shutdown.IsCancellationRequested) return;
        EstimateSummary = string.IsNullOrWhiteSpace(Source) ? "Paste text to see its word count and calibrated estimates." : "Updating spoken-word count and calibrated estimates…";
        estimateCancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        _ = EstimateAsync(estimateCancellation.Token);
    }
    private async Task EstimateAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(450, ct);
            if (string.IsNullOrWhiteSpace(Source)) { EstimateSummary = "Paste text to see its word count and calibrated estimates."; return; }
            var text = Source; var choices = narrationPreferences.Current;
            settings.Providers.TryGetValue(choices.Engine, out var info);
            var history = Jobs.Select(j => j.Job).ToArray();
            var estimate = await Task.Run(() =>
            {
                var prepared = TextPreparation.Prepare(text, choices.ExcludeCode, choices.Pronunciation, choices.Profile, ct);
                return NarrationEstimate.Calculate(prepared.Script, choices, info, history);
            }, ct);
            ct.ThrowIfCancellationRequested();
            EstimateSummary = $"{estimate.WordCount:N0} spoken words · " + (estimate.Listening is null
                ? $"Estimates need {NarrationEstimate.RequiredSamples} completed narrations of at least 20 spoken words with this voice/model ({estimate.SampleCount} available)."
                : $"Estimated listening {NarrationEstimate.FormatRange(estimate.Listening)} · " + (estimate.Generation is { } generation
                    ? $"Generation {NarrationEstimate.FormatRange(generation)} while active, excluding queued time. Observed service/loading waits included; a new cold start may take longer."
                    : "Generation estimate needs three completed narrations with service-timing measurements.") + $" Based on {estimate.SampleCount} local jobs; technical content can vary.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!ct.IsCancellationRequested) EstimateSummary = "Word count/estimate unavailable until preparation succeeds: " + QueueCoordinator.FriendlyError(error);
        }
    }
}
