using CommuteCast.Core;
using CommuteCast.Infrastructure;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CommuteCast.Desktop;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Raise(name); return true;
    }
}
public sealed class AsyncCommand(Func<object?, Task> execute, Action<Exception> failure) : ICommand
{
    private bool running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !running;
    public async void Execute(object? parameter)
    {
        if (running) return;
        running = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await execute(parameter); }
        catch (Exception error) { failure(error); }
        finally { running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
}
public sealed class JobView(Job job, Workspace workspace)
{
    public Job Job { get; } = job;
    public string Id => Job.Id;
    public string Title => Job.Title;
    public string Submitted => Job.CreatedUtc.ToLocalTime().ToString("MMM d, yyyy · h:mm tt zzz");
    public string Status => Job.CancellationRequested && !Job.ExportCommitted && Job.Stage != JobStage.Cancelled
        ? "Cancelling · waiting for active work to stop" : Job.Stage switch
    {
        JobStage.WaitingForService => "Waiting for local speech service",
        JobStage.Synthesizing => $"Narrating · {Job.CompletedChunks} of {Job.Chunks.Count} chunks validated",
        JobStage.Exported => "Exported locally · upload unknown",
        JobStage.Generated => "Generated · ready to export",
        JobStage.Failed => $"Needs attention at {Job.FailedStage?.ToString() ?? "reconciliation"} · preserved for retry",
        JobStage.Cancelled => "Cancelled · ready to resume",
        _ => Job.Stage.ToString()
    };
    public double Progress => Job.CompletedChunks;
    public double ChunkTotal => Math.Max(1, Job.Chunks.Count);
    public string Details => $"{Job.Settings.Engine} · {Job.Settings.Voice}\n{Job.Settings.Speed:0.00}× pace · {Job.Source.Length:N0} source characters\n{Job.CompletedChunks}/{Job.Chunks.Count} validated chunks" + (Job.DurationSeconds > 0 ? "\n" + TimeSpan.FromSeconds(Job.DurationSeconds).ToString(@"hh\:mm\:ss") + " audio" : "");
    public string Error => Job.Error;
    public string Delivery => Job.Stage == JobStage.Exported
        ? (File.Exists(Path.Combine(Job.Destination, Job.ExportName)) ? "Finished MP3 exists in the selected local folder. Cloud upload is unknown. Check OneDrive and your phone." : "The exported file is missing or moved. History is preserved. Cloud upload is unknown.")
        : File.Exists(workspace.FinalPath(Job)) && Job.FinalHash.Length > 0 ? "Generated audio is saved privately on this laptop. It has not been exported." : "Audio is generated privately on this laptop before publication.";
}

public sealed class MainViewModel : Observable, IAsyncDisposable
{
    public Workspace Workspace { get; }
    private readonly AppSettings settings;
    private readonly LocalSpeechProvider provider;
    private readonly ExportPublisher publisher;
    private readonly QueueCoordinator queue;
    private readonly SqliteJobStore store;
    private readonly DraftStore drafts;
    private readonly MediaPlayer player = new();
    private readonly CancellationTokenSource shutdown = new();
    private CancellationTokenSource? draftSave;
    private readonly SemaphoreSlim draftGate = new(1);
    private string page = "compose", source = "", draftTitle = "", statusMessage = "Paste something worth listening to. Queue it when you're ready.", serviceStatus = "Checking local speech…";
    private string providerDetails = "", encoderVersion = "Not checked";
    private string storageSummary = "Usage has not been measured.";
    private bool loading = true, queueLoaded, draftLoadFailed, draftDirty;
    private JobView? selectedJob;
    public ObservableCollection<JobView> Jobs { get; } = [];
    public ObservableCollection<string> Voices { get; } = [];
    public string[] Engines { get; } = ["kokoro", "piper"];
    public event Action? DraftQueued;
    public string PageHeading => page switch { "library" => "Your listening library", "settings" => "Settle in. Set it up.", _ => "Make time to listen." };
    public bool IsCompose => page == "compose";
    public bool IsLibrary => page == "library";
    public bool IsSettings => page == "settings";
    public string Source
    {
        get => source;
        set
        {
            if (!Set(ref source, value)) return;
            Raise(nameof(CharacterCount));
            if (DraftTitle.Length == 0 && source.Length > 0) DraftTitle = TextPreparation.SuggestTitle(source[..Math.Min(source.Length, 2000)]);
            ScheduleDraftSave();
        }
    }
    public string DraftTitle { get => draftTitle; set { if (Set(ref draftTitle, value)) ScheduleDraftSave(); } }
    public string CharacterCount => $"{source.Length:N0} / {TextPreparation.MaximumCharacters:N0} characters";
    public string Engine { get => settings.Engine; set { if (settings.Engine == value || value is null) return; settings.Engine = value; settings.Voice = value == "kokoro" ? "af_heart" : "en_US-lessac-medium"; Voices.Clear(); Voices.Add(settings.Voice); Raise(); Raise(nameof(Voice)); ServiceStatus = "Check readiness to refresh voices"; } }
    public string Voice { get => settings.Voice; set { if (value is not null) { settings.Voice = value; Raise(); } } }
    public double Speed { get => settings.Speed; set { settings.Speed = Math.Round(value, 2); Raise(); Raise(nameof(SpeedLabel)); } }
    public string SpeedLabel => $"{Speed:0.00}×";
    public bool ExcludeCode { get => settings.ExcludeCode; set { settings.ExcludeCode = value; Raise(); } }
    public string Pronunciation { get => settings.Pronunciation; set { settings.Pronunciation = value; Raise(); } }
    public string Ffmpeg { get => settings.Ffmpeg; set { settings.Ffmpeg = value; Raise(); } }
    public string Ffprobe { get => settings.Ffprobe; set { settings.Ffprobe = value; Raise(); } }
    public string DestinationDisplay => settings.Destination.Length == 0 ? "Choose your local OneDrive folder" : settings.Destination;
    public string StorageDetails => $"Private data: {Workspace.Root}\n{storageSummary}\nSource, history, finished MP3s, active artifacts and retryable chunks are retained. Delete narrations to remove those files. Local migration backups and recovered draft copies are retained separately.";
    public int CacheQuotaMiB { get => settings.CacheQuotaMiB; set { settings.CacheQuotaMiB = value; Raise(); } }
    public int ScratchRetentionDays { get => settings.ScratchRetentionDays; set { settings.ScratchRetentionDays = value; Raise(); } }
    public int PrivateStorageLimitMiB { get => settings.PrivateStorageLimitMiB; set { settings.PrivateStorageLimitMiB = value; Raise(); } }
    public string ProviderDetails { get => providerDetails; private set => Set(ref providerDetails, value); }
    public string ServiceStatus { get => serviceStatus; private set => Set(ref serviceStatus, value); }
    public string StatusMessage { get => statusMessage; private set => Set(ref statusMessage, value); }
    public string LibrarySummary => !queueLoaded ? "Local records have not loaded. Repair storage or restore a verified backup." : $"{Jobs.Count} narrations · {Jobs.Count(j => j.Job.Stage == JobStage.Queued)} queued · {Jobs.Count(j => j.Job.Stage == JobStage.Exported)} exported locally";
    public string PauseLabel => queue.Paused ? "Resume queue" : "Pause future jobs";
    public JobView? SelectedJob { get => selectedJob; set => Set(ref selectedJob, value); }
    public ICommand NavigateCommand { get; }
    public ICommand QueueCommand { get; }
    public ICommand ReviewCommand { get; }
    public ICommand ChooseFolderCommand { get; }
    public ICommand TestFolderCommand { get; }
    public ICommand ReadinessCommand { get; }
    public ICommand AuditionCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand CheckEncoderCommand { get; }
    public ICommand ThemeCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand MoveEarlierCommand { get; }
    public ICommand MoveLaterCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand InspectCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand ReplaceDestinationCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ReuseCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand DeleteAllCommand { get; }
    public ICommand DiagnosticsCommand { get; }
    public ICommand MeasureStorageCommand { get; }
    public ICommand CleanCacheCommand { get; }

    public MainViewModel(AppSettings saved, Workspace? workspace = null)
    {
        settings = saved; Workspace = workspace ?? new();
        drafts = new(Workspace);
        store = new SqliteJobStore(Workspace);
        provider = new(Workspace); publisher = new(Workspace, store);
        queue = new(Workspace, store, provider, new(settings), publisher) { Paused = settings.QueuePaused, CacheQuotaMiB = settings.CacheQuotaMiB, ScratchRetentionDays = settings.ScratchRetentionDays, PrivateStorageLimitMiB = settings.PrivateStorageLimitMiB };
        queue.Changed += snapshots => Application.Current.Dispatcher.InvokeAsync(() => RefreshJobs(snapshots));
        player.MediaFailed += (_, _) => StatusMessage = "Playback failed. Check that the local audio exists and is decodable.";
        Voices.Add(settings.Voice);
        NavigateCommand = Command(p => { Navigate(p?.ToString() ?? "compose"); return Task.CompletedTask; });
        QueueCommand = Command(_ => SubmitAsync());
        ReviewCommand = Command(async _ => { var text = Source; var omit = ExcludeCode; var dictionary = Pronunciation; var prepared = await Task.Run(() => TextPreparation.Prepare(text, omit, dictionary)); ShowPreparation(prepared, text); });
        ChooseFolderCommand = Command(async _ => { var path = ChooseFolder(); if (path is not null) { await publisher.TestDestinationAsync(path); settings.Destination = path; Raise(nameof(DestinationDisplay)); await SaveSettingsAsync(); StatusMessage = "Output folder saved. Only completed MP3s will be exported here."; } });
        TestFolderCommand = Command(async _ => { await publisher.TestDestinationAsync(settings.Destination); StatusMessage = "Local write access passed. OneDrive cloud upload is still unknown."; });
        ReadinessCommand = Command(_ => CheckReadinessAsync(true));
        AuditionCommand = Command(_ => AuditionAsync());
        SaveSettingsCommand = Command(async _ => { TextPreparation.ParseDictionary(Pronunciation); await SaveSettingsAsync(); StatusMessage = "Settings saved for future submissions."; });
        CheckEncoderCommand = Command(_ => CheckEncoderAsync());
        ThemeCommand = Command(_ => { App.ToggleTheme(); return Task.CompletedTask; });
        PauseCommand = Command(async _ => { queue.Paused = !queue.Paused; settings.QueuePaused = queue.Paused; Raise(nameof(PauseLabel)); await SaveSettingsAsync(); StatusMessage = queue.Paused ? "Future dispatch paused. The current narration can finish." : "Queue resumed."; });
        MoveEarlierCommand = Command(_ => queue.MovePendingAsync(RequireSelected().Id, -1, shutdown.Token));
        MoveLaterCommand = Command(_ => queue.MovePendingAsync(RequireSelected().Id, 1, shutdown.Token));
        PlayCommand = Command(async _ => { var job = RequireSelected(); var file = Workspace.FinalPath(job); if (!File.Exists(file) || job.FinalHash.Length == 0 || await Infrastructure.Workspace.HashFileAsync(file) != job.FinalHash) throw new IOException("No validated local audio is available for this narration."); player.Open(new Uri(file)); player.Play(); StatusMessage = "Playing local audio. Use Stop playback to stop."; });
        StopCommand = Command(_ => { player.Stop(); return Task.CompletedTask; });
        OpenFolderCommand = Command(_ => { var job = RequireSelected(); if (!Directory.Exists(job.Destination)) throw new IOException("The recorded output folder is missing."); Process.Start(new ProcessStartInfo(job.Destination) { UseShellExecute = true }); return Task.CompletedTask; });
        InspectCommand = Command(_ => { var job = RequireSelected(); ShowPreparation(job.Prepared, job.Source); return Task.CompletedTask; });
        RetryCommand = Command(async _ => { var job = RequireSelected(); await provider.ResetRecoveryBudgetAsync(job.Settings.Engine, shutdown.Token); await queue.RetryAsync(job.Id); });
        ReplaceDestinationCommand = Command(async _ => { var job = RequireSelected(); var path = ChooseFolder(); if (path is not null) { await publisher.TestDestinationAsync(path); await queue.RetryAsync(job.Id, path); } });
        CancelCommand = Command(async _ => { var id = RequireSelected().Id; await queue.CancelAsync(id); StatusMessage = "Cancellation settled. Exported files remain exported; validated chunks are retained for retry."; });
        ReuseCommand = Command(_ => { var job = RequireSelected(); DraftTitle = job.Title; Source = job.Source; Engine = job.Settings.Engine; Voice = job.Settings.Voice; Speed = job.Settings.Speed; ExcludeCode = job.Settings.ExcludeCode; Pronunciation = job.Settings.Pronunciation; Navigate("compose"); return Task.CompletedTask; });
        DeleteCommand = Command(_ => DeleteAsync(false));
        DeleteAllCommand = Command(_ => DeleteAsync(true));
        DiagnosticsCommand = Command(_ => DiagnosticsAsync());
        MeasureStorageCommand = Command(_ => RefreshStorageAsync());
        CleanCacheCommand = Command(async _ => { ValidateRetention(); queue.CacheQuotaMiB = CacheQuotaMiB; queue.ScratchRetentionDays = ScratchRetentionDays; var result = await queue.CleanCacheAsync(shutdown.Token); await RefreshStorageAsync(); StatusMessage = $"Cleanup removed {result.FilesRemoved} files ({result.BytesRemoved / 1048576.0:0.0} MiB). {result.Failures} files could not be removed. Protected data and exports are retained."; });
    }
    private ICommand Command(Func<object?, Task> action) => new AsyncCommand(action, e => StatusMessage = QueueCoordinator.FriendlyError(e));
    private void Navigate(string value) { page = value; Raise(nameof(IsCompose)); Raise(nameof(IsLibrary)); Raise(nameof(IsSettings)); Raise(nameof(PageHeading)); }
    private Job RequireSelected() => SelectedJob?.Job ?? throw new ArgumentException("Select a narration first.");
    private void RefreshJobs(IReadOnlyList<Job> snapshots)
    {
        var id = SelectedJob?.Id;
        Jobs.Clear();
        foreach (var job in snapshots.Where(j => j.Stage == JobStage.Queued).Concat(snapshots.Where(j => j.Stage != JobStage.Queued).OrderByDescending(j => j.CreatedUtc))) Jobs.Add(new(job, Workspace));
        SelectedJob = Jobs.FirstOrDefault(j => j.Id == id) ?? Jobs.FirstOrDefault();
        Raise(nameof(LibrarySummary));
        Raise(nameof(PauseLabel));
        if (queue.PersistenceError.Length > 0) StatusMessage = queue.PersistenceError;
    }
    public async Task InitializeAsync()
    {
        try { var saved = await drafts.LoadAsync(shutdown.Token); DraftTitle = saved.Title; Source = saved.Source; }
        catch (IOException error) { draftLoadFailed = true; StatusMessage = error.Message; }
        loading = false;
        await Task.Run(() => queue.InitializeAsync(shutdown.Token)); queueLoaded = true; Raise(nameof(LibrarySummary));
        try { await CheckReadinessAsync(); } catch (Exception error) { ServiceStatus = "Speech needs setup"; StatusMessage = QueueCoordinator.FriendlyError(error); }
    }
    private void ScheduleDraftSave()
    {
        if (loading) return;
        draftDirty = true;
        draftSave?.Cancel();
        draftSave = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        _ = SaveDraftAsync(DraftTitle, Source, draftSave.Token);
    }
    private async Task SaveDraftAsync(string title, string text, CancellationToken ct)
    {
        try
        {
            await Task.Delay(500, ct);
            await draftGate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested(); var preserved = await drafts.SaveAsync(new(title, text), ct); draftLoadFailed = false;
                if (preserved is not null) StatusMessage = "The unreadable original draft was preserved in local recovery storage. Your current draft is saved.";
            }
            finally { draftGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { StatusMessage = "Draft autosave failed. Keep this window open and check disk space."; }
    }
    private Task SaveSettingsAsync()
    {
        ValidateRetention(); queue.CacheQuotaMiB = CacheQuotaMiB; queue.ScratchRetentionDays = ScratchRetentionDays; queue.PrivateStorageLimitMiB = PrivateStorageLimitMiB; settings.QueuePaused = queue.Paused;
        return Workspace.SaveSettingsAsync(settings);
    }
    private void ValidateRetention()
    {
        if (CacheQuotaMiB is < 1 or > 102400 || ScratchRetentionDays is < 1 or > 3650 || PrivateStorageLimitMiB is < 128 or > 1048576)
            throw new ArgumentException("Use cache quota 1–102400 MiB, age 1–3650 days, and private storage limit 128–1048576 MiB.");
    }
    private async Task RefreshStorageAsync()
    {
        var usage = await queue.MeasureStorageAsync(shutdown.Token);
        storageSummary = $"{usage.TotalBytes / 1048576.0:0.0} MiB total · {usage.CacheBytes / 1048576.0:0.0} MiB intermediates · {usage.ReclaimableBytes / 1048576.0:0.0} MiB eligible for cleanup · {usage.ReservedBytes / 1048576.0:0.0} MiB reserved for pending work.";
        if (queue.MaintenanceError.Length > 0) storageSummary += "\nAutomatic cleanup needs attention: " + queue.MaintenanceError;
        Raise(nameof(StorageDetails));
    }
    private async Task<ProviderInfo> CheckReadinessAsync(bool explicitRetry = false)
    {
        var engine = Engine;
        ServiceStatus = "Checking " + engine + " locally…";
        var info = await provider.ReadyAsync(engine, shutdown.Token, explicitRetry);
        settings.Providers[engine] = info;
        await SaveSettingsAsync();
        if (engine == Engine)
        {
            var voice = Voice;
            Voices.Clear(); foreach (var item in info.Voices) Voices.Add(item);
            Voice = info.Voices.Contains(voice) ? voice : info.Voices.First();
            ServiceStatus = engine + " · ready on this laptop";
            ProviderDetails = $"Contract v1 · {info.Engine}\nModel: {info.Fingerprint}\nEncoder: {encoderVersion}";
        }
        return info;
    }
    private async Task SubmitAsync()
    {
        if (!queueLoaded) throw new IOException("Local queue records have not loaded. Repair storage or restore a verified compatible backup before submitting. Your draft is retained.");
        var text = Source; var title = DraftTitle.Trim();
        var engine = Engine; var voice = Voice; var speed = Speed; var exclusion = ExcludeCode; var dictionary = Pronunciation; var destination = settings.Destination;
        var prepared = await Task.Run(() => TextPreparation.Prepare(text, exclusion, dictionary));
        ValidateRetention();
        await queue.CleanCacheAsync(shutdown.Token);
        var usage = await queue.MeasureStorageAsync(shutdown.Token);
        var drive = new DriveInfo(Path.GetPathRoot(Workspace.Root)!);
        StorageBudget.EnsureFits(usage, prepared.Script, PrivateStorageLimitMiB, drive.AvailableFreeSpace);
        if (title.Length == 0) title = TextPreparation.SuggestTitle(text);
        await publisher.TestDestinationAsync(destination, shutdown.Token);
        if (!settings.Providers.TryGetValue(engine, out var info)) info = await CheckReadinessAsync();
        if (!info.Voices.Contains(voice)) throw new ArgumentException("Select an installed voice after checking readiness.");
        var job = new Job { Title = title, Source = text, Prepared = prepared, Settings = new(engine, voice, speed, exclusion, dictionary, info.Fingerprint), Destination = destination };
        await queue.AddAsync(job, shutdown.Token);
        if (Source == text) { Source = ""; DraftTitle = ""; }
        StatusMessage = "Narration saved to the durable queue. Follow progress in Your library, or paste your next narration here.";
        DraftQueued?.Invoke();
        await SaveSettingsAsync();
    }
    private async Task AuditionAsync()
    {
        var engine = Engine; var voice = Voice; var speed = Speed;
        await queue.InferenceGate.WaitAsync(shutdown.Token);
        try
        {
            var info = await provider.ReadyAsync(engine, shutdown.Token, true);
            var path = Path.Combine(Workspace.Root, "audition.wav");
            player.Stop(); player.Close();
            await provider.SynthesizeAsync(new(engine, voice, speed, false, "", info.Fingerprint), "Welcome to CommuteCast. A good idea deserves a little more time. Let's take this one on the road. The API processes twenty-four requests per second.", path, shutdown.Token);
            player.Open(new Uri(path)); player.Play();
            StatusMessage = "Playing a short local voice audition. Choose the voice and pace that suit your listening.";
        }
        finally { queue.InferenceGate.Release(); }
    }
    private async Task CheckEncoderAsync()
    {
        var encoder = await ProcessRunner.RunAsync(Ffmpeg, ["-version"], TimeSpan.FromSeconds(10), shutdown.Token);
        var probe = await ProcessRunner.RunAsync(Ffprobe, ["-version"], TimeSpan.FromSeconds(10), shutdown.Token);
        if (encoder.ExitCode != 0 || probe.ExitCode != 0) throw new IOException("FFmpeg or FFprobe did not respond successfully.");
        encoderVersion = encoder.Output.Split('\n')[0];
        ProviderDetails += "\n" + encoderVersion;
        StatusMessage = "FFmpeg and FFprobe are available. Full audio validation runs for each narration.";
    }
    private static string? ChooseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose your existing local corporate OneDrive output folder", Multiselect = false };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FolderName : null;
    }
    private static void ShowPreparation(PreparedText prepared, string source) => new PreparationWindow(prepared, source) { Owner = Application.Current.MainWindow }.ShowDialog();
    private async Task DeleteAsync(bool all)
    {
        var ids = all ? Jobs.Select(j => j.Id).ToArray() : [RequireSelected().Id];
        if (ids.Length == 0) { StatusMessage = "There are no managed narrations to delete."; return; }
        var choice = new DeleteWindow(ids.Length) { Owner = Application.Current.MainWindow };
        if (choice.ShowDialog() != true) return;
        player.Stop(); player.Close();
        var pausedBefore = queue.Paused;
        queue.Paused = true;
        var errors = new List<string>();
        try
        {
            foreach (var id in ids)
                try { await queue.DeleteAsync(id, choice.DeleteExports); } catch (Exception error) { errors.Add(QueueCoordinator.FriendlyError(error)); }
        }
        finally { queue.Paused = pausedBefore; }
        StatusMessage = errors.Count == 0 ? $"Deleted {ids.Length} managed narrations. Unrelated files were preserved. External retention and phone copies remain outside this app's control." : $"{errors.Count} items could not be fully removed: {string.Join(" ", errors.Distinct())}";
    }
    private async Task DiagnosticsAsync()
    {
        var dialog = new SaveFileDialog { Title = "Export redacted diagnostics", FileName = "CommuteCast-diagnostics.json", Filter = "JSON file|*.json" };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;
        var package = await Task.Run(() => new DiagnosticExporter(Workspace, store).BuildAsync(queue.Snapshot(), encoderVersion, shutdown.Token));
        await File.WriteAllTextAsync(dialog.FileName, package, shutdown.Token);
        StatusMessage = "Redacted diagnostics exported. No source, script, title, audio, or corporate path is included.";
    }
    public async ValueTask DisposeAsync()
    {
        draftSave?.Cancel(); shutdown.Cancel(); player.Close();
        await queue.DisposeAsync();
        await draftGate.WaitAsync();
        try { if (!draftLoadFailed || draftDirty) await drafts.SaveAsync(new(DraftTitle, Source)); }
        finally { draftGate.Release(); }
        await SaveSettingsAsync();
        provider.Dispose(); shutdown.Dispose();
    }
}
