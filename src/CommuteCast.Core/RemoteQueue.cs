namespace CommuteCast.Core;

public sealed record RemoteRequest(string JobId, DateTimeOffset CreatedUtc, string Title, string Text,
    NarrationOptions Options, int Version = 1)
{
    public void Validate()
    {
        if (Version != 1 || !RemoteIdentity.IsValid(JobId) || CreatedUtc == default)
            throw new ArgumentException("The remote request version or identity is invalid.");
        if (string.IsNullOrWhiteSpace(Title) || Title.Length > 200 || string.IsNullOrWhiteSpace(Text) ||
            Text.Length > TextPreparation.MaximumCharacters || Options?.Profile is null || Options.Pronunciation is null)
            throw new ArgumentException("A remote request needs a title, narration text and valid voice settings.");
        if (Options.Voice?.Length > 160 || SpeechProviders.IsKnown(Options.Engine) &&
            SpeechProviders.IsHosted(Options.Engine) != (Options.Speech is not null))
            throw new ArgumentException("A remote request must name the selected voice and capture its hosted model when applicable.");
        Options.Validate();
    }
}

public enum RemoteJobState { Queued, Generating, Uploading, Ready, NeedsAttention }

public sealed record RemoteVoice(string Engine, string Voice, double Speed, string? Model = null, string? Preset = null)
{
    public static RemoteVoice From(NarrationOptions options, string? preset = null) =>
        new(options.Engine, options.Voice, options.Speed, options.Speech?.Model, preset);
}

public sealed record RemoteJobStatus(string JobId, string WorkerId, DateTimeOffset UpdatedUtc, RemoteJobState State,
    RemoteVoice RequestedVoice, RemoteVoice? ActualVoice = null, int ChunksDone = 0, int ChunksTotal = 0,
    int? QueuePosition = null, long? EstimatedSecondsRemaining = null, string? OutputFile = null,
    double? DurationSeconds = null, string? FallbackReason = null, string? Attention = null, int Version = 1);

public enum RemoteWorkerState { Starting, Ready, Busy, NeedsAttention, Stopped, Disabled, Offline }

public sealed record RemoteWorkerStatus(string WorkerId, string Name, DateTimeOffset UpdatedUtc, RemoteWorkerState State,
    string[] Presets, int Version = 1)
{
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
