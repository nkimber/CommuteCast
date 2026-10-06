using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Net.Http.Json;
using System.Text.Json;
using System.Reflection;

internal static class StoppedSpeechAcceptance
{
    internal static async Task RunAsync(string root, Workspace workspace, LocalSpeechProvider provider, ProviderInfo info, CancellationToken ct)
    {
        if (info.Active != 0 || info.InstanceId is null || info.AdmissionSequence is null || info.ImageId is null)
            throw new IOException("A ready idle reservation-capable owned service is required.");
        var runtime = new LocalSpeechRuntime();
        var inspected = await runtime.DockerAsync(["inspect", DockerContainerPolicy.Name(info.Engine)], TimeSpan.FromSeconds(10), ct);
        if (inspected.ExitCode != 0 || !DockerContainerPolicy.Evaluate(inspected.Output, info.Engine, info.ImageId)) throw new IOException("Owned service verification failed.");
        using var container = JsonDocument.Parse(inspected.Output); var id = container.RootElement[0].GetProperty("Id").GetString()!;
        if (id.Length != 64 || !id.All(Uri.IsHexDigit)) throw new IOException("The container's immutable identity is invalid.");
        var sequence = info.AdmissionSequence.Value + 1;
        var journal = Path.Combine(workspace.Root, "speech-admission.json");
        await Workspace.AtomicWriteAsync(journal, JsonSerializer.Serialize(new { Version = 1, info.Engine, Image = info.ImageId, info.Fingerprint, Instance = info.InstanceId, Sequence = sequence }));
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var endpoint = "http://127.0.0.1:" + (info.Engine == "kokoro" ? "8765" : "8766");
        using var reserved = await http.PostAsJsonAsync(endpoint + "/reserve", new { instance = info.InstanceId, sequence, fingerprint = info.Fingerprint }, ct);
        reserved.EnsureSuccessStatusCode();
        var before = await provider.ProbeAsync(info.Engine, ct);
        if (before.Active != 0 || before.InstanceId != info.InstanceId || before.AdmissionSequence != sequence) throw new IOException("The service changed or became active; controlled stop was refused.");
        // No source/speech is submitted: stop only the verified idle process by
        // immutable container ID, leaving every unrelated container untouched.
        var stopped = await runtime.DockerAsync(["stop", "--time", "5", id], TimeSpan.FromSeconds(15), ct);
        if (stopped.ExitCode != 0) throw new IOException("The controlled owned idle stop failed; evidence was preserved.");
        var proof = await runtime.DockerAsync(["inspect", id], TimeSpan.FromSeconds(10), ct);
        if (proof.ExitCode != 0 || DockerContainerPolicy.Evaluate(proof.Output, info.Engine, info.ImageId)) throw new IOException("The owned process did not stop.");
        if (!File.Exists(journal)) throw new IOException("The saved reservation disappeared before recovery.");
        var recovered = await provider.ReadyAsync(info.Engine, ct, true);
        if (File.Exists(journal) || recovered.Active != 0 || recovered.State != "ready" || recovered.InstanceId == info.InstanceId || recovered.Fingerprint != info.Fingerprint || recovered.ImageId != info.ImageId)
            throw new IOException("Stopped-service reservation recovery failed.");
        using var gate = new SemaphoreSlim(1); var generator = new AuditionGenerator(workspace, provider, gate);
        var settings = new NarrationSettings(info.Engine, info.Engine == "kokoro" ? "af_heart" : "en_US-lessac-medium", 1, false, "", info.Fingerprint, ProviderImageId: info.ImageId);
        var sample = await generator.GenerateAsync(AuditionRequest.Standard(settings), ct);
        if (!await generator.RemoveAsync(sample) || gate.CurrentCount != 1 || File.Exists(journal)) throw new IOException("Post-recovery sample or cleanup failed.");
        var report = new { engine = info.Engine, applicationBuild = typeof(Job).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            info.ImageId, info.Fingerprint, containerId = id, originalInstance = info.InstanceId, recoveredInstance = recovered.InstanceId,
            stoppedIdleReservation = true, savedFenceSurvivedStop = true, readinessSettledAndRestartedOwnedService = true, unchangedModelAndImage = true,
            subsequentSynthesisAndCleanup = true, sourceSubmittedBeforeStop = false, scope = "Real idle reserved-container stop and recovery; active service kill, host loss, sleep/wake and native UI not tested." };
        await File.WriteAllTextAsync(Path.Combine(root, "stopped-recovery-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
