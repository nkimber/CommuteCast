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
using System.Windows.Threading;

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
        catch (Exception error) { AppLogging.Failure("DesktopCommand", error); failure(error); }
        finally { running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
}
public sealed class JobView(Job job, Workspace workspace, bool paused = false) : Observable
{
    public Job Job { get; } = job;
    public string Id => Job.Id;
    public string Title => Job.Title;
    public string Submitted => Job.CreatedUtc.ToLocalTime().ToString("MMM d, yyyy · h:mm tt zzz");
    private JobProgress Presentation => JobProgress.Describe(Job, paused);
    public string Status => IsWorking && Job.Activity.Read().Waiting && !Job.CancellationRequested ? "Waiting · processing will continue automatically" : Presentation.Status;
    public string Guidance => Presentation.Guidance;
    public bool IsWorking => Presentation.IsWorking;
    public bool NeedsAttention => Presentation.NeedsAttention;
    public bool CanResume => (Job.Stage is JobStage.Failed or JobStage.Cancelled) && !Job.ExportCommitted && !Job.DeletionRequested;
    public bool CanOpenFolder => !Job.DeletionRequested && (Job.ExportCommitted || Job.FinalHash.Length > 0);
    public string AudioSummary { get; } = DescribeAudio(job, workspace);
    private static string DescribeAudio(Job job, Workspace workspace)
    {
        if (!job.ExportCommitted && job.FinalHash.Length == 0) return "";
        var duration = double.IsFinite(job.DurationSeconds) && job.DurationSeconds > 0
            ? "Audio length " + Clock((long)(job.DurationSeconds * 1000)) : "Audio length unavailable";
        var size = "File size unavailable";
        try
        {
            var file = job.ExportCommitted
                ? job.ExportName.Length > 0 && Path.GetFileName(job.ExportName) == job.ExportName ? Path.Combine(job.Destination, job.ExportName) : null
                : workspace.FinalPath(job);
            if (file is not null && new FileInfo(file) is { Exists: true } info)
            {
                var bytes = info.Length;
                size = bytes >= 1_000_000_000 ? $"{bytes / 1_000_000_000.0:0.##} GB"
                    : bytes >= 1_000_000 ? $"{bytes / 1_000_000.0:0.##} MB"
                    : bytes >= 1_000 ? $"{bytes / 1_000.0:0.##} KB" : $"{bytes:N0} bytes";
                size = "MP3 size " + size;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { /* A missing or inaccessible MP3 must not prevent the library from displaying. */ }
        return duration + " · " + size;
    }
    public string RecoveryInstructions => JobRecovery.Instructions(Job);
    public bool HasRecoveryInstructions => RecoveryInstructions.Length > 0;
    public bool NeedsSpeechCheck => Job.Stage == JobStage.Failed && (Job.FailedStage is JobStage.WaitingForService or JobStage.Synthesizing || Job.FailureCategory is FailureCategory.ServiceConnection or FailureCategory.ServiceContract or FailureCategory.Prerequisite);
    public string CopyableDetails => string.Join("\n\n", new[] { Title, $"Job ID: {Id}\nSubmitted: {Submitted}", Status, AudioSummary, ActivityNotice, TimingSummary, Guidance, Details, Delivery,
        Error.Length > 0 ? "Error\n" + Error : "", HasRecoveryInstructions ? "How to fix\n" + RecoveryInstructions : "" }.Where(s => s.Length > 0));
    public double Progress => Job.CompletedChunks;
    public double ChunkTotal => Math.Max(1, Job.Chunks.Count);
    public string ProgressSummary => $"{Job.CompletedChunks} of {Job.Chunks.Count} segments completed and validated";
    public string ActivityNotice => IsWorking && Job.Activity.Read().Notice is { Length: > 0 } notice ? notice : Job.Stage switch
    {
        JobStage.Failed => "Stopped. Completed segments are retained for retry.",
        JobStage.Cancelled => "Cancelled. Saved segments remain available.",
        JobStage.Queued => paused ? "Queue paused · choose Resume queue" : "Waiting for its turn in the queue",
        _ => Guidance
    };
    public string FailureSummary => NeedsAttention ? "Processing stopped. Open details for the cause and recovery steps." : "";
    public string TimingSummary
    {
        get
        {
            var live = Job.Activity.Read();
            var timing = live.Running || live.Timing.ProcessingMilliseconds > 0 ? live.Timing : Job.RunTiming;
            if (timing is null) return "Processing time starts when this narration begins.";
            return $"Processing time {Clock(timing.ProcessingMilliseconds)}" + (timing.SegmentNumber is { } segment ? $" · Segment {segment}: {Clock(timing.SegmentMilliseconds)}" : "");
        }
    }
    private static string Clock(long milliseconds)
    {
        var elapsed = TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, 0, TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond / 2));
        return $"{(long)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }
    public void RefreshActivity()
    {
        Raise(nameof(Status)); Raise(nameof(ActivityNotice)); Raise(nameof(TimingSummary));
        Raise(nameof(IsWorking)); Raise(nameof(NeedsAttention)); Raise(nameof(FailureSummary));
        Raise(nameof(CanResume));
        Raise(nameof(CanOpenFolder));
    }
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
    private readonly ISetupRuntime setupRuntime;
    private readonly Action<ProcessStartInfo> openFolder;
    private readonly DispatcherTimer progressTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
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
    private string operationError = "", speechError = "", speechErrorEngine = "";
    private string checkedJobId = "", selectedSpeechResult = "";
    private string setupDetails = "Setup has not been checked. This inspection does not start or change speech services.";
    private string speechRepairDetails = "Start / repair checks both installed engines, starts verified stopped CommuteCast containers and refreshes setup. It keeps saved narrations for explicit resume.";
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
    public string SpeechRepairDetails { get => speechRepairDetails; private set => Set(ref speechRepairDetails, value); }
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
    public bool HasSelectedJob => SelectedJob is not null;
    public string SelectedSpeechResult => SelectedJob?.Id == checkedJobId ? selectedSpeechResult : "";
    public string SelectedDetailsForCopy => SelectedJob is { } view ? view.CopyableDetails + (SelectedSpeechResult.Length > 0 ? "\n\nLast speech check\n" + SelectedSpeechResult : "") : "";
    public JobView? SelectedJob { get => selectedJob; set { if (Set(ref selectedJob, value)) { Raise(nameof(HasSelectedJob)); Raise(nameof(SelectedSpeechResult)); Raise(nameof(SelectedDetailsForCopy)); } } }
    public ICommand NavigateCommand { get; }
    public ICommand ViewLatestCommand { get; }
    public ICommand ViewAttentionCommand { get; }
    public ICommand CopyDetailsCommand { get; }
    public ICommand SelectedReadinessCommand { get; }
    public ICommand RepairSelectedCommand { get; }
    public ICommand RepairSpeechCommand { get; }
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
    public ICommand ResumeJobCommand { get; }
    public ICommand ReplaceDestinationCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ReuseCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand DeleteAllCommand { get; }
    public ICommand DiagnosticsCommand { get; }
    public ICommand MeasureStorageCommand { get; }
    public ICommand CleanCacheCommand { get; }

    public MainViewModel(AppSettings saved, Workspace? workspace = null, LocalSpeechProvider? speechProvider = null, ISetupRuntime? setupInspection = null, Action<ProcessStartInfo>? openFolder = null)
    {
        settings = saved; Workspace = workspace ?? new();
        drafts = new(Workspace);
        store = new SqliteJobStore(Workspace);
        provider = speechProvider ?? new(Workspace); publisher = new(Workspace, store);
        setupRuntime = setupInspection ?? new SetupRuntime(Workspace.Root);
        this.openFolder = openFolder ?? (info => { Process.Start(info); });
        queue = new(Workspace, store, provider, new(settings), publisher) { Paused = settings.QueuePaused, CacheQuotaMiB = settings.CacheQuotaMiB, ScratchRetentionDays = settings.ScratchRetentionDays, PrivateStorageLimitMiB = settings.PrivateStorageLimitMiB };
        auditions = new(Workspace, provider, queue.InferenceGate, (engine, ct) => provider.ReadyAsync(engine, ct, true));
        queue.Changed += _ => Application.Current.Dispatcher.InvokeAsync(() => RefreshJobs(queue.Snapshot()));
        progressTimer.Tick += (_, _) => { foreach (var job in Jobs) job.RefreshActivity(); };
        progressTimer.Start();
        player.MediaFailed += (_, _) => ReportError("Playback failed. Check that the local audio exists and is decodable.");
        Voices.Add(settings.Voice);
        NavigateCommand = Command(p => { Navigate(p?.ToString() ?? "compose"); return Task.CompletedTask; }, false);
        ViewLatestCommand = Command(_ => { SelectedJob = LatestJob; Navigate("library"); return Task.CompletedTask; }, false);
        ViewAttentionCommand = Command(_ => { SelectedJob = AttentionJob ?? LatestJob; Navigate("library"); return Task.CompletedTask; }, false);
        CopyDetailsCommand = Command(_ => { RequireSelected(); Clipboard.SetText(SelectedDetailsForCopy); StatusMessage = "Narration details copied, including the error and repair steps. Source text and spoken script are not included."; return Task.CompletedTask; }, false);
        SelectedReadinessCommand = Command(_ => CheckSavedSpeechAsync(RequireSelected()));
        RepairSelectedCommand = Command(async _ =>
        {
            var job = RequireSelected();
            await CheckSavedSpeechAsync(job);
            try { await queue.RetryAsync(job.Id); }
            catch (Exception error) { SetSelectedSpeechResult(job.Id, "Speech is ready, but resume failed: " + QueueCoordinator.FriendlyError(error)); throw; }
            RefreshJobs(queue.Snapshot()); Navigate("library");
            StatusMessage = queue.Paused ? "Speech is ready and this saved narration is queued. Choose Resume queue to continue dispatch." : "Speech is ready. This saved narration is queued to resume; no duplicate was created.";
            SetSelectedSpeechResult(job.Id, StatusMessage);
        });
        RepairSpeechCommand = Command(_ => RepairSpeechAsync());
        QueueCommand = Command(_ => SubmitAsync());
        ReviewCommand = Command(async _ => { var text = Source; var omit = ExcludeCode; var dictionary = Pronunciation; var profile = CaptureProfile(); var prepared = await Task.Run(() => TextPreparation.Prepare(text, omit, dictionary, profile, shutdown.Token), shutdown.Token); ShowPreparation(prepared, text); });
        ChooseFolderCommand = Command(async _ => { var path = ChooseFolder(); if (path is not null) { await publisher.TestDestinationAsync(path); settings.Destination = path; Raise(nameof(DestinationDisplay)); await SaveSettingsAsync(); StatusMessage = "Output folder saved. Only completed MP3s will be exported here."; } });
        TestFolderCommand = Command(async _ => { await publisher.TestDestinationAsync(settings.Destination); StatusMessage = "Local write access passed. OneDrive cloud upload is still unknown."; });
        ReadinessCommand = Command(_ => CheckReadinessAsync(true));
        SetupCommand = Command(async _ => { await RefreshSetupAsync(); StatusMessage = "Setup inspection finished. Corporate approval and real narration acceptance remain separate."; });
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
        OpenFolderCommand = Command(parameter =>
        {
            var job = parameter is JobView row
                ? queue.Snapshot().SingleOrDefault(j => j.Id == row.Id) ?? throw new ArgumentException("This narration no longer exists. Refresh the library.")
                : RequireSelected();
            if (parameter is JobView)
            {
                if (!new JobView(job, Workspace).CanOpenFolder) throw new ArgumentException("This narration has no completed MP3 to locate.");
                SelectedJob = Jobs.FirstOrDefault(j => j.Id == job.Id);
            }
            var location = AudioFolderNavigation.ForJob(job, Workspace); this.openFolder(location);
            StatusMessage = !location.UseShellExecute ? "The MP3 is selected in its folder."
                : job.ExportCommitted || job.FinalHash.Length > 0 ? "Folder opened. The recorded MP3 is missing or moved." : "Output folder opened.";
            return Task.CompletedTask;
        });
        InspectCommand = Command(_ => { var job = RequireSelected(); ShowPreparation(job.Prepared, job.Source); return Task.CompletedTask; });
        RetryCommand = Command(_ => ResumeAsync(RequireSelected()));
        ResumeJobCommand = Command(async parameter =>
        {
            var row = parameter as JobView ?? throw new ArgumentException("Choose a saved narration to resume.");
            var job = queue.Snapshot().SingleOrDefault(j => j.Id == row.Id) ?? throw new ArgumentException("This narration no longer exists. Refresh the library.");
            if (!new JobView(job, Workspace).CanResume) throw new ArgumentException("This narration is already queued, running, exported or pending removal.");
            SelectedJob = Jobs.FirstOrDefault(j => j.Id == job.Id);
            await ResumeAsync(job);
        });
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
    private void SetSelectedSpeechResult(string id, string result) { checkedJobId = id; selectedSpeechResult = result; Raise(nameof(SelectedSpeechResult)); Raise(nameof(SelectedDetailsForCopy)); }
    private Job RequireSelected() => SelectedJob?.Job ?? throw new ArgumentException("Select a narration first.");
    private async Task ResumeAsync(Job job)
    {
        await provider.ResetRecoveryBudgetAsync(job.Settings.Engine, shutdown.Token);
        await queue.RetryAsync(job.Id);
        RefreshJobs(queue.Snapshot());
        StatusMessage = queue.Paused
            ? "This narration is queued to resume. Choose Resume queue to continue. Completed segments are retained."
            : "This narration is queued to resume from its saved progress. Completed segments are retained.";
    }
    private void RefreshJobs(IReadOnlyList<Job> snapshots)
    {
        // A running synthesis stage has passed this job's captured readiness
        // check. Retire only that engine's older readiness warning.
        if (snapshots.Any(j => j.Stage == JobStage.Synthesizing && j.Activity.Read().Running && j.Settings.Engine == speechErrorEngine))
        { speechError = ""; speechErrorEngine = ""; }
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
        try { using var trace = new StartupStepTrace(StartupPhase.DraftLoad); var saved = await drafts.LoadAsync(shutdown.Token); DraftTitle = saved.Title; Source = saved.Source; trace.Complete(); }
        catch (IOException error) { AppLogging.Failure("DraftLoad", error); draftLoadFailed = true; ReportError(error.Message); }
        loading = false;
        string? previewNotice = null;
        try { using var trace = new StartupStepTrace(StartupPhase.AuditionRecovery); await Task.Run(() => auditions.RecoverAsync(shutdown.Token)); trace.Complete(); }
        catch (IOException error) { AppLogging.Failure("AuditionRecovery", error); previewNotice = "An interrupted voice preview needs inspection. Its files were preserved in private storage."; }
        using (var trace = new StartupStepTrace(StartupPhase.QueueRecovery)) { await Task.Run(() => queue.InitializeAsync(shutdown.Token)); queueLoaded = true; Raise(nameof(LibrarySummary)); trace.Complete(); }
        try { using var trace = new StartupStepTrace(StartupPhase.SpeechReadiness); await CheckReadinessAsync(); trace.Complete(); } catch (Exception error) { AppLogging.Failure("InitialSpeechReadiness", error); StatusMessage = QueueCoordinator.FriendlyError(error); }
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
        catch (IOException error) { AppLogging.Failure("DraftAutosave", error); ReportError("Draft autosave failed. Keep this window open and check disk space."); }
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
    private async Task<ProviderInfo> CheckReadinessAsync(bool explicitRetry = false, string? savedEngine = null)
    {
        var engine = savedEngine ?? Engine;
        ServiceStatus = "Checking " + engine + " locally…";
        ProviderInfo info;
        try { info = await provider.ReadyAsync(engine, shutdown.Token, explicitRetry); }
        catch (Exception error)
        {
            if (engine == Engine || savedEngine is not null)
            {
                ServiceStatus = "Speech unavailable · needs attention";
                speechErrorEngine = engine;
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
            ProviderDetails = $"Contract v1 · {info.Engine}\nModel: {info.Fingerprint}\nEncoder: {encoderVersion}";
        }
        else if (savedEngine is not null) ServiceStatus = engine + " · ready on this laptop";
        if (speechErrorEngine == engine) { speechError = ""; speechErrorEngine = ""; RaiseAttention(); }
        return info;
    }
    private async Task RefreshSetupAsync()
    {
        SetupDetails = "Checking setup without starting or changing services…";
        var report = await new SetupDiagnostics(setupRuntime).CheckAsync(settings, shutdown.Token);
        SetupDetails = report.Display;
    }
    private async Task CheckSavedSpeechAsync(Job job)
    {
        SetSelectedSpeechResult(job.Id, $"Checking / starting {job.Settings.Engine} for this saved narration…");
        try
        {
            var info = await CheckReadinessAsync(true, job.Settings.Engine);
            if (info.Fingerprint != job.Settings.ProviderFingerprint || (job.Settings.ProviderImageId is not null && info.ImageId != job.Settings.ProviderImageId))
                throw new IOException("The speech model or image changed since submission. Restore the original compatible service for this saved narration, or use Use as a new draft to narrate with the new model. Existing chunks cannot be mixed with another model.");
            StatusMessage = $"{job.Settings.Engine} is ready for this saved narration. Choose Retry / resume to continue it; choose Resume queue if paused.";
            SetSelectedSpeechResult(job.Id, StatusMessage);
        }
        catch (Exception error) { SetSelectedSpeechResult(job.Id, $"{job.Settings.Engine}: {QueueCoordinator.FriendlyError(error)}"); throw; }
    }
    private async Task RepairSpeechAsync()
    {
        var results = new List<string>(); var failures = new List<string>();
        foreach (var engine in Engines)
        {
            SpeechRepairDetails = string.Join("\n\n", results.Append($"Checking / starting {engine}… Readiness allows up to two minutes per engine."));
            try
            {
                await CheckReadinessAsync(true, engine);
                results.Add($"{engine}: ready. Installed service identity and health verified.");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                var failure = $"{engine}: {QueueCoordinator.FriendlyError(error)}";
                results.Add(failure); failures.Add(failure);
            }
        }
        SpeechRepairDetails = string.Join("\n\n", results) + "\n\nSaved failed narrations remain in Your library. Use Repair speech & resume on the existing item to continue it.";
        await RefreshSetupAsync();
        if (failures.Count > 0) throw new IOException("Some speech services still need attention. " + string.Join("\n", failures));
        StatusMessage = "Both speech services are ready. Setup has been refreshed. Resume the existing saved narration in Your library.";
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
        progressTimer.Stop();
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
