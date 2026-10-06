using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Text.Json;
using System.Reflection;

internal static class PrivatePromotionBoundary
{
    internal static async Task RunAsync(string[] args, Workspace workspace, string marker)
    {
        var store = new SqliteJobStore(workspace);
        if (args[0] == "promotion-barrier")
        {
            if ((await store.LoadAsync()).Count != 0 || File.Exists(marker) || Directory.Exists(marker)) throw new IOException("Use a fresh promotion fixture.");
            var job = new Job { Title = "Synthetic private promotion", Source = "Preserve this frozen source.", Prepared = TextPreparation.Prepare("Preserve this frozen source."), Stage = JobStage.Failed };
            var directory = workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
            var source = args[2] == "chunk" ? "normalized.partial.wav" : "encoded.partial.mp3";
            var target = args[2] == "chunk" ? "chunk-00000.wav" : "complete.mp3";
            // Synthetic bytes test publication ownership, not voice or audio format.
            await File.WriteAllTextAsync(Path.Combine(directory, source), "Complete synthetic private audio bytes.");
            await PrivateJobFiles.RecordAsync(job, directory, source, default); await store.SaveAsync(job);
            var calls = 0;
            await PrivateJobFiles.MoveRecordedAsync(job, directory, source, target, async () =>
            {
                calls++;
                if (calls == 1) await store.SaveAsync(job); // destination receipt is durable before rename
                if (calls == (args[3] == "before" ? 1 : 2))
                {
                    await Workspace.AtomicWriteAsync(marker, JsonSerializer.Serialize(new { processId = Environment.ProcessId, applicationBuild = Build, kind = args[2], point = args[3], job.Id, source, target, expectedHash = Job.Hash("Complete synthetic private audio bytes.") }));
                    await Task.Delay(Timeout.InfiniteTimeSpan); // only the parent-owned child is terminated
                }
                if (calls == 2) await store.SaveAsync(job);
            }, default);
            throw new IOException("The requested promotion boundary was not reached.");
        }
        var saved = AssertSingle(await store.LoadAsync()); var inventory = PrivateJobFiles.Inventory(saved);
        if (saved.Title != "Synthetic private promotion" || saved.Source != "Preserve this frozen source." || inventory.Count != 2 ||
            inventory.Values.Any(hash => hash != Job.Hash("Complete synthetic private audio bytes."))) throw new IOException("The durable promotion fixture changed.");
        var root = workspace.JobDirectory(saved.Id); var files = Directory.GetFiles(root);
        if (files.Length != 1 || await Workspace.HashFileAsync(files[0]) != inventory[Path.GetFileName(files[0])]) throw new IOException("The surviving promotion bytes do not match the pre-rename receipt.");
        var surviving = Path.GetFileName(files[0]);
        await PrivateJobFiles.ReconcilePromotionsAsync(saved, root, () => store.SaveAsync(saved), default);
        if (saved.PrivateArtifacts.Any(r => r.PromotionIdentity is not null)) throw new IOException("Promotion intent did not reconcile.");
        await PrivateJobFiles.RemoveAsync(saved, root);
        if (Directory.Exists(root)) throw new IOException("Owned promotion recovery did not finish.");
        Console.WriteLine(JsonSerializer.Serialize(new { saved.Id, applicationBuild = Build, surviving, expectedHash = Job.Hash("Complete synthetic private audio bytes."), durableReceiptMatched = true, exactRemoval = true, scope = "Synthetic completed private promotion at actual owned child termination; incomplete writes and real narration remain separate." }));
    }
    private static string? Build => typeof(Job).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    private static Job AssertSingle(IReadOnlyList<Job> jobs) => jobs.Count == 1 ? jobs[0] : throw new IOException("Expected one isolated promotion job.");
}
