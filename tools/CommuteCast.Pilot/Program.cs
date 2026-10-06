using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Text.Json;
using System.Diagnostics;
using System.Reflection;

var engine = args.FirstOrDefault() ?? "kokoro";
if (engine is not ("kokoro" or "piper")) throw new ArgumentException("Use kokoro or piper.");
if (args.Length > 2) throw new ArgumentException("Use an engine and an optional fresh folder under artifacts/pilot.");
var pilotParent = Path.GetFullPath(Path.Combine("artifacts", "pilot"));
var root = Path.GetFullPath(args.ElementAtOrDefault(1) ?? Path.Combine(pilotParent, engine + "-" + Guid.NewGuid().ToString("N")));
if (!Workspace.IsWithin(pilotParent, root) || root.Equals(pilotParent, StringComparison.OrdinalIgnoreCase) || Directory.Exists(root) || File.Exists(root))
    throw new ArgumentException("Choose a fresh isolated folder under artifacts/pilot. Existing evidence is preserved.");
Workspace.RejectReparsePoints(root);
var pinSource = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CommuteCast", "provider-lock.local.json");
if (!File.Exists(pinSource)) throw new IOException("Provision the speech engines before running acceptance.");
var workspace = new Workspace(Path.Combine(root, "private"));
using var lease = WorkspaceLease.Acquire(workspace);
var pinPath = Path.Combine(workspace.Root, "provider-lock.local.json"); File.Copy(pinSource, pinPath, false);
using var pin = JsonDocument.Parse(await File.ReadAllTextAsync(pinPath)); var imageId = pin.RootElement.GetProperty("ImageId").GetString();
var destination = Path.Combine(root, "output"); Directory.CreateDirectory(destination);
var store = new SqliteJobStore(workspace);
using var provider = new LocalSpeechProvider(workspace);
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
var readiness = Stopwatch.StartNew();
var info = await provider.ReadyAsync(engine, timeout.Token);
readiness.Stop();
var text = "# A better commute\n\n" + string.Join("\n\n", new[]
{
    "First, preserve the original idea. CommuteCast turns substantial text into an ordered narration for listening on the road. A trustworthy result contains every paragraph you approved, in its original order. It should never silently summarize your source or skip a section just because the submission is long.",
    "Second, consider technical details. The API processes 24 requests per second. SQLite keeps the queue durable. Version v2.10.0 uses .NET 10. A measurement is 1.25e-3; a precise amount is 12.50 dollars. The release date is 2026-10-06. This pronunciation profile spells uppercase words and keeps version digits distinct, without guessing an acronym's meaning.",
    "Third, prepare for interruptions. A laptop may sleep, Docker may stop, or a destination may become unavailable. The application keeps validated chunks and resumes compatible work. It creates a single finished MP3 only after the complete ordered sequence has passed audio validation.",
    "Fourth, distinguish local export from delivery. A file in a local OneDrive folder does not prove cloud upload. Check the OneDrive client and the actual Android account before leaving. Once the file is uploaded, confirm playback with the laptop switched off, and prepare any offline downloads while stationary.",
    "| Check | Result |\n| --- | --- |\n| Unicode | café 😀 |\n| Literal code | `x_1 = 7;` |",
    "Finally, judge the experience by listening. Automated coverage, checksums, decoding, and duration checks are useful evidence, but they do not prove that every word was pronounced correctly. Listen for pace, volume, technical intelligibility, and joins. This is the final paragraph. The end."
});
var profile = new PronunciationProfile(Numbers: NumberReading.ScientificWords, Acronyms: AcronymReading.SpellUppercaseWords, Dates: DateReading.IsoYearMonthDay);
const string dictionary = "CommuteCast=Commute Cast\nSQLite=S Q Lite\n.NET=dot net";
var job = new Job { Title = "A better commute — " + engine + " pilot", Source = text, Prepared = TextPreparation.Prepare(text, pronunciation: dictionary, profile: profile), Settings = new(engine, engine == "kokoro" ? "af_heart" : "en_US-lessac-medium", 1, false, dictionary, info.Fingerprint, profile), Destination = destination };
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
Chunker.ValidateManifest(finished.Chunks, finished.Prepared.Script);
if (string.Concat(finished.Prepared.Spans.Select(s => s.Original)) != text || string.Concat(finished.Prepared.Spans.Select(s => s.Narration)) != finished.Prepared.Script || finished.Receipts.Count != finished.Chunks.Count || finished.Receipts.Any(r => r.Fingerprint != finished.Fingerprint))
    throw new IOException("Source, script or frozen receipt accounting failed.");
var retained = (await store.LoadAsync()).Single();
if (retained.Source != text || retained.Settings != job.Settings || retained.Prepared.Script != job.Prepared.Script || retained.Fingerprint != job.Fingerprint || retained.Stage != JobStage.Exported)
    throw new IOException("Durable completed narration differs from the approved snapshot.");
var exported = Path.Combine(destination, finished.ExportName);
if (await Workspace.HashFileAsync(exported) != finished.FinalHash) throw new IOException("Pilot export checksum failed.");
var report = new { engine, imageId, applicationBuild = typeof(Job).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, provider = info.Fingerprint, profile, dictionaryRevision = finished.Prepared.ProfileReview!.DictionaryRevision, pronunciationChanges = finished.Prepared.ProfileReview.Changes.Count, submittedCharacters = text.Length, preparedCharacters = finished.Prepared.Script.Length, sourceAccounted = true, scriptCoverage = true, durableSnapshotVerified = true, chunks = finished.Chunks.Count, finished.CompletedChunks, finished.DurationSeconds, readinessSeconds = readiness.Elapsed.TotalSeconds, wallSeconds = watch.Elapsed.TotalSeconds, realTimeFactor = watch.Elapsed.TotalSeconds / finished.DurationSeconds, timingScope = "generation, validation and local export; model readiness recorded separately", mp3 = exported, sha256 = finished.FinalHash, generation = "passed", localExport = "passed", cloudUpload = "unknown", listeningQuality = "requires user audition", phonePlayback = "not performed" };
await File.WriteAllTextAsync(Path.Combine(root, "pilot-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
