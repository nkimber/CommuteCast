using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

internal static class AudioWriteBoundary
{
    internal static async Task RunAsync(string[] args, Workspace workspace, string marker)
    {
        var store = new SqliteJobStore(workspace); var name = args[2] == "wav" ? "normalized.partial.wav" : "encoded.partial.mp3";
        var build = typeof(Job).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (args[0] == "audio-write-barrier")
        {
            if ((await store.LoadAsync()).Count != 0 || File.Exists(marker)) throw new IOException("Use a fresh audio write fixture.");
            var job = new Job { Title = "Synthetic held audio process", Source = "Preserve frozen source.", Stage = JobStage.Failed };
            var directory = workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "unknown.txt"), "Preserve unrelated bytes.");
            await store.SaveAsync(job); int childId = 0;
            var operation = PrivateJobFiles.WriteRecordedAsync(job, directory, name, async stream =>
            {
                var codec = args[2] == "wav" ? "pcm_s16le" : "libmp3lame";
                var result = await ProcessRunner.RunToFileAsync("ffmpeg", ["-v", "error", "-nostdin", "-re", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=24000:duration=60", "-ac", "1", "-c:a", codec, "-f", args[2], "-fd", "1", "fd:"], (FileStream)stream, 8 * 1024 * 1024, TimeSpan.FromSeconds(90), processStarted: id => childId = id);
                if (result.ExitCode != 0) throw new IOException("Synthetic encoder failed.");
            }, () => store.SaveAsync(job), default);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            var path = Path.Combine(directory, name);
            while (childId == 0 || new FileInfo(path).Length < 8192)
            {
                if (operation.IsCompleted) { await operation; throw new IOException("Encoder finished before the process-loss boundary."); }
                if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("Active audio bytes were not observed.");
                await Task.Delay(50);
            }
            using var child = Process.GetProcessById(childId);
            var saved = (await store.LoadAsync()).Single();
            if (child.HasExited || saved.PrivateArtifacts.Single().CreationIdentity is null) throw new IOException("Active child and durable incomplete receipt are required.");
            await Workspace.AtomicWriteAsync(marker, JsonSerializer.Serialize(new { processId = Environment.ProcessId, childId, childStartUtc = child.StartTime.ToUniversalTime(), applicationBuild = build, format = args[2], job.Id, observedBytes = new FileInfo(path).Length }));
            await operation; throw new IOException("The parent-loss harness did not terminate this host.");
        }
        var recovered = (await store.LoadAsync()).Single(); var root = workspace.JobDirectory(recovered.Id);
        if (recovered.Source != "Preserve frozen source." || recovered.PrivateArtifacts.Single().CreationIdentity is null) throw new IOException("Expected frozen source and incomplete receipt.");
        await PrivateJobFiles.ReconcilePromotionsAsync(recovered, root, () => store.SaveAsync(recovered), default);
        if (File.Exists(Path.Combine(root, name))) throw new IOException("Original incomplete audio survived reconciliation.");
        await PrivateJobFiles.WriteRecordedAsync(recovered, root, name, async stream =>
        {
            var result = await ProcessRunner.RunToFileAsync("ffmpeg", ["-v", "error", "-nostdin", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=24000:duration=1", "-ac", "1", "-c:a", args[2] == "wav" ? "pcm_s16le" : "libmp3lame", "-f", args[2], "-fd", "1", "fd:"], (FileStream)stream, 100000, TimeSpan.FromSeconds(10));
            if (result.ExitCode != 0) throw new IOException("Recovered encoding failed.");
        }, () => store.SaveAsync(recovered), default);
        if (await Workspace.HashFileAsync(Path.Combine(root, name)) != PrivateJobFiles.Inventory((await store.LoadAsync()).Single())[name] || await File.ReadAllTextAsync(Path.Combine(root, "unknown.txt")) != "Preserve unrelated bytes.") throw new IOException("Durable retry or unrelated preservation failed.");
        Console.WriteLine(JsonSerializer.Serialize(new { applicationBuild = build, recovered.Id, format = args[2], exactRecovery = true, durableRetry = true, unrelatedPreserved = true }));
    }
}
