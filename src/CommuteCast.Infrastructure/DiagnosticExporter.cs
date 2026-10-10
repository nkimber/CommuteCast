using CommuteCast.Core;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CommuteCast.Infrastructure;

public sealed class DiagnosticExporter(Workspace workspace, SqliteJobStore store)
{
    public async Task<string> BuildAsync(IReadOnlyList<Job> jobs, string encoderVersion, CancellationToken ct = default)
    {
        var events = await store.ReadDiagnosticEventsAsync(ct);
        var usage = await new CacheMaintenance(workspace).MeasureAsync(jobs, null, ct);
        var package = new
        {
            application = "CommuteCast 0.1.0", runtime = Environment.Version.ToString(), windows = Environment.OSVersion.Version.ToString(),
            exportedUtc = DateTimeOffset.UtcNow, encoder = encoderVersion,
            eventLimit = 2000, timingMeaning = "Elapsed since the preceding persisted event for this job; includes waiting and checkpoint work, not isolated inference timing.",
            storage = new { usage.TotalBytes, usage.CacheBytes, usage.ReclaimableBytes, usage.ReservedBytes },
            jobs = jobs.Select(j => new
            {
                jobId = j.Id.Length == 32 && j.Id.All(Uri.IsHexDigit) ? j.Id : "invalid",
                stage = j.Stage.ToString(), j.CompletedChunks, chunks = j.Chunks.Count, j.DurationSeconds, j.Attempts,
                failedStage = j.FailedStage?.ToString(), failureCategory = j.FailureCategory.ToString(),
                engine = SpeechProviders.IsKnown(j.Settings.Engine) ? j.Settings.Engine : "unrecognized",
                provider = Regex.IsMatch(j.Settings.ProviderFingerprint, "^((kokoro|piper):contract-v1|(openai|elevenlabs|cartesia|gemini):hosted-v1):[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant) ? j.Settings.ProviderFingerprint : "unverified",
                podcastSpeakers = j.Episode?.Speakers.Count,
                hostedAttempts = j.SpeechAttempts?.Select(a => new { state = a.State is "started" or "received" or "uncertain" or "rejected" ? a.State : "unverified", a.StartedUtc, a.UnitIndex, a.BilledCharacters }),
                generatedLocally = j.FinalHash.Length > 0 && File.Exists(workspace.FinalPath(j)), exportedLocally = j.ExportCommitted,
                cloudUpload = "unknown"
            }),
            events
        };
        return JsonSerializer.Serialize(package, new JsonSerializerOptions { WriteIndented = true });
    }
}
