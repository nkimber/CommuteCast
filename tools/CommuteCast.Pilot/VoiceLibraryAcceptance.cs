using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Diagnostics;
using System.Text.Json;

internal static class VoiceLibraryAcceptance
{
    public static async Task RunAsync(string root, Workspace workspace, SqliteJobStore store, LocalSpeechProvider provider,
        ProviderInfo info, string destination, CancellationToken ct)
    {
        const string text = "Welcome to CommuteCast. Choose a voice you enjoy, and turn your next reading into something worth hearing on your commute.";
        var profile = new PronunciationProfile();
        await using var queue = new QueueCoordinator(workspace, store, provider, new(new()), new(workspace, store));
        await queue.InitializeAsync(ct);
        var results = new List<object>();
        foreach (var voice in info.Voices)
        {
            var watch = Stopwatch.StartNew();
            var job = new Job { Title = info.Engine + " voice — " + voice, Source = text, Prepared = TextPreparation.Prepare(text, profile: profile),
                Settings = new(info.Engine, voice, 1, false, "", info.Fingerprint, profile, info.ImageId), Destination = destination };
            await queue.AddAsync(job, ct);
            Job finished;
            while (true)
            {
                finished = queue.Snapshot().Single(j => j.Id == job.Id);
                if (finished.Stage is JobStage.Exported or JobStage.Failed or JobStage.Cancelled) break;
                await Task.Delay(100, ct);
            }
            if (finished.Stage != JobStage.Exported) throw new IOException(voice + ": " + finished.Error);
            Chunker.ValidateManifest(finished.Chunks, finished.Prepared.Script);
            var retained = (await store.LoadAsync(ct)).Single(j => j.Id == job.Id);
            var exported = Path.Combine(destination, finished.ExportName);
            if (retained.Settings != job.Settings || retained.Stage != JobStage.Exported ||
                finished.Receipts.Count != finished.Chunks.Count || finished.Receipts.Any(r => r.Fingerprint != finished.Fingerprint) ||
                await Workspace.HashFileAsync(exported, ct) != finished.FinalHash || finished.DurationSeconds <= 0)
                throw new IOException("Voice selection, complete chunk receipts or MP3 publication did not retain the captured voice.");
            watch.Stop();
            results.Add(new { voice, finished.DurationSeconds, wallSeconds = watch.Elapsed.TotalSeconds, mp3 = exported,
                sha256 = finished.FinalHash, frozenSettingsVerified = true, orderedReceiptsVerified = true, mp3ValidatedAndExported = true });
            Console.WriteLine($"{info.Engine}: {results.Count}/{info.Voices.Length} voices validated — {voice}");
        }
        var report = new { engine = info.Engine, voiceCount = info.Voices.Length, fingerprint = info.Fingerprint,
            imageId = info.ImageId, samples = results, listeningApproval = "not performed", nativeUiAcceptance = "separate check" };
        await File.WriteAllTextAsync(Path.Combine(root, "voice-library-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), ct);
        Console.WriteLine($"Passed: {info.Voices.Length} {info.Engine} voices synthesized, validated, encoded and exported.");
    }
}
