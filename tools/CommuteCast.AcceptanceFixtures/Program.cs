using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Text.Json;

// Synthetic developer fixtures only. Never packaged and never accepts the live workspace.
if (args.Length is < 2 or > 3 || args[0] is not ("seed" or "inspect")) throw new ArgumentException("seed|inspect <isolated artifact private-root> [label]");
var repository = new DirectoryInfo(AppContext.BaseDirectory);
while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "CommuteCast.slnx"))) repository = repository.Parent;
if (repository is null) throw new IOException("Run this fixture host from its repository build.");
var artifactRoot = Path.Combine(repository.FullName, "artifacts", "installation-acceptance"); var root = Path.GetFullPath(args[1]);
if (!Workspace.IsWithin(artifactRoot, root) || Path.GetFileName(root) != "private") throw new IOException("Fixture writes are restricted to an isolated installation-acceptance private folder.");
Workspace.RejectReparsePoints(root); var workspace = new Workspace(root); using var lease = WorkspaceLease.Acquire(workspace);
if (args[0] == "seed")
{
    var label = args.Length == 3 ? args[2] : "original";
    if (label is not ("original" or "later")) throw new ArgumentException("Choose the synthetic original or later fixture label.");
    var destination = Path.Combine(Path.GetDirectoryName(root)!, "separate-exports"); Directory.CreateDirectory(destination); workspace.GuardLocalDestination(destination);
    var source = "Synthetic " + label + " queued narration — preserve source, timestamp, frozen settings and its checked PCM artifact.";
    var job = new Job { Title = "Synthetic " + label, Source = source, Prepared = TextPreparation.Prepare(source), Destination = destination, Stage = JobStage.Queued,
        Settings = new("piper", "en_US-lessac-medium", 1.1, false, "API=A P I", "piper:contract-v1:" + new string('b', 64)) };
    job.Chunks = Chunker.Split(job.Prepared.Script, 450); Directory.CreateDirectory(workspace.JobDirectory(job.Id));
    using (var file = File.Create(workspace.ChunkPath(job, 0)))
    {
        WaveAudio.WriteHeader(file, 24000); using var writer = new BinaryWriter(file);
        for (var i = 0; i < 24000; i++) writer.Write((short)(Math.Sin(i * 2 * Math.PI * 220 / 24000) * 8000));
    }
    job.CompletedChunks = 1; job.Receipts.Add(new(0, await Workspace.HashFileAsync(workspace.ChunkPath(job, 0)), job.Fingerprint, 1));
    await new SqliteJobStore(workspace).SaveAsync(job);
    await workspace.SaveSettingsAsync(new() { Destination = destination, QueuePaused = true, Engine = "piper", Voice = "en_US-lessac-medium", Speed = 1.1 });
    await new DraftStore(workspace).SaveAsync(new("Synthetic " + label + " draft", source));
    await Workspace.AtomicWriteAsync(Path.Combine(root, "provider-lock.local.json"), JsonSerializer.Serialize(new { ImageId = "sha256:" + new string('a', 64), Contract = 1 }));
    // Acceptance UI must not attempt Docker startup or dispatch synthetic work.
    if (!File.Exists(Path.Combine(root, "recovery-piper.json"))) await new RecoveryBudget(workspace).BeginAsync("piper", default);
}
var jobs = await new SqliteJobStore(workspace).LoadAsync(); var artifacts = new List<object>();
foreach (var job in jobs)
{
    var chunks = new List<object>();
    foreach (var receipt in job.Receipts) chunks.Add(new { receipt.Index, hash = await Workspace.HashFileAsync(workspace.ChunkPath(job, receipt.Index)) });
    artifacts.Add(new { job.Id, job.Stage, job.CreatedUtc, immutablePayloadHash = Job.Hash(JsonSerializer.Serialize(job)), chunks });
}
Console.WriteLine(JsonSerializer.Serialize(new { jobs = artifacts, draftHash = await Workspace.HashFileAsync(Path.Combine(root, "draft.json")), settingsHash = await Workspace.HashFileAsync(Path.Combine(root, "settings.json")), providerPinHash = await Workspace.HashFileAsync(Path.Combine(root, "provider-lock.local.json")) }, new JsonSerializerOptions { WriteIndented = true }));
