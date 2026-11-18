using System.Text.Json.Serialization;

namespace CommuteCast.Core;

public sealed record RemoteRequest(string JobId, string TargetWorkerId, DateTimeOffset CreatedUtc, string Title, string Text,
    NarrationOptions Options, int Version = 1, string? Preset = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool AllowHostedFallback = false)
{
    public const int CurrentVersion = 2;

    public void Validate()
    {
        if (Version is not (1 or CurrentVersion) || Version == 1 && AllowHostedFallback ||
            !RemoteIdentity.IsValid(JobId) || !RemoteIdentity.IsValid(TargetWorkerId) || CreatedUtc == default)
            throw new ArgumentException("The remote request version or identity is invalid.");
        if (string.IsNullOrWhiteSpace(Title) || Title.Length > 200 || string.IsNullOrWhiteSpace(Text) ||
            Text.Length > TextPreparation.MaximumCharacters || Options?.Profile is null || Options.Pronunciation is null ||
            Preset is { Length: > 60 } || Preset?.Any(char.IsControl) == true)
            throw new ArgumentException("A remote request needs a title, narration text and valid voice settings.");
        if (Options.Voice?.Length > 160 || SpeechProviders.IsKnown(Options.Engine) &&
            SpeechProviders.IsHosted(Options.Engine) != (Options.Speech is not null))
            throw new ArgumentException("A remote request must name the selected voice and capture its hosted model when applicable.");
        Options.Validate();
    }
}

public enum RemoteJobState { Queued, Generating, Uploading, Ready, NeedsAttention }

public sealed record RemoteVoice(string Engine, string Voice, double Speed, string? Model = null, string? Preset = null,
    LocalVoiceOptions? LocalDelivery = null, bool HasDeliveryInstructions = false, string? Emotion = null)
{
    public static RemoteVoice From(NarrationOptions options, string? preset = null) =>
        new(options.Engine, options.Voice, options.Speed, options.Speech?.Model, preset, options.LocalVoice,
            options.Speech?.Delivery.Length > 0, options.Speech?.Emotion);
}

// Stored alongside the accepted narration in the worker's private queue.
public sealed record RemoteRender(string WorkerId, string QueueRoot, string RequestFingerprint,
    RemoteVoice RequestedVoice, RemoteVoice ActualVoice, string? FallbackReason, RemoteDelivery? Delivery = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool AllowHostedFallback = false);

public sealed record RemoteDelivery(string OutputFile, string Hash, DateTimeOffset ConfirmedUtc);

public sealed record RemoteJobStatus(string JobId, string WorkerId, DateTimeOffset UpdatedUtc, RemoteJobState State,
    RemoteVoice RequestedVoice, RemoteVoice? ActualVoice = null, int ChunksDone = 0, int ChunksTotal = 0,
    int? QueuePosition = null, long? EstimatedSecondsRemaining = null, string? OutputFile = null,
    double? DurationSeconds = null, string? FallbackReason = null, string? Attention = null, int Version = 1);

public enum RemoteWorkerState { Starting, Ready, Busy, NeedsAttention, Stopped, Disabled, Offline }

public sealed record RemoteQueueSettings(string Folder, bool EnableWorker, string WorkerName, string RegistrationId,
    string[] FallbackPresets, bool AllowHosted = false);

public sealed record RemoteWorkerStatus(string WorkerId, string Name, DateTimeOffset UpdatedUtc, RemoteWorkerState State,
    string[] Presets, int Version = 1)
{
    public int RequestVersion { get; init; } = 1;

    public static TimeSpan CheckInInterval { get; } = TimeSpan.FromMinutes(1);
    public static TimeSpan OfflineAfter { get; } = TimeSpan.FromMinutes(4);

    public RemoteWorkerState StateAt(DateTimeOffset now) =>
        State is RemoteWorkerState.Disabled or RemoteWorkerState.Stopped ? State :
        UpdatedUtc < now - OfflineAfter || UpdatedUtc > now + OfflineAfter ? RemoteWorkerState.Offline : State;
}

public static class RemoteIdentity
{
    public static bool IsValid(string? value) => value is { Length: 32 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
