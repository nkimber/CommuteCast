using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Text.Json;
using System.Reflection;
using System.Diagnostics;

internal static class ActiveSpeechLossAcceptance
{
    internal static async Task RunAsync(string root, Workspace workspace, LocalSpeechProvider provider, ProviderInfo info, CancellationToken ct)
    {
        foreach (var name in new[] { "CommuteCast.Desktop", "CommuteCast.Pilot" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process)
                    if (process.Id != Environment.ProcessId) throw new IOException("Close other CommuteCast clients before this isolated service-loss acceptance test.");
        if (info.Active != 0 || info.InstanceId is null || info.ImageId is null) throw new IOException("An idle verified owned service is required.");
        var runtime = new LocalSpeechRuntime();
        var metadata = await runtime.DockerAsync(["inspect", DockerContainerPolicy.Name(info.Engine)], TimeSpan.FromSeconds(10), ct);
        if (metadata.ExitCode != 0 || !DockerContainerPolicy.Evaluate(metadata.Output, info.Engine, info.ImageId)) throw new IOException("Owned container verification failed.");
        using var document = JsonDocument.Parse(metadata.Output); var id = document.RootElement[0].GetProperty("Id").GetString()!;
        if (id.Length != 64 || !id.All(Uri.IsHexDigit)) throw new IOException("The immutable container identity is invalid.");
        using var gate = new SemaphoreSlim(1); var generator = new AuditionGenerator(workspace, provider, gate);
        var settings = new NarrationSettings(info.Engine, info.Engine == "kokoro" ? "af_heart" : "en_US-lessac-medium", 1, false, "", info.Fingerprint, ProviderImageId: info.ImageId);
        var prior = await generator.GenerateAsync(AuditionRequest.Standard(settings), ct);
        var path = OwnedFileRemoval.Resolve(workspace.Root, prior.RelativePath);
        var source = string.Concat(Enumerable.Repeat("Preserve every paragraph in its original order. A reliable narration keeps technical details and survives interruptions. ", 7));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var request = provider.SynthesizeAsync(settings, source, path, stop.Token);
        var killed = false;
        string? failure = null;
        ProviderInfo? recovered = null;
        try
        {
            using var observation = CancellationTokenSource.CreateLinkedTokenSource(ct); observation.CancelAfter(TimeSpan.FromSeconds(120));
            ProviderInfo active;
            while (true)
            {
                active = await provider.ProbeAsync(info.Engine, observation.Token);
                if (active.Active == 1) break;
                if (request.IsCompleted) { await request; throw new IOException("Inference ended before controlled loss could be observed."); }
                await Task.Delay(30, observation.Token);
            }
            if (active.InstanceId != info.InstanceId || !File.Exists(Path.Combine(workspace.Root, "speech-admission.json"))) throw new IOException("The active request lacks its saved process fence.");
            var rechecked = await runtime.DockerAsync(["inspect", id], TimeSpan.FromSeconds(10), ct);
            if (rechecked.ExitCode != 0 || !DockerContainerPolicy.Evaluate(rechecked.Output, info.Engine, info.ImageId)) throw new IOException("The active owned container changed; termination was refused.");
            active = await provider.ProbeAsync(info.Engine, ct);
            if (active.Active != 1 || active.InstanceId != info.InstanceId) throw new IOException("The exact process is no longer actively synthesizing; termination was refused.");
            var termination = await runtime.DockerAsync(["kill", id], TimeSpan.FromSeconds(15), ct);
            if (termination.ExitCode != 0) throw new IOException("Controlled owned-service termination failed.");
            killed = true;
            var stopped = await runtime.DockerAsync(["inspect", id], TimeSpan.FromSeconds(10), ct);
            if (stopped.ExitCode != 0 || DockerContainerPolicy.Evaluate(stopped.Output, info.Engine, info.ImageId)) throw new IOException("Owned process loss was not established.");
            try { await request; throw new InvalidOperationException("A killed inference returned completed audio."); }
            catch (Exception error) when (error is IOException or HttpRequestException or TimeoutException) { failure = error.GetType().Name; }
            if (await Workspace.HashFileAsync(path, ct) != prior.Hash) throw new IOException("Failed inference replaced prior completed audio.");
            recovered = await provider.ReadyAsync(info.Engine, ct, true);
            if (recovered.InstanceId == info.InstanceId || recovered.Active != 0 || recovered.Fingerprint != info.Fingerprint || recovered.ImageId != info.ImageId || File.Exists(Path.Combine(workspace.Root, "speech-admission.json")))
                throw new IOException("Active-loss recovery did not retain identity and settle the fence.");
            await provider.SynthesizeAsync(settings, "A fresh request succeeds after recovery.", path, ct);
            WaveAudio.DataRegion(path, false); var hash = await Workspace.HashFileAsync(path, ct);
            if (hash == prior.Hash || !await OwnedFileRemoval.DeleteByHashAsync(workspace.Root, prior.RelativePath, hash)) throw new IOException("Post-recovery synthesis or owned cleanup failed.");
            if (File.Exists(Path.Combine(workspace.Root, "queue.db")) || Directory.GetFiles(Path.Combine(workspace.Root, "auditions")).Length != 0 || Directory.GetFiles(Path.Combine(root, "output")).Length != 0)
                throw new IOException("The acceptance fixture left unexpected history, exports or attempt output.");
            var report = new { engine = info.Engine, applicationBuild = typeof(Job).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                info.ImageId, info.Fingerprint, containerId = id, originalInstance = info.InstanceId, recoveredInstance = recovered.InstanceId,
                sourceCharacters = source.Length, activeInferenceObserved = true, fenceObservedBeforeTermination = true, killedExactOwnedProcess = true,
                failure, previousAudioPreserved = true, recoveredSameImageAndModel = true, subsequentSynthesisAndCleanup = true,
                scope = "Real active speech-service process termination and direct adapter recovery. Queue/desktop host termination, resumed chunk manifests, sleep/wake, native UI and full MP3 recovery remain separate." };
            await File.WriteAllTextAsync(Path.Combine(root, "active-loss-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            stop.Cancel();
            try { await request; } catch (Exception error) when (error is IOException or HttpRequestException or TimeoutException or OperationCanceledException) { }
            if (killed && recovered is null)
                using (var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(120))) await provider.ReadyAsync(info.Engine, recovery.Token, true);
        }
    }
}
