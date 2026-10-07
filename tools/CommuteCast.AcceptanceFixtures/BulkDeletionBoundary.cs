using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Reflection;
using System.Text.Json;

internal static class BulkDeletionBoundary
{
    private sealed record Plan(string[] Selected, string Unselected, string UnselectedJson, string ArtifactHash);
    internal static async Task RunAsync(string[] args, Workspace workspace, string marker)
    {
        var durable = new SqliteJobStore(workspace);
        var planPath = Path.Combine(Path.GetDirectoryName(workspace.Root)!, "bulk-plan.json");
        if (args[0] == "bulk-delete-barrier")
        {
            if ((await durable.LoadAsync()).Count != 0 || File.Exists(marker) || File.Exists(planPath)) throw new IOException("Use a fresh bulk deletion fixture.");
            var jobs = new List<Job>();
            for (var index = 0; index < 4; index++)
            {
                var job = new Job { Title = "Synthetic bulk " + index, Source = "Preserve unselected narration.", Stage = JobStage.Failed };
                var directory = workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory, "source.json"), "Synthetic owned source " + index);
                await PrivateJobFiles.RecordAsync(job, directory, "source.json", default); await durable.SaveAsync(job); jobs.Add(job);
            }
            await File.WriteAllTextAsync(Path.Combine(workspace.Root, "unrelated.txt"), "Preserve unrelated root bytes.");
            var plan = new Plan(jobs.Take(3).Select(j => j.Id).ToArray(), jobs[3].Id, JsonSerializer.Serialize(jobs[3]),
                await Workspace.HashFileAsync(Path.Combine(workspace.JobDirectory(jobs[3].Id), "source.json")));
            await Workspace.AtomicWriteAsync(planPath, JsonSerializer.Serialize(plan));
            async Task HoldAsync(string point)
            {
                if (point != args[2]) return;
                var remaining = (await durable.LoadAsync()).Where(j => plan.Selected.Contains(j.Id)).ToArray();
                if (remaining.Any(j => !j.DeletionRequested || j.DeleteExportRequested)) throw new IOException("All remaining selected intents must be durable with private-only scope.");
                await Workspace.AtomicWriteAsync(marker, JsonSerializer.Serialize(new
                {
                    processId = Environment.ProcessId, point, remainingSelected = remaining.Length,
                    applicationBuild = typeof(QueueCoordinator).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                }));
                await Task.Delay(Timeout.InfiniteTimeSpan);
            }
            var observed = new ObservedStore(durable, HoldAsync);
            await using var queue = Queue(workspace, observed); queue.Paused = true; await queue.InitializeAsync();
            await queue.DeleteManyAsync(plan.Selected, false);
            throw new IOException("Bulk deletion did not reach the selected boundary.");
        }
        var saved = JsonSerializer.Deserialize<Plan>(await File.ReadAllTextAsync(planPath)) ?? throw new IOException("Missing bulk fixture plan.");
        await using (var queue = Queue(workspace, durable)) { queue.Paused = true; await queue.InitializeAsync(); }
        var retained = (await durable.LoadAsync()).Single();
        if (retained.Id != saved.Unselected || JsonSerializer.Serialize(retained) != saved.UnselectedJson ||
            await Workspace.HashFileAsync(Path.Combine(workspace.JobDirectory(retained.Id), "source.json")) != saved.ArtifactHash ||
            saved.Selected.Any(id => Directory.Exists(workspace.JobDirectory(id))) ||
            await File.ReadAllTextAsync(Path.Combine(workspace.Root, "unrelated.txt")) != "Preserve unrelated root bytes.")
            throw new IOException("Bulk recovery changed unselected state or retained selected files.");
        Console.WriteLine(JsonSerializer.Serialize(new { passed = true, selectedRemoved = 3, unselectedPreserved = true, unrelatedPreserved = true }));
    }
    private static QueueCoordinator Queue(Workspace workspace, IJobStore store) => new(workspace, store, new NeverProvider(), new(new()), new(workspace, store));
    private sealed class NeverProvider : ISpeechProvider
    {
        public Task<ProviderInfo> ReadyAsync(string engine, CancellationToken ct) => throw new IOException("Bulk fixture must not contact speech.");
        public Task SynthesizeAsync(NarrationSettings settings, string text, string output, CancellationToken ct) => throw new IOException("Bulk fixture must not synthesize.");
    }
    private sealed class ObservedStore(IJobStore inner, Func<string, Task> barrier) : IJobStore
    {
        private int removals;
        public Task SaveAsync(Job job, CancellationToken ct = default) => inner.SaveAsync(job, ct);
        public Task SaveQueueOrderAsync(IReadOnlyDictionary<string, long> positions, CancellationToken ct = default) => inner.SaveQueueOrderAsync(positions, ct);
        public Task<IReadOnlyList<Job>> LoadAsync(CancellationToken ct = default) => inner.LoadAsync(ct);
        public async Task<IReadOnlyDictionary<string, bool>> RequestDeletionAsync(IReadOnlyList<string> ids, bool deleteExports, CancellationToken ct = default)
        { var result = await inner.RequestDeletionAsync(ids, deleteExports, ct); await barrier("intent-committed"); return result; }
        public async Task RemoveAsync(string id, CancellationToken ct = default)
        { await inner.RemoveAsync(id, ct); if (++removals == 1) await barrier("first-record-removed"); }
    }
}
