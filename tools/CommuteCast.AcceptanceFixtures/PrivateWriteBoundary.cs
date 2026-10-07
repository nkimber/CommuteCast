using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Reflection;
using System.Text.Json;

internal static class PrivateWriteBoundary
{
    private static string? Build => typeof(Job).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    internal static async Task RunAsync(string[] args, Workspace workspace, string marker)
    {
        var store = new SqliteJobStore(workspace);
        if (args[0] == "write-barrier")
        {
            if ((await store.LoadAsync()).Count != 0 || File.Exists(marker) || Directory.Exists(marker)) throw new IOException("Use a fresh private write fixture.");
            var job = new Job { Title = "Synthetic interrupted assembly", Source = "Preserve this source.", Stage = JobStage.Failed };
            var directory = workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "unknown.txt"), "Preserve unrelated bytes.");
            await store.SaveAsync(job);
            async Task BarrierAsync(string point)
            {
                if (args[2] != point) return;
                await Workspace.AtomicWriteAsync(marker, JsonSerializer.Serialize(new { processId = Environment.ProcessId, applicationBuild = Build, point, job.Id }));
                await Task.Delay(Timeout.InfiniteTimeSpan);
            }
            var saves = 0;
            await PrivateJobFiles.WriteRecordedAsync(job, directory, "assembled.wav", async output =>
            {
                await BarrierAsync("before-write");
                await output.WriteAsync(new byte[] { 1, 2, 3 }); output.Flush();
                await BarrierAsync("during-write");
                await output.WriteAsync(new byte[] { 4, 5, 6 });
            }, async () =>
            {
                if (++saves == 2) await BarrierAsync("before-complete-save");
                await store.SaveAsync(job);
                if (saves == 2) await BarrierAsync("after-complete-save");
            }, default);
            throw new IOException("Requested write boundary was not reached.");
        }
        var saved = (await store.LoadAsync()).Single();
        if (saved.Title != "Synthetic interrupted assembly" || saved.Source != "Preserve this source.") throw new IOException("Frozen source changed.");
        var root = workspace.JobDirectory(saved.Id);
        var incomplete = saved.PrivateArtifacts.Single().CreationIdentity is not null;
        await PrivateJobFiles.ReconcilePromotionsAsync(saved, root, () => store.SaveAsync(saved), default);
        var outputPath = Path.Combine(root, "assembled.wav");
        if (incomplete && File.Exists(outputPath)) throw new IOException("Original incomplete creation survived recovery.");
        if (!incomplete && await Workspace.HashFileAsync(outputPath) != PrivateJobFiles.Inventory(saved)["assembled.wav"]) throw new IOException("Completed receipt changed.");
        await PrivateJobFiles.WriteRecordedAsync(saved, root, "assembled.wav", s => s.WriteAsync(new byte[] { 7, 8, 9 }).AsTask(), () => store.SaveAsync(saved), default);
        if (!(await File.ReadAllBytesAsync(outputPath)).SequenceEqual(new byte[] { 7, 8, 9 }) ||
            await Workspace.HashFileAsync(outputPath) != PrivateJobFiles.Inventory((await store.LoadAsync()).Single())["assembled.wav"] ||
            await File.ReadAllTextAsync(Path.Combine(root, "unknown.txt")) != "Preserve unrelated bytes.") throw new IOException("Recovered retry or unrelated preservation failed.");
        Console.WriteLine(JsonSerializer.Serialize(new { applicationBuild = Build, saved.Id, incomplete, exactRecovery = true, durableRetry = true, unrelatedPreserved = true, schema = await SqliteSchema.ValidateDatabaseAsync(Path.Combine(workspace.Root, "queue.db")) }));
    }
}
