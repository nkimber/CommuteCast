using System.IO;
using System.Windows;
using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Desktop;

public sealed partial class MainViewModel
{
    private RemoteQueueHost? remoteHost;
    private RemoteRequest? pendingRemoteSubmission;
    private readonly Dictionary<string, string> remoteNotices = new(StringComparer.Ordinal);
    private bool changingRemoteSettings;

    internal RemoteQueueSettings? RemoteSettings => settings.RemoteQueue;
    internal bool RemoteFolderNeedsConfirmation => OneDriveFolders.NeedsConfirmation(RemoteSettings?.Folder,
        settings.ConfirmedRemoteQueueFolder, previousPersonalFolders?.Root);
    internal IReadOnlyList<NarrationPreset> RemotePresets => presetLibrary.Items;
    internal bool CanSubmitRemote => !PodcastMode && !string.IsNullOrWhiteSpace(Source);
    public string RemoteQueueLabel => remoteNotices.Count > 0 ? "Remote queue · setup needs attention" : "Remote queue";

    internal async Task SaveRemoteSettingsAsync(string path, bool enabled, string name, string[] fallbackNames, bool allowHosted)
    {
        if (!queueLoaded) throw new IOException("Wait for CommuteCast to finish loading before changing remote settings.");
        path = path.Trim(); name = name.Trim();
        if (name.Length is 0 or > 120 || name.Any(char.IsControl)) throw new ArgumentException("Enter a worker name of 1–120 characters.");
        var previous = settings.RemoteQueue;
        var previousConfirmation = settings.ConfirmedRemoteQueueFolder;
        var folder = new RemoteQueueFolder(path, Workspace);
        foreach (var fallback in fallbackNames)
        {
            var preset = RemotePresets.FirstOrDefault(p => p.Name.Equals(fallback, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Save a narration preset named '{fallback}' before using it as a fallback.");
            if (SpeechProviders.IsHosted(preset.Options.Engine) && !allowHosted)
                throw new ArgumentException("Remove hosted fallback presets or enable hosted speech for this worker.");
        }
        var registration = previous?.RegistrationId ?? "";
        if (enabled && !RemoteIdentity.IsValid(registration)) registration = Guid.NewGuid().ToString("N");
        changingRemoteSettings = true;
        try
        {
            settings.RemoteQueue = new(folder.Root, enabled, name, registration, fallbackNames, allowHosted);
            settings.ConfirmedRemoteQueueFolder = folder.Root;
            try { await SaveSettingsAsync(); }
            catch { settings.RemoteQueue = previous; settings.ConfirmedRemoteQueueFolder = previousConfirmation; throw; }
            Raise(nameof(AdminFolders));
            await StopRemoteWorkerAsync(disable: previous?.EnableWorker == true && !enabled);
        }
        finally { changingRemoteSettings = false; OpenRemoteWorker(); }
    }

    internal RemoteQueueFolder? OpenRemoteFolder() => RemoteSettings is { } remote ? new(remote.Folder, Workspace) : null;

    private void OpenRemoteWorker()
    {
        if (!queueLoaded || changingRemoteSettings || shutdown.IsCancellationRequested || remoteHost is not null ||
            RemoteSettings is not { } config || !RemoteIdentity.IsValid(config.RegistrationId)) return;
        try
        {
            var folder = new RemoteQueueFolder(config.Folder, Workspace);
            var fallbacks = config.FallbackPresets.Select(name => RemotePresets.FirstOrDefault(p =>
                p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))).OfType<NarrationPreset>().ToArray();
            var planner = new RemoteRenderPlanner((snapshot, ct) => SpeechProviders.IsHosted(snapshot.Engine)
                ? provider.ReadyForSettingsAsync(snapshot, ct)
                : provider.CaptureForSubmissionAsync(snapshot.Engine, cached: null, ct), fallbacks, config.AllowHosted);
            // A settings restore on another machine creates a separate roster entry.
            var id = Job.Hash(config.RegistrationId + "\n" + Environment.MachineName)[..32].ToLowerInvariant();
            var worker = new RemoteQueueWorker(id, config.WorkerName, folder, Workspace, queue, planner);
            worker.StatusChanged += status =>
            {
                _ = Application.Current.Dispatcher.InvokeAsync(() => RecordRemoteNotice(status));
            };
            var defaults = SpeechProviders.All.Where(p => !p.Capabilities.Hosted)
                .Select(p => new NarrationPreset(p.DefaultVoice, new(p.Id, p.DefaultVoice, 1, false, "", new())));
            remoteHost = new(worker, config, queue, planner, fallbacks.Concat(defaults).ToArray());
            if (remoteNotices.Remove("worker")) Raise(nameof(RemoteQueueLabel));
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            if (remoteNotices.TryAdd("worker", "Check the shared remote queue folder in CommuteCast."))
            {
                AppLogging.Failure("RemoteWorkerStart", error);
                Raise(nameof(RemoteQueueLabel));
            }
        }
    }

    private void RecordRemoteNotice(RemoteJobStatus status)
    {
        var notice = status.FallbackReason ?? status.Attention;
        var changed = notice is null ? remoteNotices.Remove(status.JobId) : remoteNotices.GetValueOrDefault(status.JobId) != notice;
        if (notice is not null) remoteNotices[status.JobId] = notice;
        if (changed) Raise(nameof(RemoteQueueLabel));
    }

    private async Task StopRemoteWorkerAsync(bool disable = false)
    {
        var host = remoteHost;
        remoteHost = null;
        if (host is not null) await host.StopAsync(disable);
    }

    internal bool CanRetryRemote(string id) => RemoteSettings?.EnableWorker == true && remoteHost?.WorkerId == id;

    internal Task<bool> RetryRemoteAsync(string workerId, string id, CancellationToken ct) => CanRetryRemote(workerId)
        ? remoteHost!.RetryAsync(id, ct) : throw new ArgumentException("Retry this narration on its assigned worker.");

    internal async Task<string> SubmitRemoteDraftAsync(string workerId, CancellationToken ct)
    {
        if (!CanSubmitRemote) throw new ArgumentException("Paste a narration in New narration before sending it to a worker. Podcast dialogue uses local creation.");
        if (RemoteFolderNeedsConfirmation)
            throw new ArgumentException("Review the OneDrive account and choose Save remote settings to confirm this shared folder before sending a narration.");
        var folder = OpenRemoteFolder() ?? throw new ArgumentException("Choose the shared remote queue folder first.");
        var source = Source;
        var title = string.IsNullOrWhiteSpace(DraftTitle) ? TextPreparation.SuggestTitle(source) : DraftTitle.Trim();
        var choices = narrationPreferences.Current with { Profile = CaptureProfile() };
        var preset = RemotePresets.FirstOrDefault(p => p.Options == choices)?.Name;
        var request = pendingRemoteSubmission;
        if (request is null || request.TargetWorkerId != workerId || request.Text != source || request.Title != title || request.Options != choices)
            request = new(Guid.NewGuid().ToString("N"), workerId, DateTimeOffset.UtcNow, title, source, choices, Preset: preset);
        pendingRemoteSubmission = request;
        await folder.SubmitAsync(request, ct);
        pendingRemoteSubmission = null;
        if (Source == source && (DraftTitle.Trim() == title || string.IsNullOrWhiteSpace(DraftTitle)))
        {
            ClearVersionDraft();
            Source = "";
            DraftTitle = "";
            GenerationPrompt = "";
        }
        StatusMessage = "Request saved to OneDrive. Open Remote queue to see when the worker accepts it.";
        return request.JobId;
    }
}
