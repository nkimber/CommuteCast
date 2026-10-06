using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

internal static class SpeechCancellationAcceptance
{
    internal static async Task RunAsync(string root, Workspace workspace, LocalSpeechProvider provider, NarrationSettings settings, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(1);
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(3) };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var followingStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var generator = new AuditionGenerator(workspace, provider, gate, (engine, token) => provider.ReadyAsync(engine, token, true));
        var otherEngine = settings.Engine == "kokoro" ? "piper" : "kokoro";
        var otherSettings = settings with { Engine = otherEngine, Voice = otherEngine == "kokoro" ? "af_heart" : "en_US-lessac-medium", Profile = null, Pronunciation = "" };
        const string sentence = "Preserve every paragraph in its original order. A reliable narration keeps technical details and survives interruptions. ";
        var source = string.Concat(Enumerable.Repeat(sentence, 7));
        var initial = await HealthAsync(http, settings.Engine, ct);
        var otherInitial = await HealthAsync(http, otherEngine, ct);
        if (initial.Active != 0 || otherInitial.Active != 0) throw new IOException("Both owned services must be idle before controlled cancellation acceptance.");
        var generation = generator.GenerateAsync(AuditionRequest.Selection(source, 0, source.Length, settings with { Profile = null, Pronunciation = "" }), stop.Token);
        var watch = Stopwatch.StartNew();
        var observations = new List<object>();
        Task<AuditionAudio>? following = null;
        AuditionAudio? completed = null;
        string? firstFollowingError = null;
        var fenceRetained = false;
        var cancellationSettled = false;
        var samples = 0;
        try
        {
            using var activeDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct); activeDeadline.CancelAfter(TimeSpan.FromSeconds(120));
            while (true)
            {
                var first = await HealthAsync(http, settings.Engine, activeDeadline.Token);
                if (first.Active == 1) break;
                if (generation.IsCompleted) { await generation; throw new IOException("The request finished before active inference could be observed; cancellation was not tested."); }
                await Task.Delay(30, activeDeadline.Token);
            }
            var beforeStop = await HealthAsync(http, settings.Engine, ct);
            if (beforeStop.Active != 1) throw new IOException("Inference finished before cancellation; this run does not establish active-inference cancellation.");
            stop.Cancel();
            following = generator.GenerateAsync(AuditionRequest.Standard(otherSettings), followingStop.Token);
            observations.Add(new { stage = "cancel requested during observed inference", engine = settings.Engine, beforeStop.Instance, beforeStop.Sequence, active = beforeStop.Active });
            using var settlementDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct); settlementDeadline.CancelAfter(TimeSpan.FromSeconds(180));
            while (!generation.IsCompleted || !following.IsCompleted)
            {
                var first = await HealthAsync(http, settings.Engine, settlementDeadline.Token);
                var other = await HealthAsync(http, otherEngine, settlementDeadline.Token);
                samples++;
                if (first.Instance != initial.Instance || other.Instance != otherInitial.Instance) throw new IOException("A service restarted during cancellation acceptance.");
                if (first.Active == 1 && other.Active == 1 && (await HealthAsync(http, settings.Engine, settlementDeadline.Token)).Active == 1)
                    throw new IOException("The engines overlapped while cancellation was unsettled.");
                if (first.Active == 1 && generation.IsCompleted)
                {
                    fenceRetained = File.Exists(Path.Combine(workspace.Root, "speech-admission.json"));
                    if (!fenceRetained) throw new IOException("The cancelled request lost its durable fence before real inference settled.");
                }
                await Task.Delay(60, settlementDeadline.Token);
            }
            try { completed = await generation; throw new IOException("The controlled cancellation returned playable audio."); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { cancellationSettled = true; }
            try { completed = await following; }
            catch (TimeoutException error)
            {
                firstFollowingError = error.Message;
                // A bounded wait is allowed to report pending settlement. Wait for
                // this exact owned process, then explicitly retry the blocked sample.
                while ((await HealthAsync(http, settings.Engine, settlementDeadline.Token)).Active != 0)
                    await Task.Delay(100, settlementDeadline.Token);
                completed = await generator.GenerateAsync(AuditionRequest.Standard(otherSettings), settlementDeadline.Token);
            }
            var final = await HealthAsync(http, settings.Engine, ct); var otherFinal = await HealthAsync(http, otherEngine, ct);
            if (!cancellationSettled || completed.Settings.Engine != otherEngine || gate.CurrentCount != 1 || final.Active != 0 || otherFinal.Active != 0 ||
                final.Instance != initial.Instance || otherFinal.Instance != otherInitial.Instance || File.Exists(Path.Combine(workspace.Root, "speech-admission.json")))
                throw new IOException("Real cancellation or subsequent engine admission failed its final checks.");
            if (!await generator.RemoveAsync(completed)) throw new IOException("The completed subsequent sample did not pass owned removal.");
            completed = null;
            if (Directory.GetFiles(Path.Combine(workspace.Root, "auditions")).Length != 0 || File.Exists(Path.Combine(workspace.Root, "queue.db")) || Directory.GetFiles(Path.Combine(root, "output")).Length != 0)
                throw new IOException("Cancellation left playable/private attempt output, history or exports.");
            watch.Stop();
            var report = new { applicationBuild = typeof(Job).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                cancelledEngine = settings.Engine, subsequentEngine = otherEngine, sourceCharacters = source.Length, initial, otherInitial, final, otherFinal,
                observations, healthSamplePairs = samples, activeInferenceObservedBeforeCancellation = true, cancellationReturnedNoPlayableAudio = true,
                pendingFenceObservedAfterClientCompletion = fenceRetained, firstFollowingError, noObservedCrossEngineOverlap = true,
                unchangedServiceProcesses = true, subsequentEngineGenerationAndCleanup = true, noQueueOrExport = true, wallSeconds = watch.Elapsed.TotalSeconds,
                scope = "Real active-inference HTTP cancellation, shared audition scheduler and opposite-engine admission. Health samples supplement the server reservation fence; they do not prove unobserved UI/media-lock, process-kill, sleep/wake or resource acceptance." };
            await File.WriteAllTextAsync(Path.Combine(root, "cancellation-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            stop.Cancel();
            followingStop.Cancel();
            try { var late = await generation; await generator.RemoveAsync(late); } catch (OperationCanceledException) { }
            if (following is not null)
                try { var late = await following; await generator.RemoveAsync(late); }
                catch (Exception error) when (error is OperationCanceledException or TimeoutException or IOException) { }
            if (completed is not null) await generator.RemoveAsync(completed);
        }
    }
    private sealed record ServiceState(string Engine, string Instance, long Sequence, string Fingerprint, int Active, string State);
    private static async Task<ServiceState> HealthAsync(HttpClient http, string engine, CancellationToken ct)
    {
        using var response = await http.GetAsync("http://127.0.0.1:" + (engine == "kokoro" ? "8765" : "8766") + "/health", ct); response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); var data = document.RootElement;
        if (data.GetProperty("service").GetString() != "CommuteCast" || data.GetProperty("contract").GetInt32() != 1 || data.GetProperty("engine").GetString() != engine || data.GetProperty("admission").GetInt32() != 1)
            throw new IOException("Unexpected local speech contract during acceptance.");
        return new(engine, data.GetProperty("instance").GetString()!, data.GetProperty("sequence").GetInt64(), data.GetProperty("fingerprint").GetString()!, data.GetProperty("active").GetInt32(), data.GetProperty("state").GetString()!);
    }
}
