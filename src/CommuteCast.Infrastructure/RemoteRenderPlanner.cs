using CommuteCast.Core;

namespace CommuteCast.Infrastructure;

public sealed class RemoteSpeechUnavailableException(string message) : IOException(message);

public sealed class RemoteRenderPlanner(
    Func<NarrationSettings, CancellationToken, Task<ProviderInfo>> checkSpeech,
    IReadOnlyList<NarrationPreset> fallbacks, bool allowHosted = false)
{
    public async Task<bool> IsAvailableAsync(NarrationOptions options, CancellationToken ct) =>
        (await TrySettingsAsync(options, ct)).Settings is not null;

    internal async Task<(Job? Job, string? Attention)> TryPrepareAsync(RemoteRequest request, string workerId, string queueRoot, CancellationToken ct)
    {
        try { return (await PrepareAsync(request, workerId, queueRoot, ct), null); }
        catch (RemoteSpeechUnavailableException error) { return (null, error.Message); }
        catch (ArgumentException) { return (null, "This narration could not be prepared. Review its text and voice choices on the submitting PC."); }
    }

    public async Task<Job> PrepareAsync(RemoteRequest request, string workerId, string queueRoot, CancellationToken ct)
    {
        request.Validate();
        var requested = request.Options;
        var selection = await TrySettingsAsync(requested, ct);
        var requestedVoice = RemoteVoice.From(requested, request.Preset);
        var actualVoice = requestedVoice;
        var reason = selection.Reason;
        if (selection.Settings is null)
        {
            foreach (var preset in fallbacks)
            {
                var options = requested with
                {
                    Engine = preset.Options.Engine, Voice = preset.Options.Voice,
                    LocalVoice = preset.Options.LocalVoice, Speech = preset.Options.Speech
                };
                // Preserve the requested pace when the substitute supports it.
                if (options.Engine == "gemini" && options.Speed != 1 || options.Engine == "elevenlabs" && options.Speed > 1.2)
                    options = options with { Speed = preset.Options.Speed };
                selection = await TrySettingsAsync(options, ct);
                if (selection.Settings is null) continue;
                actualVoice = RemoteVoice.From(options, preset.Name);
                break;
            }
        }
        if (selection.Settings is not { } settings)
            throw new RemoteSpeechUnavailableException(reason + " None of this worker's configured fallbacks is usable. Check the speech setup on the worker.");

        var prepared = TextPreparation.Prepare(request.Text, requested.ExcludeCode, requested.Pronunciation, requested.Profile, ct);
        var job = new Job
        {
            Id = request.JobId, CreatedUtc = request.CreatedUtc, Title = request.Title, Source = request.Text, Prepared = prepared,
            Settings = settings, Remote = new(workerId, queueRoot, RemoteQueueFolder.Fingerprint(request), requestedVoice, actualVoice, reason)
        };
        NaturalChunker.ConfigureNew(job);
        return job;
    }

    private async Task<(NarrationSettings? Settings, string? Reason)> TrySettingsAsync(NarrationOptions options, CancellationToken ct)
    {
        if (!SpeechProviders.IsKnown(options.Engine)) return (null, "The requested speech engine is unsupported.");
        var hosted = SpeechProviders.IsHosted(options.Engine);
        if (hosted && !allowHosted) return (null, "This worker is configured for local speech only.");
        try { options.Validate(); }
        catch (ArgumentException) { return (null, "The voice or delivery settings are incompatible with this model."); }
        var settings = new NarrationSettings(options.Engine, options.Voice, options.Speed, options.ExcludeCode,
            options.Pronunciation, "", options.Profile, Speech: options.Speech, LocalVoice: options.LocalVoice);
        if (hosted && options.Speech is null) return (null, "The hosted model was not captured in the request.");
        ProviderInfo info;
        try { info = await checkSpeech(settings, ct); }
        catch (Exception error) when (error is IOException or TimeoutException or HttpRequestException or ArgumentException or InvalidOperationException)
        { return (null, "The requested speech service or model could not be verified on this worker."); }
        if (info.Engine != options.Engine || info.State != "ready" || string.IsNullOrWhiteSpace(info.Fingerprint) ||
            options.Speech is { } speech && info.Fingerprint != speech.Identity(options.Engine))
            return (null, "The requested speech model is unavailable on this worker.");
        if (!info.Voices.Contains(options.Voice)) return (null, "The requested voice is unavailable on this worker.");
        try { options.LocalVoice?.RequireAvailable(info, options.Voice); }
        catch (Exception error) when (error is IOException or ArgumentException)
        { return (null, "The requested local delivery profile is unavailable on this worker."); }
        return (settings with { ProviderFingerprint = info.Fingerprint, ProviderImageId = info.ImageId }, null);
    }
}
