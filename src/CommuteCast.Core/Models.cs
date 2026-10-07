using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CommuteCast.Core;

public enum JobStage { Queued, Preparing, WaitingForService, Synthesizing, Assembling, Validating, Generated, Exporting, Exported, Failed, Cancelled, Deleting }
public enum FailureCategory { None, Cancelled, Timeout, Prerequisite, ServiceConnection, ServiceContract, AccessDenied, AudioValidation, Export, Storage, Unexpected }
public record DiagnosticEvent(string JobId, JobStage Stage, DateTimeOffset Timestamp, double? ElapsedSincePreviousMs);
public record SourceSpan(int Start, int Length, string Kind, string Original, string Narration);
public record PreparedText(string Script, IReadOnlyList<SourceSpan> Spans, string Version = "prepare-v1", PronunciationReview? ProfileReview = null);
public record TextChunk(int Index, int Start, int Length, string Text, bool HardSplit);
public record NarrationSettings(string Engine, string Voice, double Speed, bool ExcludeCode, string Pronunciation, string ProviderFingerprint, PronunciationProfile? Profile = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProviderImageId = null)
{
    public void ValidateProviderImage()
    {
        if (ProviderImageId is not null && !Regex.IsMatch(ProviderImageId, "^sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("The captured speech image identity is invalid. Check readiness and submit a new narration.");
    }
}
public sealed class Job
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public long QueuePosition { get; set; }
    public string Title { get; set; } = "";
    public string Source { get; set; } = "";
    public PreparedText Prepared { get; set; } = new("", []);
    public NarrationSettings Settings { get; set; } = new("kokoro", "af_heart", 1, false, "", "");
    public string Destination { get; set; } = "";
    public string AudioContractVersion { get; set; } = "pcm24k-s16le-mono-mp3128-gap150-v1";
    public string ChunkingVersion { get; set; } = "chunk450-v1";
    public JobStage Stage { get; set; } = JobStage.Queued;
    public List<TextChunk> Chunks { get; set; } = [];
    public List<ChunkReceipt> Receipts { get; set; } = [];
    public List<PrivateArtifactReceipt> PrivateArtifacts { get; set; } = [];
    public string PrivateStorageNotice { get; set; } = "";
    public int CompletedChunks { get; set; }
    public string Error { get; set; } = "";
    public FailureCategory FailureCategory { get; set; }
    public JobStage? FailedStage { get; set; }
    public int Attempts { get; set; }
    public string FinalHash { get; set; } = "";
    public double DurationSeconds { get; set; }
    public string ExportName { get; set; } = "";
    public string ExportHash { get; set; } = "";
    public bool ExportStagingOwned { get; set; }
    public ExportStagingIdentity? ExportStagingIdentity { get; set; }
    public string ExportNotice { get; set; } = "";
    public bool ExportCommitted { get; set; }
    private volatile bool cancellationRequested;
    public bool CancellationRequested { get => cancellationRequested; set => cancellationRequested = value; }
    public bool DeletionRequested { get; set; }
    public bool DeleteExportRequested { get; set; }
    public string Fingerprint => Hash(JsonSerializer.Serialize(new { Settings = FingerprintSettings(), Prepared.Version, Prepared.Script, AudioContractVersion, ChunkingVersion }));
    // Preserve the exact six-field legacy and seven-field pronunciation snapshots when no image was captured.
    private object FingerprintSettings() => Settings.Profile is null && Settings.ProviderImageId is null
        ? new { Settings.Engine, Settings.Voice, Settings.Speed, Settings.ExcludeCode, Settings.Pronunciation, Settings.ProviderFingerprint }
        : Settings;
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
public record ChunkReceipt(int Index, string Hash, string Fingerprint, double Duration);
public record PrivateArtifactReceipt(string RelativePath, string Hash,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExportStagingIdentity? PromotionIdentity = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExportStagingIdentity? CreationIdentity = null);
public record ExportStagingIdentity(int FormatVersion, ulong VolumeSerialNumber, string FileId, long CreationFileTime);
public record ProviderInfo(string Engine, string Fingerprint, string[] Voices, string State, int Active,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ImageId = null,
    string? InstanceId = null, long? AdmissionSequence = null);
public record AudioInfo(double Duration, int SampleRate, int Channels, long Samples, double Peak, double Rms);
public sealed class AppSettings
{
    public string Destination { get; set; } = "";
    public string Engine { get; set; } = "kokoro";
    public string Voice { get; set; } = "af_heart";
    public double Speed { get; set; } = 1;
    public string Pronunciation { get; set; } = "";
    public PronunciationProfile PronunciationProfile { get; set; } = new();
    public bool ExcludeCode { get; set; }
    public bool QueuePaused { get; set; }
    public string Ffmpeg { get; set; } = "ffmpeg";
    public string Ffprobe { get; set; } = "ffprobe";
    public int CacheQuotaMiB { get; set; } = 1024;
    public int ScratchRetentionDays { get; set; } = 7;
    public int PrivateStorageLimitMiB { get; set; } = 10240;
    public Dictionary<string, ProviderInfo> Providers { get; set; } = [];
}
public interface IJobStore
{
    Task SaveAsync(Job job, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, bool>> RequestDeletionAsync(IReadOnlyList<string> ids, bool deleteExports, CancellationToken ct = default);
    Task SaveQueueOrderAsync(IReadOnlyDictionary<string, long> positions, CancellationToken ct = default);
    Task<IReadOnlyList<Job>> LoadAsync(CancellationToken ct = default);
    Task RemoveAsync(string id, CancellationToken ct = default);
}
public interface ISpeechProvider
{
    Task<ProviderInfo> ReadyAsync(string engine, CancellationToken ct);
    Task SynthesizeAsync(NarrationSettings settings, string text, string output, CancellationToken ct);
}
public interface IDurableSpeechProvider : ISpeechProvider
{
    Task SynthesizeAsync(Job job, NarrationSettings settings, string text, string output, Func<Task> checkpoint, CancellationToken ct);
}
public sealed record AuditionWriteJournal(string Id, DateTimeOffset CreatedUtc, List<PrivateArtifactReceipt> Artifacts);
public interface IDurableAuditionSpeechProvider : ISpeechProvider
{
    Task SynthesizeAuditionAsync(AuditionWriteJournal journal, NarrationSettings settings, string text, string output, Func<Task> checkpoint, CancellationToken ct);
}
public interface IAudioPipeline
{
    Task<AudioInfo> ValidateChunkAsync(string path, string text, CancellationToken ct);
    Task AssembleAsync(Job job, string directory, CancellationToken ct);
    Task<AudioInfo> ValidateFinalAsync(Job job, string path, CancellationToken ct);
}
