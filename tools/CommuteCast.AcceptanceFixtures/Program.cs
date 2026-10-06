using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Text.Json;

// Synthetic developer fixtures only. Never packaged and never accepts the live workspace.
if (args.Length < 2 || args[0] is not ("seed" or "seed-audio" or "inspect" or "restore-barrier" or "recover-barrier"))
    throw new ArgumentException("seed|seed-audio|inspect <isolated artifact private-root> [label]; restore-barrier <root> <backup> <checkpoint> [item]; recover-barrier <root> <checkpoint> [item]");
if (args[0] is "seed" or "seed-audio" && (args.Length is < 2 or > 3 || args.Length == 3 && args[2] is not ("original" or "later")) ||
    args[0] == "inspect" && args.Length != 2 || args[0] == "restore-barrier" && args.Length is not (4 or 5) ||
    args[0] == "recover-barrier" && args.Length is not (3 or 4)) throw new ArgumentException("Invalid fixture command scope.");
var repository = new DirectoryInfo(AppContext.BaseDirectory);
while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "CommuteCast.slnx"))) repository = repository.Parent;
if (repository is null) throw new IOException("Run this fixture host from its repository build.");
var artifactRoot = Path.Combine(repository.FullName, "artifacts", "installation-acceptance"); var root = Path.GetFullPath(args[1]);
if (!Workspace.IsWithin(artifactRoot, root) || Path.GetFileName(root) != "private") throw new IOException("Fixture writes are restricted to an isolated installation-acceptance private folder.");
Workspace.RejectReparsePoints(root);
var boundary = args[0] is "restore-barrier" or "recover-barrier";
var marker = Path.Combine(Path.GetDirectoryName(root)!, "boundary.json");
string? backup = null; RestoreCheckpoint restorePoint = default; RestoreRecoveryCheckpoint recoveryPoint = default; string? item = null;
if (boundary)
{
    var point = args[0] == "restore-barrier" ? args[3] : args[2];
    if (args[0] == "restore-barrier")
    {
        if (!Enum.TryParse(point, out restorePoint) || !Enum.IsDefined(restorePoint) || point != restorePoint.ToString()) throw new ArgumentException("Unknown restore checkpoint.");
        backup = Path.GetFullPath(args[2]);
        if (!Workspace.IsWithin(Path.Combine(root, "backups"), backup)) throw new IOException("Use this isolated fixture's own completed backup.");
    }
    else if (!Enum.TryParse(point, out recoveryPoint) || !Enum.IsDefined(recoveryPoint) || point != recoveryPoint.ToString()) throw new ArgumentException("Unknown recovery checkpoint.");
    item = args.Length == (args[0] == "restore-barrier" ? 5 : 4) ? args[^1] : null;
    var perItem = args[0] == "restore-barrier" ? restorePoint is RestoreCheckpoint.OldItemMoved or RestoreCheckpoint.NewItemMoved : recoveryPoint is not RestoreRecoveryCheckpoint.CommittedStateRetained;
    if (perItem != (item is not null) || item is not null && item is not ("queue.db" or "settings.json" or "draft.json" or "provider-lock.local.json" or "recovery-piper.json" or "jobs")) throw new ArgumentException("Choose an actual synthetic managed item for a per-item checkpoint.");
    Workspace.RejectFileReparsePoint(marker);
    if (File.Exists(marker) || Directory.Exists(marker) || !Directory.Exists(root)) throw new IOException("A fresh fixture boundary marker and existing private root are required.");
}
var workspace = new Workspace(root); using var lease = WorkspaceLease.Acquire(workspace);
if (boundary)
{
    var observer = new ProcessBoundary(root, marker, args[0], restorePoint, recoveryPoint, item);
    if (args[0] == "restore-barrier") await WorkspaceBackup.RestoreAsync(lease, backup!, observer);
    else await WorkspaceBackup.RecoverInterruptedAsync(lease, observer: observer);
    throw new IOException("The requested fixture boundary was not reached; no process-kill evidence was produced.");
}
if (args[0] is "seed" or "seed-audio")
{
    var label = args.Length == 3 ? args[2] : "original";
    if (label is not ("original" or "later")) throw new ArgumentException("Choose the synthetic original or later fixture label.");
    var destination = Path.Combine(Path.GetDirectoryName(root)!, "separate-exports"); Directory.CreateDirectory(destination); workspace.GuardLocalDestination(destination);
    var source = "Synthetic " + label + " queued narration — preserve source, timestamp, frozen settings and its checked PCM artifact.";
    var job = new Job { Title = "Synthetic " + label, Source = source, Prepared = TextPreparation.Prepare(source), Destination = destination, Stage = JobStage.Queued,
        Settings = new("piper", "en_US-lessac-medium", 1.1, false, "API=A P I", "piper:contract-v1:" + new string('b', 64)) };
    job.Chunks = Chunker.Split(job.Prepared.Script, 450); Directory.CreateDirectory(workspace.JobDirectory(job.Id));
    var seconds = args[0] == "seed-audio" ? 2 : 1;
    using (var file = File.Create(workspace.ChunkPath(job, 0)))
    {
        WaveAudio.WriteHeader(file, 24000 * seconds); using var writer = new BinaryWriter(file);
        for (var i = 0; i < 24000 * seconds; i++) writer.Write((short)(Math.Sin(i * 2 * Math.PI * 220 / 24000) * 8000));
    }
    job.CompletedChunks = 1; job.Receipts.Add(new(0, await Workspace.HashFileAsync(workspace.ChunkPath(job, 0)), job.Fingerprint, seconds));
    if (args[0] == "seed-audio")
    {
        var audio = new AudioPipeline(new()); await audio.AssembleAsync(job, workspace.JobDirectory(job.Id), default);
        job.DurationSeconds = (await audio.ValidateFinalAsync(job, workspace.FinalPath(job), default)).Duration;
        job.FinalHash = await Workspace.HashFileAsync(workspace.FinalPath(job)); job.Stage = JobStage.Generated;
    }
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
    artifacts.Add(new { job.Id, job.Stage, job.CreatedUtc, immutablePayloadHash = Job.Hash(JsonSerializer.Serialize(job)), chunks,
        finalHash = File.Exists(workspace.FinalPath(job)) ? await Workspace.HashFileAsync(workspace.FinalPath(job)) : null });
}
Console.WriteLine(JsonSerializer.Serialize(new { jobs = artifacts, draftHash = await Workspace.HashFileAsync(Path.Combine(root, "draft.json")), settingsHash = await Workspace.HashFileAsync(Path.Combine(root, "settings.json")), providerPinHash = await Workspace.HashFileAsync(Path.Combine(root, "provider-lock.local.json")) }, new JsonSerializerOptions { WriteIndented = true }));

sealed class ProcessBoundary(string root, string marker, string command, RestoreCheckpoint restorePoint, RestoreRecoveryCheckpoint recoveryPoint, string? boundaryItem) : IRestoreObserver, IRestoreRecoveryObserver
{
    public Task ReachedAsync(RestoreCheckpoint checkpoint, string? item, CancellationToken ct) =>
        command == "restore-barrier" && checkpoint == restorePoint && item == boundaryItem ? WaitAsync(checkpoint.ToString(), item) : Task.CompletedTask;
    public Task ReachedAsync(RestoreRecoveryCheckpoint checkpoint, string? item, CancellationToken ct) =>
        command == "recover-barrier" && checkpoint == recoveryPoint && item == boundaryItem ? WaitAsync(checkpoint.ToString(), item) : Task.CompletedTask;
    private async Task WaitAsync(string checkpoint, string? item)
    {
        var journal = Path.Combine(root, "recovery", "restore.pending.json");
        var json = JsonSerializer.Serialize(new { processId = Environment.ProcessId, command, checkpoint, item, root, journalHash = await Workspace.HashFileAsync(journal) });
        // The parent must observe this exact live child and durable journal before killing it.
        using (var file = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { await file.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json)); file.Flush(true); }
        await Task.Delay(TimeSpan.FromSeconds(120));
        throw new TimeoutException("Fixture boundary was not terminated within its two-minute safety limit.");
    }
}
