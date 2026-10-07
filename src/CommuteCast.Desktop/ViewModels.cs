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
public sealed record PronunciationOption<T>(T Value, string Label);
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
public sealed class JobView(Job job, Workspace workspace, bool paused = false)
{
    public Job Job { get; } = job;
    public string Id => Job.Id;
    public string Title => Job.Title;
    public string Submitted => Job.CreatedUtc.ToLocalTime().ToString("MMM d, yyyy · h:mm tt zzz");
    private JobProgress Presentation => JobProgress.Describe(Job, paused);
    public string Status => Presentation.Status;
    public string Guidance => Presentation.Guidance;
    public bool IsWorking => Presentation.IsWorking;
    public bool NeedsAttention => Presentation.NeedsAttention;
    public double Progress => Job.CompletedChunks;
    public double ChunkTotal => Math.Max(1, Job.Chunks.Count);
    public string Details => $"{Job.Settings.Engine} · {Job.Settings.Voice}\n{Job.Settings.Speed:0.00}× pace · {Job.Source.Length:N0} source characters\n{Job.CompletedChunks}/{Job.Chunks.Count} validated chunks\n" + (Job.Settings.Profile is { } p ? $"{p.Language} · {p.Numbers} · {p.Acronyms} · {p.Dates}" : "Legacy literal pronunciation") + (Job.FailedStage is { } stage ? $"\nStopped during: {stage}" : "") + (Job.DurationSeconds > 0 ? "\n" + TimeSpan.FromSeconds(Job.DurationSeconds).ToString(@"hh\:mm\:ss") + " audio" : "");
    public string Error => string.Join(Environment.NewLine, new[] { Job.Error,
        string.IsNullOrWhiteSpace(Job.ExportNotice) || Job.Error.Contains(Job.ExportNotice, StringComparison.Ordinal) ? "" : Job.ExportNotice,
        Job.PrivateStorageNotice }.Where(s => !string.IsNullOrWhiteSpace(s)));
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
    private readonly AuditionGenerator auditions;
    private readonly SemaphoreSlim auditionCleanup = new(1);
    private CancellationTokenSource? activeAudition;
    private AuditionAudio? auditionAudio;
    private long auditionGeneration;
    private string selectionSource = "";
    private int selectionStart, selectionLength;
    private readonly CancellationTokenSource shutdown = new();
    private CancellationTokenSource? draftSave;
    private readonly SemaphoreSlim draftGate = new(1);
    private readonly OperationLifetime operations = new();
    private Task? disposal;
    private string page = "compose", source = "", draftTitle = "", statusMessage = "Paste something worth listening to. Queue it when you're ready.", serviceStatus = "Checking local speech…";
    private string providerDetails = "", encoderVersion = "Not checked";
    private string operationError = "", speechError = "";
    private string setupDetails = "Setup has not been checked. This inspection does not start or change speech services.";
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
            if (DraftTitle.Length == 0 && source.Length > 0) DraftTitle = TextPreparation.SuggestTitle(source[..Math.Min(source.Length, TextPreparation.MaximumCharacters)]);
            ScheduleDraftSave();
        }
    }
    public string DraftTitle { get => draftTitle; set { if (Set(ref draftTitle, value)) ScheduleDraftSave(); } }
    public string CharacterCount => $"{source.Length:N0} / {TextPreparation.MaximumCharacters:N0} characters";
    public bool HasAuditionSelection => selectionLength is > 0 and <= AuditionRequest.MaximumCharacters;
    public string AuditionSelectionSummary => selectionLength == 0 ? "Select a short passage in your text to hear it."
        : selectionLength > AuditionRequest.MaximumCharacters ? $"{selectionLength:N0} selected. Shorten the selection to {AuditionRequest.MaximumCharacters} characters."
        : $"{selectionLength:N0} selected · prepared as a short local sample";
    public void UpdateAuditionSelection(string text, int start, int length)
    {
        selectionSource = text; selectionStart = start; selectionLength = length;
        Raise(nameof(HasAuditionSelection)); Raise(nameof(AuditionSelectionSummary));
    }
    public string Engine { get => settings.Engine; set { if (settings.Engine == value || value is null) return; settings.Engine = value; settings.Voice = value == "kokoro" ? "af_heart" : "en_US-lessac-medium"; Voices.Clear(); Voices.Add(settings.Voice); Raise(); Raise(nameof(Voice)); RaiseProfile(); ServiceStatus = "Check readiness to refresh voices"; } }
    public string Voice { get => settings.Voice; set { if (value is not null) { settings.Voice = value; Raise(); } } }
    public double Speed { get => settings.Speed; set { settings.Speed = Math.Round(value, 2); Raise(); Raise(nameof(SpeedLabel)); } }
    public string SpeedLabel => $"{Speed:0.00}×";
    public bool ExcludeCode { get => settings.ExcludeCode; set { settings.ExcludeCode = value; Raise(); } }
    public string Pronunciation { get => settings.Pronunciation; set { settings.Pronunciation = value; Raise(); } }
    public PronunciationOption<NumberReading>[] NumberOptions { get; } = [new(NumberReading.AsWritten, "Keep numbers as written"), new(NumberReading.LiteralDigits, "Read each digit and symbol"), new(NumberReading.NumberWords, "Read integer and decimal values"), new(NumberReading.ScientificWords, "Read values and scientific exponents")];
    public PronunciationOption<AcronymReading>[] AcronymOptions { get; } = [new(AcronymReading.AsWritten, "Keep uppercase words as written"), new(AcronymReading.SpellUppercaseWords, "Spell uppercase words (2–32 letters)")];
    public PronunciationOption<DateReading>[] DateOptions { get; } = [new(DateReading.NoCalendarInterpretation, "No calendar interpretation"), new(DateReading.IsoYearMonthDay, "ISO dates: yyyy-MM-dd"), new(DateReading.MonthDayYear, "Month/day/year: M/d/yyyy"), new(DateReading.DayMonthYear, "Day/month/year: d/M/yyyy")];
    private PronunciationProfile Profile => settings.PronunciationProfile ?? new();
    public NumberReading NumberStyle { get => Profile.Numbers; set { settings.PronunciationProfile = Profile with { Numbers = value }; RaiseProfile(); } }
    public AcronymReading AcronymStyle { get => Profile.Acronyms; set { settings.PronunciationProfile = Profile with { Acronyms = value }; RaiseProfile(); } }
    public DateReading DateStyle { get => Profile.Dates; set { settings.PronunciationProfile = Profile with { Dates = value }; RaiseProfile(); } }
    public bool PronunciationSupported { get { try { Profile.Validate(Engine); return true; } catch (ArgumentException) { return false; } } }
    public bool PronunciationUnsupported => !PronunciationSupported;
    public string PronunciationCapability => PronunciationSupported
        ? "English preparation for Kokoro and Piper. Dictionary entries take priority. No acronym meanings are guessed. Preview the complete script and exact changes before queueing."
        : "This engine, language or saved profile version is unsupported. Choose Kokoro or Piper and reset to the supported English profile for a new narration.";
    public string ProfileSummary => PronunciationSupported ? $"English · {NumberOptions.First(o => o.Value == NumberStyle).Label}\n{AcronymOptions.First(o => o.Value == AcronymStyle).Label} · {DateOptions.First(o => o.Value == DateStyle).Label}" : PronunciationCapability;
    private void RaiseProfile() { Raise(nameof(NumberStyle)); Raise(nameof(AcronymStyle)); Raise(nameof(DateStyle)); Raise(nameof(PronunciationSupported)); Raise(nameof(PronunciationUnsupported)); Raise(nameof(PronunciationCapability)); Raise(nameof(ProfileSummary)); }
    private PronunciationProfile CaptureProfile() { var profile = Profile; profile.Validate(Engine); return profile; }
    public string Ffmpeg { get => settings.Ffmpeg; set { settings.Ffmpeg = value; Raise(); } }
    public string Ffprobe { get => settings.Ffprobe; set { settings.Ffprobe = value; Raise(); } }
    public string DestinationDisplay => settings.Destination.Length == 0 ? "Choose your local OneDrive folder" : settings.Destination;
    public string StorageDetails => $"Private data: {Workspace.Root}\n{storageSummary}\nSource, history, finished MP3s, active artifacts and retryable chunks are retained. Delete narrations to remove those files. Local migration backups and recovered draft copies are retained separately.";
    public int CacheQuotaMiB { get => settings.CacheQuotaMiB; set { settings.CacheQuotaMiB = value; Raise(); } }
    public int ScratchRetentionDays { get => settings.ScratchRetentionDays; set { settings.ScratchRetentionDays = value; Raise(); } }
    public int PrivateStorageLimitMiB { get => settings.PrivateStorageLimitMiB; set { settings.PrivateStorageLimitMiB = value; Raise(); } }
    public string ProviderDetails { get => providerDetails; private set => Set(ref providerDetails, value); }
    public string SetupDetails { get => setupDetails; private set => Set(ref setupDetails, value); }
    public string ServiceStatus { get => serviceStatus; private set => Set(ref serviceStatus, value); }
    public string StatusMessage { get => statusMessage; private set => Set(ref statusMessage, value); }
    public JobView? LatestJob => Jobs.MaxBy(j => j.Job.CreatedUtc);
    public bool HasLatestJob => LatestJob is not null;
    private JobView? AttentionJob => Jobs.Where(j => j.NeedsAttention).MaxBy(j => j.Job.CreatedUtc);
    public bool HasAttention => AttentionMessage.Length > 0;
    public string AttentionHeading => queue.PersistenceError.Length > 0 ? "Queue stopped · storage needs attention"
        : operationError.Length > 0 ? "Action could not finish"
        : speechError.Length > 0 ? "Speech unavailable · setup needs attention" : "A saved narration needs attention";
    public string AttentionMessage => queue.PersistenceError.Length > 0 ? queue.PersistenceError
        : operationError.Length > 0 ? operationError
        : speechError.Length > 0 ? speechError + "\nCheck speech readiness after setup. Saved narrations remain in Your library; retry the existing item after resolving its error."
        : AttentionJob is { } job ? $"{job.Title}: {job.Error}\n{job.Guidance}" : "";
    private void RaiseAttention() { Raise(nameof(HasAttention)); Raise(nameof(AttentionHeading)); Raise(nameof(AttentionMessage)); }
    private void ReportError(string message) { operationError = message; StatusMessage = message; RaiseAttention(); }
    public string LibrarySummary => !queueLoaded ? "Local records have not loaded. Repair storage or restore a verified backup." : $"{Jobs.Count} narrations · {Jobs.Count(j => j.Job.Stage == JobStage.Queued)} queued · {Jobs.Count(j => j.IsWorking)} working · {Jobs.Count(j => j.NeedsAttention)} need attention · {Jobs.Count(j => j.Job.Stage == JobStage.Exported)} exported locally";
    public string PauseLabel => queue.Paused ? "Resume queue" : "Pause future jobs";
    public JobView? SelectedJob { get => selectedJob; set => Set(ref selectedJob, value); }
    public ICommand NavigateCommand { get; }
    public ICommand ViewLatestCommand { get; }
    public ICommand ViewAttentionCommand { get; }
    public ICommand QueueCommand { get; }
    public ICommand ReviewCommand { get; }
    public ICommand ChooseFolderCommand { get; }
    public ICommand TestFolderCommand { get; }
    public ICommand ReadinessCommand { get; }
    public ICommand SetupCommand { get; }
    public ICommand AuditionCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand ResetPronunciationCommand { get; }
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
        auditions = new(Workspace, provider, queue.InferenceGate, (engine, ct) => provider.ReadyAsync(engine, ct, true));
        queue.Changed += _ => Application.Current.Dispatcher.InvokeAsync(() => RefreshJobs(queue.Snapshot()));
        player.MediaFailed += (_, _) => ReportError("Playback failed. Check that the local audio exists and is decodable.");
        Voices.Add(settings.Voice);
        NavigateCommand = Command(p => { Navigate(p?.ToString() ?? "compose"); return Task.CompletedTask; }, false);
        ViewLatestCommand = Command(_ => { SelectedJob = LatestJob; Navigate("library"); return Task.CompletedTask; }, false);
        ViewAttentionCommand = Command(_ => { SelectedJob = AttentionJob ?? LatestJob; Navigate("library"); return Task.CompletedTask; }, false);
        QueueCommand = Command(_ => SubmitAsync());
        ReviewCommand = Command(async _ => { var text = Source; var omit = ExcludeCode; var dictionary = Pronunciation; var profile = CaptureProfile(); var prepared = await Task.Run(() => TextPreparation.Prepare(text, omit, dictionary, profile, shutdown.Token), shutdown.Token); ShowPreparation(prepared, text); });
        ChooseFolderCommand = Command(async _ => { var path = ChooseFolder(); if (path is not null) { await publisher.TestDestinationAsync(path); settings.Destination = path; Raise(nameof(DestinationDisplay)); await SaveSettingsAsync(); StatusMessage = "Output folder saved. Only completed MP3s will be exported here."; } });
        TestFolderCommand = Command(async _ => { await publisher.TestDestinationAsync(settings.Destination); StatusMessage = "Local write access passed. OneDrive cloud upload is still unknown."; });
        ReadinessCommand = Command(_ => CheckReadinessAsync(true));
        SetupCommand = Command(async _ =>
        {
            SetupDetails = "Checking setup without starting or changing services…";
            var report = await new SetupDiagnostics(new SetupRuntime(Workspace.Root)).CheckAsync(settings, shutdown.Token);
            SetupDetails = report.Display;
            StatusMessage = "Setup inspection finished. Corporate approval and real narration acceptance remain separate.";
        });
        AuditionCommand = Command(p => AuditionAsync(p?.ToString() == "selection"));
        SaveSettingsCommand = Command(async _ => { CaptureProfile(); TextPreparation.ValidateDictionary(Pronunciation); await SaveSettingsAsync(); StatusMessage = "Settings saved for future submissions."; });
        ResetPronunciationCommand = Command(_ => { settings.PronunciationProfile = new(); RaiseProfile(); StatusMessage = "English profile selected for new narrations. Save settings to retain it."; return Task.CompletedTask; });
        CheckEncoderCommand = Command(_ => CheckEncoderAsync());
        ThemeCommand = Command(_ => { App.ToggleTheme(); return Task.CompletedTask; }, false);
        PauseCommand = Command(async _ => { if (queue.Paused && queue.PersistenceError.Length > 0) throw new IOException(queue.PersistenceError); queue.Paused = !queue.Paused; settings.QueuePaused = queue.Paused; RefreshJobs(queue.Snapshot()); await SaveSettingsAsync(); StatusMessage = queue.Paused ? "Future dispatch paused. The current narration can finish." : "Queue resumed."; });
        MoveEarlierCommand = Command(_ => queue.MovePendingAsync(RequireSelected().Id, -1, shutdown.Token));
        MoveLaterCommand = Command(_ => queue.MovePendingAsync(RequireSelected().Id, 1, shutdown.Token));
        PlayCommand = Command(async _ => { var job = RequireSelected(); await StopPlaybackAsync(false); var file = Workspace.FinalPath(job); if (!File.Exists(file) || job.FinalHash.Length == 0 || await Infrastructure.Workspace.HashFileAsync(file) != job.FinalHash) throw new IOException("No validated local audio is available for this narration."); player.Open(new Uri(file)); player.Play(); StatusMessage = "Playing local audio. Use Stop playback to stop."; });
        StopCommand = Command(_ => StopPlaybackAsync(true));
        OpenFolderCommand = Command(_ => { var job = RequireSelected(); if (!Directory.Exists(job.Destination)) throw new IOException("The recorded output folder is missing."); Process.Start(new ProcessStartInfo(job.Destination) { UseShellExecute = true }); return Task.CompletedTask; });
        InspectCommand = Command(_ => { var job = RequireSelected(); ShowPreparation(job.Prepared, job.Source); return Task.CompletedTask; });
        RetryCommand = Command(async _ => { var job = RequireSelected(); await provider.ResetRecoveryBudgetAsync(job.Settings.Engine, shutdown.Token); await queue.RetryAsync(job.Id); });
        ReplaceDestinationCommand = Command(async _ => { var job = RequireSelected(); var path = ChooseFolder(); if (path is not null) { await publisher.TestDestinationAsync(path); await queue.RetryAsync(job.Id, path); StatusMessage = queue.Snapshot().Single(j => j.Id == job.Id).ExportNotice is { Length: > 0 } notice ? notice : "Narration queued in the selected output folder."; } });
        CancelCommand = Command(async _ => { var id = RequireSelected().Id; await queue.CancelAsync(id); StatusMessage = "Cancellation settled. Exported files remain exported; validated chunks are retained for retry."; });
        ReuseCommand = Command(_ => { var job = RequireSelected(); DraftTitle = job.Title; Source = job.Source; Engine = job.Settings.Engine; Voice = job.Settings.Voice; Speed = job.Settings.Speed; ExcludeCode = job.Settings.ExcludeCode; Pronunciation = job.Settings.Pronunciation; settings.PronunciationProfile = job.Settings.Profile ?? new(); RaiseProfile(); Navigate("compose"); return Task.CompletedTask; });
        DeleteCommand = Command(_ => DeleteAsync(false));
        DeleteAllCommand = Command(_ => DeleteAsync(true));
        DiagnosticsCommand = Command(_ => DiagnosticsAsync());
        MeasureStorageCommand = Command(_ => RefreshStorageAsync());
        CleanCacheCommand = Command(async _ => { ValidateRetention(); queue.CacheQuotaMiB = CacheQuotaMiB; queue.ScratchRetentionDays = ScratchRetentionDays; var result = await queue.CleanCacheAsync(shutdown.Token); await RefreshStorageAsync(); StatusMessage = $"Cleanup removed {result.FilesRemoved} files ({result.BytesRemoved / 1048576.0:0.0} MiB). {result.Failures} files could not be removed. Protected data and exports are retained."; });
    }
    private ICommand Command(Func<object?, Task> action, bool clearsError = true) => new AsyncCommand(async p =>
    {
        await operations.RunAsync(() => action(p));
        if (clearsError) { operationError = ""; RaiseAttention(); }
    }, e => ReportError(QueueCoordinator.FriendlyError(e)));
    private void Navigate(string value) { page = value; Raise(nameof(IsCompose)); Raise(nameof(IsLibrary)); Raise(nameof(IsSettings)); Raise(nameof(PageHeading)); }
    private Job RequireSelected() => SelectedJob?.Job ?? throw new ArgumentException("Select a narration first.");
    private void RefreshJobs(IReadOnlyList<Job> snapshots)
    {
        var id = SelectedJob?.Id;
        Jobs.Clear();
        foreach (var job in snapshots.Where(j => j.Stage == JobStage.Queued).Concat(snapshots.Where(j => j.Stage != JobStage.Queued).OrderByDescending(j => j.CreatedUtc))) Jobs.Add(new(job, Workspace, queue.Paused));
        SelectedJob = Jobs.FirstOrDefault(j => j.Id == id) ?? Jobs.FirstOrDefault();
        Raise(nameof(LibrarySummary));
        Raise(nameof(PauseLabel));
        Raise(nameof(LatestJob)); Raise(nameof(HasLatestJob)); RaiseAttention();
        if (queue.PersistenceError.Length > 0) StatusMessage = queue.PersistenceError;
    }
    public Task InitializeAsync() => operations.RunAsync(InitializeCoreAsync);
    private async Task InitializeCoreAsync()
    {
        try { var saved = await drafts.LoadAsync(shutdown.Token); DraftTitle = saved.Title; Source = saved.Source; }
        catch (IOException error) { draftLoadFailed = true; ReportError(error.Message); }
        loading = false;
        string? previewNotice = null;
        try { await Task.Run(() => auditions.RecoverAsync(shutdown.Token)); }
        catch (IOException) { previewNotice = "An interrupted voice preview needs inspection. Its files were preserved in private storage."; }
        await Task.Run(() => queue.InitializeAsync(shutdown.Token)); queueLoaded = true; Raise(nameof(LibrarySummary));
        try { await CheckReadinessAsync(); } catch (Exception error) { StatusMessage = QueueCoordinator.FriendlyError(error); }
        if (previewNotice is not null) ReportError(previewNotice);
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
        catch (IOException) { ReportError("Draft autosave failed. Keep this window open and check disk space."); }
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
        ProviderInfo info;
        try { info = await provider.ReadyAsync(engine, shutdown.Token, explicitRetry); }
        catch (Exception error)
        {
            if (engine == Engine)
            {
                ServiceStatus = "Speech unavailable · needs attention";
                speechError = engine + ": " + QueueCoordinator.FriendlyError(error); RaiseAttention();
            }
            throw;
        }
        settings.Providers[engine] = info;
        await SaveSettingsAsync();
        if (engine == Engine)
        {
            var voice = Voice;
            Voices.Clear(); foreach (var item in info.Voices) Voices.Add(item);
            Voice = info.Voices.Contains(voice) ? voice : info.Voices.First();
            ServiceStatus = engine + " · ready on this laptop";
            speechError = ""; RaiseAttention();
            ProviderDetails = $"Contract v1 · {info.Engine}\nModel: {info.Fingerprint}\nEncoder: {encoderVersion}";
        }
        return info;
    }
    private async Task SubmitAsync()
    {
        if (!queueLoaded) throw new IOException("Local queue records have not loaded. Repair storage or restore a verified compatible backup before submitting. Your draft is retained.");
        var text = Source; var title = DraftTitle.Trim();
        var engine = Engine; var voice = Voice; var speed = Speed; var exclusion = ExcludeCode; var dictionary = Pronunciation; var destination = settings.Destination;
        var profile = CaptureProfile();
        var prepared = await Task.Run(() => TextPreparation.Prepare(text, exclusion, dictionary, profile, shutdown.Token), shutdown.Token);
        ValidateRetention();
        await queue.CleanCacheAsync(shutdown.Token);
        var usage = await queue.MeasureStorageAsync(shutdown.Token);
        var drive = new DriveInfo(Path.GetPathRoot(Workspace.Root)!);
        StorageBudget.EnsureFits(usage, prepared.Script, PrivateStorageLimitMiB, drive.AvailableFreeSpace);
        if (title.Length == 0) title = TextPreparation.SuggestTitle(text);
        await publisher.TestDestinationAsync(destination, shutdown.Token);
        settings.Providers.TryGetValue(engine, out var cached);
        var info = await provider.CaptureForSubmissionAsync(engine, cached, shutdown.Token);
        if (!info.Voices.Contains(voice)) throw new ArgumentException("Select an installed voice after checking readiness.");
        settings.Providers[engine] = info;
        var job = new Job { Title = title, Source = text, Prepared = prepared, Settings = new(engine, voice, speed, exclusion, dictionary, info.Fingerprint, profile, info.ImageId), Destination = destination };
        await queue.AddAsync(job, shutdown.Token);
        RefreshJobs(queue.Snapshot());
        SelectedJob = Jobs.Single(j => j.Id == job.Id);
        Navigate("library");
        if (Source == text) { Source = ""; DraftTitle = ""; }
        StatusMessage = "Narration saved. Its progress is shown here. You do not need to queue it again.";
        DraftQueued?.Invoke();
        await SaveSettingsAsync();
    }
    private async Task AuditionAsync(bool selection)
    {
        var snapshot = new NarrationSettings(Engine, Voice, Speed, ExcludeCode, Pronunciation, "", CaptureProfile());
        var request = selection ? AuditionRequest.Selection(selectionSource, selectionStart, selectionLength, snapshot) : AuditionRequest.Standard(snapshot);
        var generation = ++auditionGeneration;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        activeAudition = cancellation;
        try
        {
            player.Stop(); player.Close();
            await CleanupAuditionAsync();
            StatusMessage = "Preparing a local audition. It waits for current narration before using speech. Use Stop audition to cancel.";
            var audio = await auditions.GenerateAsync(request, cancellation.Token);
            if (generation != auditionGeneration || cancellation.IsCancellationRequested)
            { await auditions.RemoveAsync(audio); return; }
            auditionAudio = audio;
            player.Open(new Uri(OwnedFileRemoval.Resolve(Workspace.Root, audio.RelativePath))); player.Play();
            StatusMessage = selection ? "Playing the selected excerpt with its captured voice and pronunciation settings." : "Playing the standard voice sample with its captured pronunciation settings.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { if (!shutdown.IsCancellationRequested && generation == auditionGeneration) StatusMessage = "Audition stopped."; }
        finally { if (ReferenceEquals(activeAudition, cancellation)) activeAudition = null; }
    }
    private async Task StopPlaybackAsync(bool showStatus)
    {
        ++auditionGeneration;
        var pending = activeAudition is not null;
        activeAudition?.Cancel(); player.Stop(); player.Close();
        await CleanupAuditionAsync();
        if (showStatus) StatusMessage = pending ? "Playback stopped. Audition cancellation requested." : "Playback stopped.";
    }
    private async Task CleanupAuditionAsync()
    {
        await auditionCleanup.WaitAsync();
        try
        {
            if (auditionAudio is not { } audio) return;
            await auditions.RemoveAsync(audio);
            auditionAudio = null;
        }
        finally { auditionCleanup.Release(); }
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
        var review = await Task.Run(() => queue.ReviewDeletionAsync(ids));
        var choice = new DeleteWindow(review.Items, review.RecordedExportRemovals, review.PendingRequests) { Owner = Application.Current.MainWindow };
        if (choice.ShowDialog() != true) return;
        await StopPlaybackAsync(false);
        var deleteExports = choice.DeleteExports;
        var result = await Task.Run(() => queue.DeleteManyAsync(ids, deleteExports));
        Raise(nameof(PauseLabel));
        StatusMessage = result.Failed == 0 ? $"Deleted {result.Removed} managed narrations. Unrelated files were preserved. External retention and phone copies remain outside this app's control." :
            $"Deleted {result.Removed} of {result.Items.Count} narrations. {result.Failed} remain pending removal: {string.Join(" ", result.Items.Where(i => !i.Removed).Select(i => $"{i.Title} ({i.Id[..8]}): {i.Error}"))}";
    }
    private async Task DiagnosticsAsync()
    {
        var dialog = new SaveFileDialog { Title = "Export redacted diagnostics", FileName = "CommuteCast-diagnostics.json", Filter = "JSON file|*.json" };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;
        var package = await Task.Run(() => new DiagnosticExporter(Workspace, store).BuildAsync(queue.Snapshot(), encoderVersion, shutdown.Token));
        await File.WriteAllTextAsync(dialog.FileName, package, shutdown.Token);
        StatusMessage = "Redacted diagnostics exported. No source, script, title, audio, or corporate path is included.";
    }
    public void ValidateForMaintenance() => ValidateRetention();
    public ValueTask DisposeAsync() => new(disposal ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        var settled = operations.StopAsync();
        draftSave?.Cancel(); shutdown.Cancel(); player.Close();
        await settled.WaitAsync(TimeSpan.FromSeconds(20));
        player.Close(); // An already admitted playback action may have finished after the first close.
        await queue.DisposeAsync();
        await draftGate.WaitAsync();
        try { if (!draftLoadFailed || draftDirty) await drafts.SaveAsync(new(DraftTitle, Source)); }
        finally { draftGate.Release(); }
        await SaveSettingsAsync();
        try { await CleanupAuditionAsync(); }
        finally { provider.Dispose(); shutdown.Dispose(); auditionCleanup.Dispose(); }
    }
}
