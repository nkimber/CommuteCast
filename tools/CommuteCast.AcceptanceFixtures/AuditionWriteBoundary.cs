using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Reflection;
using System.Text.Json;

// Actual production preview paths; synthetic in-process service observations only.
internal static class AuditionWriteBoundary
{
    internal static async Task RunAsync(string[] args, Workspace workspace, string marker)
    {
        var engine = args[2]; var ledger = new AuditionOwnershipStore(workspace);
        var settings = new NarrationSettings(engine, engine == "kokoro" ? "af_heart" : "en_US-lessac-medium", 1, false, "", engine + ":contract-v1:" + new string('c', 64), ProviderImageId: SpeechWriteBoundary.Image);
        var build = typeof(Job).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        using var pcm = new MemoryStream(); WaveAudio.WriteHeader(pcm, 48000);
        using (var writer = new BinaryWriter(pcm, System.Text.Encoding.UTF8, true))
            for (var i = 0; i < 48000; i++) writer.Write((short)(Math.Sin(i * 2 * Math.PI * 220 / 24000) * 8000));
        var wave = pcm.ToArray(); var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(wave));
        var http = new SpeechWriteBoundary.SyntheticHttp(settings, wave);
        using var provider = new LocalSpeechProvider(workspace, new SpeechWriteBoundary.SyntheticRuntime(engine), http);
        using var gate = new SemaphoreSlim(1);
        var directory = Path.Combine(workspace.Root, "auditions");
        const string selected = "Private selected fixture text stays absent from persisted preview metadata.";
        if (args[0] == "audition-write-barrier")
        {
            if ((await ledger.LoadAsync()).Count != 0 || File.Exists(marker) || Directory.Exists(marker)) throw new IOException("Use a fresh preview fixture.");
            await using (var pin = new FileStream(Path.Combine(workspace.Root, "provider-lock.local.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(pin, new { ImageId = SpeechWriteBoundary.Image, Contract = 1 });
            Directory.CreateDirectory(directory); await File.WriteAllTextAsync(Path.Combine(directory, "unknown.wav"), "Preserve unrelated preview bytes.");
            async Task BarrierAsync(string point)
            {
                if (point != args[3]) return;
                var saved = (await ledger.LoadAsync()).Single();
                var existing = saved.Artifacts.Select(r => r.RelativePath).Where(name => File.Exists(Path.Combine(directory, name))).ToArray();
                if (existing.Length != 1 || JsonSerializer.Serialize(saved).Contains(selected, StringComparison.Ordinal) || (await new SqliteJobStore(workspace).LoadAsync()).Count != 0)
                    throw new IOException("The preview boundary has incorrect ownership or source/history disclosure.");
                await Workspace.AtomicWriteAsync(marker, JsonSerializer.Serialize(new { processId = Environment.ProcessId, applicationBuild = build, point, engine, saved.Id, expectedHash = hash, surviving = existing[0], observedBytes = new FileInfo(Path.Combine(directory, existing[0])).Length, reservationPresent = File.Exists(Path.Combine(workspace.Root, "speech-admission.json")) }));
                await Task.Delay(Timeout.InfiniteTimeSpan);
            }
            http.Body = () => new SpeechWriteBoundary.BoundaryBody(wave, () => BarrierAsync("during-copy"));
            var intercepted = new BoundaryProvider(provider, BarrierAsync);
            var generator = new AuditionGenerator(workspace, intercepted, gate);
            await generator.GenerateAsync(AuditionRequest.Selection(selected, 0, selected.Length, settings), default);
            throw new IOException("The preview boundary was not reached.");
        }
        using var boundary = JsonDocument.Parse(await File.ReadAllTextAsync(marker));
        var pointName = boundary.RootElement.GetProperty("point").GetString(); var journal = (await ledger.LoadAsync()).Single();
        if (pointName is not ("before-copy" or "during-copy" or "before-rename" or "after-rename" or "after-complete-save") ||
            boundary.RootElement.GetProperty("engine").GetString() != engine || boundary.RootElement.GetProperty("Id").GetString() != journal.Id ||
            boundary.RootElement.GetProperty("expectedHash").GetString() != hash || boundary.RootElement.GetProperty("applicationBuild").GetString() != build)
            throw new IOException("The preview boundary does not identify this build and fixture.");
        var surviving = boundary.RootElement.GetProperty("surviving").GetString()!;
        if (!journal.Artifacts.Any(r => r.RelativePath == surviving)) throw new IOException("Observed preview has no durable receipt.");
        var incomplete = pointName is "before-copy" or "during-copy";
        if (incomplete != journal.Artifacts.Any(r => r.CreationIdentity is not null)) throw new IOException("Preview creation state disagrees with the boundary.");
        if (!incomplete && await Workspace.HashFileAsync(OwnedFileRemoval.Resolve(directory, surviving)) != hash) throw new IOException("Completed preview checksum changed.");
        var recovery = new AuditionGenerator(workspace, provider, gate);
        if (await recovery.RecoverAsync() != 1 || await recovery.RecoverAsync() != 0 || (await ledger.LoadAsync()).Count != 0 || File.Exists(OwnedFileRemoval.Resolve(directory, surviving)))
            throw new IOException("Preview startup reconciliation did not remove its original file and record.");
        var retry = await recovery.GenerateAsync(AuditionRequest.Selection(selected, 0, selected.Length, settings), default);
        if (retry.Hash != hash || retry.OwnershipId is null || !await recovery.RemoveAsync(retry) || (await ledger.LoadAsync()).Count != 0 ||
            File.Exists(Path.Combine(workspace.Root, "speech-admission.json")) || (await new SqliteJobStore(workspace).LoadAsync()).Count != 0 ||
            await File.ReadAllTextAsync(Path.Combine(directory, "unknown.wav")) != "Preserve unrelated preview bytes." ||
            Directory.EnumerateFiles(directory).Count() != 1) throw new IOException("Preview retry, source-free history, reservation or unrelated-file checks failed.");
        Console.WriteLine(JsonSerializer.Serialize(new { journal.Id, applicationBuild = build, point = pointName, engine, expectedHash = hash, exactRecovery = true, durableRetry = true, unrelatedPreserved = true, sourceFreeHistory = true, reservationSettled = true }));
    }
    private sealed class BoundaryProvider(LocalSpeechProvider provider, Func<string, Task> barrier) : IDurableAuditionSpeechProvider
    {
        public Task<ProviderInfo> ReadyAsync(string engine, CancellationToken ct) => provider.ReadyAsync(engine, ct);
        public Task SynthesizeAsync(NarrationSettings settings, string text, string output, CancellationToken ct) => throw new IOException("Preview must use its durable route.");
        public Task SynthesizeAuditionAsync(AuditionWriteJournal journal, NarrationSettings settings, string text, string output, Func<Task> checkpoint, CancellationToken ct)
        {
            var saves = 0;
            return provider.SynthesizeAuditionAsync(journal, settings, text, output, async () =>
            {
                saves++; if (saves == 3) await barrier("after-rename");
                await checkpoint(); await barrier(saves switch { 1 => "before-copy", 2 => "before-rename", _ => "after-complete-save" });
            }, ct);
        }
    }
}
