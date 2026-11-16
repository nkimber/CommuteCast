using System.Collections.Concurrent;
using System.Text.Json;
using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Web;

public sealed record WebRemoteSubmission(string JobId, string TargetWorkerId, DateTimeOffset CreatedUtc,
    string Title, string Text, string PresetId);

public sealed class WebRemoteQueue(IConfiguration configuration, WebRuntime runtime)
{
    private readonly ConcurrentDictionary<(string Root, string Id), RequestSummary> summaries = new();

    public async Task<object> ReadAsync(CancellationToken ct)
    {
        var folder = OpenFolder();
        if (folder is null) return new { configured = false };
        var registered = await folder.ReadWorkersAsync(ct);
        var titles = registered.ToDictionary(w => w.WorkerId, w => w.Name);
        var now = DateTimeOffset.UtcNow;
        var jobs = new List<JobSummary>();
        foreach (var id in folder.RequestIds())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var key = (folder.Root, id);
                if (!summaries.TryGetValue(key, out var summary))
                {
                    var request = await folder.ReadRequestAsync(id, ct);
                    if (request is null) continue;
                    summary = new(id, request.Title, request.CreatedUtc, request.TargetWorkerId, RemoteVoice.From(request.Options, request.Preset));
                    summaries.TryAdd(key, summary);
                }
                var status = await folder.ReadStatusAsync(id, ct);
                jobs.Add(new(summary.Id, summary.Title, summary.CreatedUtc, titles.GetValueOrDefault(summary.WorkerId),
                    summary.RequestedVoice, status?.WorkerId == summary.WorkerId ? status : null));
            }
            catch (Exception error) when (error is IOException or JsonException or ArgumentException or UnauthorizedAccessException) { }
        }
        return new
        {
            configured = true,
            listeningFolderUrl = ListeningFolderUrl(),
            workers = registered.Select(w => new { w.WorkerId, w.Name, w.UpdatedUtc, state = w.StateAt(now), w.Presets }),
            presets = Choices().Select(p => new { p.Id, p.Name, voice = RemoteVoice.From(p.Options) }),
            jobs = jobs.OrderByDescending(j => j.CreatedUtc).ThenBy(j => j.Id, StringComparer.Ordinal)
        };
    }

    public async Task<string> SubmitAsync(WebRemoteSubmission submission, CancellationToken ct)
    {
        var folder = OpenFolder() ?? throw new ArgumentException("The shared remote queue is not configured on this web host.");
        if (!RemoteIdentity.IsValid(submission.JobId) || !RemoteIdentity.IsValid(submission.TargetWorkerId) ||
            submission.CreatedUtc == default || string.IsNullOrWhiteSpace(submission.Text) || submission.Text.Length > TextPreparation.MaximumCharacters)
            throw new ArgumentException("Choose a worker and provide narration text within the input limit.");

        var title = string.IsNullOrWhiteSpace(submission.Title) ? TextPreparation.SuggestTitle(submission.Text) : submission.Title.Trim();
        if (await folder.ReadRequestAsync(submission.JobId, ct) is { } existing)
        {
            if (existing.TargetWorkerId == submission.TargetWorkerId && existing.CreatedUtc == submission.CreatedUtc &&
                existing.Title == title && existing.Text == submission.Text && ChoiceId(existing.Preset, existing.Options) == submission.PresetId)
                return existing.JobId;
            throw new ArgumentException("This job ID already belongs to a different narration. Refresh to send a separate request.");
        }

        var worker = (await folder.ReadWorkersAsync(ct)).SingleOrDefault(w => w.WorkerId == submission.TargetWorkerId);
        if (worker is null || worker.State == RemoteWorkerState.Disabled)
            throw new ArgumentException("Choose a registered worker that accepts remote requests.");
        var choice = Choices().FirstOrDefault(p => p.Id == submission.PresetId)
            ?? throw new ArgumentException("The selected voice preset changed. Refresh and choose the preset again.");
        await folder.SubmitAsync(new(submission.JobId, worker.WorkerId, submission.CreatedUtc, title, submission.Text,
            choice.Options, Preset: choice.Name), ct);
        return submission.JobId;
    }

    private RemoteQueueFolder? OpenFolder()
    {
        var path = runtime.SharedQueueRoot;
        return string.IsNullOrWhiteSpace(path) ? null : new(path, runtime.Workspace);
    }

    private string? ListeningFolderUrl() => Uri.TryCreate(configuration["RemoteQueue:ListeningFolderUrl"], UriKind.Absolute, out var url) &&
        url.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(url.UserInfo) ? url.AbsoluteUri : null;

    private Choice[] Choices()
    {
        var defaults = runtime.CurrentNarrationDefaults;
        var presets = runtime.Settings.NarrationPresets ?? new NarrationPresets(new()).Items;
        return new[] { new NarrationPreset("Studio defaults", defaults) }.Concat(presets)
            .Select(p => new Choice(ChoiceId(p.Name, p.Options), p.Name, p.Options)).DistinctBy(p => p.Id).ToArray();
    }

    private static string ChoiceId(string? name, NarrationOptions options) => Job.Hash(JsonSerializer.Serialize(new { name, options }));
    private sealed record Choice(string Id, string Name, NarrationOptions Options);
    private sealed record RequestSummary(string Id, string Title, DateTimeOffset CreatedUtc, string WorkerId, RemoteVoice RequestedVoice);
    private sealed record JobSummary(string Id, string Title, DateTimeOffset CreatedUtc, string? Worker,
        RemoteVoice RequestedVoice, RemoteJobStatus? Status);
}
