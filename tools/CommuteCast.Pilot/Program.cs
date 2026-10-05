using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Text.Json;
using System.Diagnostics;

var engine = args.FirstOrDefault() ?? "kokoro";
if (engine is not ("kokoro" or "piper")) throw new ArgumentException("Use kokoro or piper.");
var root = Path.GetFullPath(args.ElementAtOrDefault(1) ?? Path.Combine("artifacts", "pilot", engine + "-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss")));
var workspace = new Workspace(Path.Combine(root, "private"));
var production = new Workspace();
File.Copy(Path.Combine(production.Root, "provider-lock.local.json"), Path.Combine(workspace.Root, "provider-lock.local.json"), true);
var destination = Path.Combine(root, "output"); Directory.CreateDirectory(destination);
var store = new SqliteJobStore(workspace);
using var provider = new LocalSpeechProvider(workspace);
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
var info = await provider.ReadyAsync(engine, timeout.Token);
var text = "# A better commute\n\n" + string.Join("\n\n", new[]
{
    "First, preserve the original idea. CommuteCast turns substantial text into an ordered narration for listening on the road. A trustworthy result contains every paragraph you approved, in its original order. It should never silently summarize your source or skip a section just because the submission is long.",
    "Second, consider technical details. The API processes twenty-four requests per second. The service uses a local SQLite database and a durable queue. Version 10 of dot NET supports the Windows desktop application. A pronunciation dictionary can make acronyms easier to understand without changing their meaning.",
    "Third, prepare for interruptions. A laptop may sleep, Docker may stop, or a destination may become unavailable. The application keeps validated chunks and resumes compatible work. It creates a single finished MP3 only after the complete ordered sequence has passed audio validation.",
    "Fourth, distinguish local export from delivery. A file in a local OneDrive folder does not prove cloud upload. Check the OneDrive client and the actual Android account before leaving. Once the file is uploaded, confirm playback with the laptop switched off, and prepare any offline downloads while stationary.",
    "Finally, judge the experience by listening. Automated coverage, checksums, decoding, and duration checks are useful evidence, but they do not prove that every word was pronounced correctly. Listen for pace, volume, technical intelligibility, and joins. This is the final paragraph. The end."
});
var job = new Job { Title = "A better commute — " + engine + " pilot", Source = text, Prepared = TextPreparation.Prepare(text), Settings = new(engine, engine == "kokoro" ? "af_heart" : "en_US-lessac-medium", 1, false, "", info.Fingerprint), Destination = destination };
var watch = Stopwatch.StartNew();
await using var queue = new QueueCoordinator(workspace, store, provider, new(new()), new(workspace, store));
var previous = "";
queue.Changed += items =>
{
    var current = items.FirstOrDefault(); if (current is null) return;
    var status = $"{current.Stage}: {current.CompletedChunks}/{current.Chunks.Count}";
    if (status != previous) { Console.WriteLine(status); previous = status; }
};
await queue.InitializeAsync(timeout.Token);
await queue.AddAsync(job, timeout.Token);
Job finished;
while (true)
{
    finished = queue.Snapshot().Single();
    if (finished.Stage is JobStage.Exported or JobStage.Failed or JobStage.Cancelled) break;
    await Task.Delay(250, timeout.Token);
}
if (finished.Stage != JobStage.Exported) throw new IOException(finished.Error);
var exported = Path.Combine(destination, finished.ExportName);
if (await Workspace.HashFileAsync(exported) != finished.FinalHash) throw new IOException("Pilot export checksum failed.");
var report = new { engine, provider = info.Fingerprint, submittedCharacters = text.Length, sourceAccounted = finished.Prepared.Spans.Sum(s => s.Length) == text.Length, scriptCoverage = string.Concat(finished.Chunks.Select(c => c.Text)) == finished.Prepared.Script, chunks = finished.Chunks.Count, finished.CompletedChunks, finished.DurationSeconds, wallSeconds = watch.Elapsed.TotalSeconds, realTimeFactor = watch.Elapsed.TotalSeconds / finished.DurationSeconds, mp3 = exported, sha256 = finished.FinalHash, generation = "passed", localExport = "passed", cloudUpload = "unknown", listeningQuality = "requires user audition", phonePlayback = "not performed" };
await File.WriteAllTextAsync(Path.Combine(root, "pilot-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
