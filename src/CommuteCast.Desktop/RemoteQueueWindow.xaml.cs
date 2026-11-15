using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CommuteCast.Core;
using CommuteCast.Infrastructure;
using Microsoft.Win32;

namespace CommuteCast.Desktop;

public partial class RemoteQueueWindow : Window
{
    private readonly MainViewModel model;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Dictionary<string, RemoteRequest> requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RemoteJobStatus> lastStatuses = new(StringComparer.Ordinal);
    private string? loadedRoot, selectedJob;
    private bool refreshing, writing, closed;

    public RemoteQueueWindow(MainViewModel model)
    {
        this.model = model;
        InitializeComponent();
        var config = model.RemoteSettings;
        FolderInput.Text = config?.Folder ?? "";
        WorkerName.Text = config?.WorkerName ?? Environment.MachineName;
        EnableWorker.IsChecked = config?.EnableWorker == true;
        AllowHosted.IsChecked = config?.AllowHosted == true;
        FallbackInput.Text = string.Join(Environment.NewLine, config?.FallbackPresets ?? []);
        PresetHint.Text = "Saved presets: " + string.Join(", ", model.RemotePresets.Select(p => p.Name));
        SettingsPanel.IsExpanded = config is null;
        refreshTimer.Tick += async (_, _) => await RefreshAsync();
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        refreshTimer.Start();
        await RefreshAsync();
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        closed = true;
        refreshTimer.Stop();
        lifetime.Cancel();
        lifetime.Dispose();
    }

    private void Browse(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose the existing shared OneDrive folder" };
        if (picker.ShowDialog(this) == true) FolderInput.Text = picker.FolderName;
    }

    private async void SaveRemoteSettings(object sender, RoutedEventArgs e)
    {
        var fallbacks = FallbackInput.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        await WriteAsync(async () =>
        {
            await model.SaveRemoteSettingsAsync(FolderInput.Text, EnableWorker.IsChecked == true, WorkerName.Text, fallbacks, AllowHosted.IsChecked == true);
            SettingsPanel.IsExpanded = false;
            Feedback.Text = "Remote settings saved.";
        });
    }

    private async void SendDraft(object sender, RoutedEventArgs e)
    {
        if (Workers.SelectedItem is not WorkerChoice worker) return;
        await WriteAsync(async () =>
        {
            selectedJob = await model.SubmitRemoteDraftAsync(worker.Id, lifetime.Token);
            Feedback.Text = $"Request saved for {worker.Name}. The worker will acknowledge it after sync.";
        });
    }

    private async Task WriteAsync(Func<Task> action)
    {
        if (writing) return;
        writing = true;
        UpdateSend();
        UpdateRetry();
        try { await action(); }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { if (!closed) Feedback.Text = QueueCoordinator.FriendlyError(error); }
        finally { writing = false; if (!closed) { UpdateSend(); UpdateRetry(); } }
        if (!closed) await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (closed || refreshing || writing) return;
        refreshing = true;
        try
        {
            var folder = model.OpenRemoteFolder();
            if (folder is null) { RosterSummary.Text = "Choose the shared OneDrive folder to see workers and remote jobs."; return; }
            var ct = lifetime.Token;
            var snapshot = await Task.Run(() => ReadFolderAsync(folder, ct), ct);
            if (closed) return;
            var now = DateTimeOffset.UtcNow;
            var workers = snapshot.Workers.Select(w => new WorkerChoice(w, now)).OrderBy(w => w.Preference).ThenBy(w => w.Name).ToArray();
            var previousWorker = Workers.SelectedValue;
            Workers.ItemsSource = workers;
            Workers.SelectedValue = previousWorker;
            if (Workers.SelectedIndex < 0) Workers.SelectedItem = workers.FirstOrDefault(w => w.State != RemoteWorkerState.Disabled);
            RosterSummary.Text = workers.Any(w => w.State is RemoteWorkerState.Ready or RemoteWorkerState.Busy)
                ? "Each worker generates one narration at a time."
                : "No workers ready right now. You can queue a narration for a registered worker to pick up later.";
            var previousJob = selectedJob ?? JobList.SelectedValue as string;
            JobList.ItemsSource = snapshot.Jobs;
            JobList.SelectedValue = previousJob;
            if (JobList.SelectedIndex < 0 && JobList.Items.Count > 0) JobList.SelectedIndex = 0;
            selectedJob = null;
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { if (!closed) RosterSummary.Text = "The shared queue is temporarily unavailable. Check the folder and OneDrive, then wait for refresh."; }
        finally { refreshing = false; if (!closed) UpdateSend(); }
    }

    private async Task<(IReadOnlyList<RemoteWorkerStatus> Workers, JobRow[] Jobs)> ReadFolderAsync(RemoteQueueFolder folder, CancellationToken ct)
    {
        if (!string.Equals(loadedRoot, folder.Root, StringComparison.OrdinalIgnoreCase))
        { requests.Clear(); lastStatuses.Clear(); loadedRoot = folder.Root; }
        var workers = await folder.ReadWorkersAsync(ct);
        var rows = new List<JobRow>();
        foreach (var id in folder.RequestIds())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!requests.TryGetValue(id, out var request))
                {
                    request = await folder.ReadRequestAsync(id, ct);
                    if (request is null) continue;
                    requests[id] = request;
                }
                var status = await folder.ReadStatusAsync(id, ct);
                if (status is not null && status.WorkerId == request.TargetWorkerId &&
                    (!lastStatuses.TryGetValue(id, out var last) || status.UpdatedUtc >= last.UpdatedUtc))
                    lastStatuses[id] = status;
                rows.Add(new(request, lastStatuses.GetValueOrDefault(id), workers.FirstOrDefault(w => w.WorkerId == request.TargetWorkerId)));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
            {
                if (requests.TryGetValue(id, out var cached))
                    rows.Add(new(cached, lastStatuses.GetValueOrDefault(id), workers.FirstOrDefault(w => w.WorkerId == cached.TargetWorkerId)));
            }
        }
        return (workers, rows.OrderByDescending(j => j.Request.CreatedUtc).ToArray());
    }

    private void WorkerSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSend();

    private void UpdateSend()
    {
        if (SendButton is not null) SendButton.IsEnabled = !writing && model.CanSubmitRemote && Workers.SelectedItem is WorkerChoice { State: not RemoteWorkerState.Disabled };
    }

    private void JobSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = JobList.SelectedItem as JobRow;
        JobDetails.Text = row?.Details ?? "Select a narration to see its progress and voice settings.";
        PlayButton.IsEnabled = row?.Status is { State: RemoteJobState.Ready, OutputFile: not null };
        DiagnosticsButton.IsEnabled = row?.Status is { FallbackReason: not null } or { Attention: not null };
        UpdateRetry();
    }

    private void UpdateRetry()
    {
        if (RetryButton is not null) RetryButton.IsEnabled = !writing && JobList.SelectedItem is JobRow
            { Status: { State: RemoteJobState.NeedsAttention } status } && model.CanRetryRemote(status.WorkerId);
    }

    private async void RetryOnWorker(object sender, RoutedEventArgs e)
    {
        if (JobList.SelectedItem is not JobRow { Status: { State: RemoteJobState.NeedsAttention } status }) return;
        await WriteAsync(async () =>
        {
            Feedback.Text = "Checking whether the saved narration can resume…";
            Feedback.Text = await model.RetryRemoteAsync(status.WorkerId, status.JobId, lifetime.Token)
                ? "The saved narration can continue. Watch its status here."
                : "The saved narration still needs attention. See its details for the latest reason.";
        });
    }

    private void OpenMp3(object sender, RoutedEventArgs e)
    {
        if (JobList.SelectedItem is JobRow { Status: { State: RemoteJobState.Ready, OutputFile: { } file } })
            OpenSharedPath(folder => folder.OutputPath(file));
    }

    private void OpenListeningFolder(object sender, RoutedEventArgs e) => OpenSharedPath(folder => folder.OutputFolder);

    private void OpenSharedPath(Func<RemoteQueueFolder, string> pickPath)
    {
        try
        {
            var folder = model.OpenRemoteFolder() ?? throw new IOException("Choose the shared folder first.");
            var path = pickPath(folder);
            if (!File.Exists(path) && !Directory.Exists(path)) throw new IOException("Wait for OneDrive to sync this item to the PC.");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { Feedback.Text = QueueCoordinator.FriendlyError(error); }
    }

    private void CopyDiagnostics(object sender, RoutedEventArgs e)
    {
        if (JobList.SelectedItem is not JobRow row) return;
        Clipboard.SetText($"CommuteCast remote speech check\nJob ID: {row.Id}\n{row.Details}\n" +
            "Inspect the worker's selected speech engine, installed model and voice catalog. Verify the requested profile before retrying.");
        Feedback.Text = "Speech diagnostics copied.";
    }

    private sealed class WorkerChoice(RemoteWorkerStatus worker, DateTimeOffset now)
    {
        public string Id => worker.WorkerId;
        public string Name => worker.Name;
        public RemoteWorkerState State => worker.StateAt(now);
        public string Label => $"{Name} · {StateName(State)} · checked in {worker.UpdatedUtc.ToLocalTime():g}";
        public int Preference => State switch { RemoteWorkerState.Ready => 0, RemoteWorkerState.Busy => 1, RemoteWorkerState.Starting => 2, RemoteWorkerState.Disabled => 4, _ => 3 };
        private static string StateName(RemoteWorkerState state) => state == RemoteWorkerState.NeedsAttention ? "Needs attention" : state.ToString();
    }

    private sealed class JobRow(RemoteRequest request, RemoteJobStatus? status, RemoteWorkerStatus? worker)
    {
        public RemoteRequest Request => request;
        public RemoteJobStatus? Status => status;
        public string Id => request.JobId;
        public string Title => request.Title;
        public string StateLabel => status?.State switch
        {
            RemoteJobState.Ready when status.FallbackReason is not null => "Ready · fallback used",
            RemoteJobState.NeedsAttention => "Needs attention",
            { } state => state.ToString(),
            _ => "Waiting for pickup"
        };
        public string Progress => status switch
        {
            { State: RemoteJobState.Queued, QueuePosition: { } position } => $"Queue position {position}",
            { State: RemoteJobState.Generating, ChunksTotal: > 0 } => $"{status.ChunksDone} of {status.ChunksTotal} chunks" +
                (status.EstimatedSecondsRemaining is > 0 and < 86_400 ? $" · ~{Math.Ceiling(status.EstimatedSecondsRemaining.Value / 60d)} min" : ""),
            { State: RemoteJobState.Ready, DurationSeconds: > 0 and < 86_400 } => TimeSpan.FromSeconds(status.DurationSeconds.Value).ToString(@"hh\:mm\:ss"),
            _ => ""
        };

        public string Details => string.Join(Environment.NewLine, new[]
        {
            "Worker: " + (worker?.Name ?? request.TargetWorkerId),
            "Requested: " + Describe(status?.RequestedVoice ?? RemoteVoice.From(request.Options, request.Preset)),
            status?.ActualVoice is { } actual ? "Used: " + Describe(actual) : null,
            status?.FallbackReason, status?.Attention,
            worker?.Presets is { Length: > 0 } presets ? "Verified presets: " + string.Join(", ", presets) : null,
            status is null ? null : $"Updated {status.UpdatedUtc.ToLocalTime():g}"
        }.OfType<string>());

        private static string Describe(RemoteVoice voice) => string.Join(" · ", new[]
        {
            voice.Engine, voice.Model, voice.Voice, voice.Preset, $"{voice.Speed:0.00}× pace", voice.Emotion,
            voice.LocalDelivery is { BlendVoice.Length: > 0 } delivery ? "Blend " + delivery.BlendVoice : null,
            voice.HasDeliveryInstructions ? "Custom delivery instructions" : null
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }
}
