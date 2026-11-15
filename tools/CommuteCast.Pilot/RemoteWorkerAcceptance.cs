using System.Reflection;
using System.Text.Json;
using CommuteCast.Core;
using CommuteCast.Infrastructure;

internal static class RemoteWorkerAcceptance
{
    public static async Task RunAsync(string[] args)
    {
        if (args.Length != 2 || args[1] is not ("kokoro" or "piper"))
            throw new ArgumentException("Use --verify-remote-worker kokoro or --verify-remote-worker piper.");
        var engine = args[1];
        var root = Path.GetFullPath(Path.Combine("artifacts", "pilot", $"remote-{engine}-{Guid.NewGuid():N}"));
        Workspace.RejectReparsePoints(root);
        var workspace = new Workspace(Path.Combine(root, "private"));
        using var lease = WorkspaceLease.Acquire(workspace);
        var pin = Path.Combine(new Workspace(createRoot: false).Root, "provider-lock.local.json");
        Workspace.RejectFileReparsePoint(pin);
        File.Copy(pin, Path.Combine(workspace.Root, "provider-lock.local.json"));
        var shared = Directory.CreateDirectory(Path.Combine(root, "shared")).FullName;
        var folder = new RemoteQueueFolder(shared, workspace);

        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        using var provider = new LocalSpeechProvider(workspace);
        var ct = deadline.Token;
        var info = await provider.ProbeAsync(engine, ct);
        var voice = SpeechVoiceCatalog.DefaultVoice(engine);
        if (info.State != "ready" || info.Active != 0 || !info.Voices.Contains(voice))
            throw new IOException("The installed speech service and pilot voice must be ready and idle before running the pilot.");

        var store = new SqliteJobStore(workspace);
        await using var queue = new QueueCoordinator(workspace, store, provider, new(new()), new(workspace, store));
        await queue.InitializeAsync(ct);
        var workerId = Guid.NewGuid().ToString("N");
        var options = new NarrationOptions(engine, voice, 1, false, "", new(), new());
        var fallback = new NarrationPreset("Verified local fallback", options);
        var planner = new RemoteRenderPlanner((settings, token) => provider.CaptureForSubmissionAsync(settings.Engine, info, token), [fallback]);
        var worker = new RemoteQueueWorker(workerId, "Pilot worker", folder, workspace, queue, planner);
        await worker.CheckInAsync(RemoteWorkerState.Ready, [fallback.Name]);

        var now = DateTimeOffset.UtcNow;
        var requests = new[]
        {
            new RemoteRequest(Guid.NewGuid().ToString("N"), workerId, now, "Remote worker pilot",
                "On the way home, a single narration can turn a few spare minutes into time to think. This sample checks the normal voice on a remote worker.", options),
            new RemoteRequest(Guid.NewGuid().ToString("N"), workerId, now.AddSeconds(1), "Remote voice fallback pilot",
                "The first choice was unavailable. A verified local voice took its turn in the queue and produced this listening copy instead.",
                options with { Voice = "fixture_unavailable" })
        };
        foreach (var request in requests) await folder.SubmitAsync(request, ct);

        var statuses = new Dictionary<string, RemoteJobStatus>();
        do
        {
            await worker.PollAsync(ct);
            foreach (var request in requests)
            {
                if (await folder.ReadStatusAsync(request.JobId, ct) is not { } status) continue;
                if (status.State == RemoteJobState.NeedsAttention) throw new IOException(status.Attention ?? "The remote pilot needs attention.");
                statuses[request.JobId] = status;
            }
            if (statuses.Count == requests.Length && statuses.Values.All(s => s.State == RemoteJobState.Uploading)) break;
            await Task.Delay(250, ct);
        } while (true);

        var saved = await store.LoadAsync(ct);
        if (!saved.OrderBy(j => j.QueuePosition).Select(j => j.Id).SequenceEqual(requests.Select(r => r.JobId)))
            throw new IOException("The pilot queue order differs from submission order.");
        var events = await store.ReadDiagnosticEventsAsync(ct);
        var firstGenerated = events.First(e => e.JobId == requests[0].JobId && e.Stage == JobStage.Generated).Timestamp;
        var secondStarted = events.First(e => e.JobId == requests[1].JobId && e.Stage == JobStage.Synthesizing).Timestamp;
        if (secondStarted < firstGenerated) throw new IOException("The worker began generating the next request before finishing the first.");
        var results = new List<object>();
        foreach (var (request, index) in requests.Select((request, index) => (request, index)))
        {
            var status = statuses[request.JobId];
            var job = saved.Single(j => j.Id == request.JobId);
            if (status.OutputFile is null || status.ActualVoice?.Voice != voice || status.RequestedVoice.Voice != request.Options.Voice ||
                (status.FallbackReason is not null) != (index == 1) || job.Stage != JobStage.Generated || job.Attempts != 1 ||
                job.Remote?.Delivery is not null || job.CompletedChunks == 0 || job.CompletedChunks != job.Chunks.Count)
                throw new IOException("The completed pilot differs from the saved remote request or selected voice.");
            var output = folder.OutputPath(status.OutputFile);
            if (await Workspace.HashFileAsync(output, ct) != job.FinalHash || job.DurationSeconds <= 0)
                throw new IOException("The listening copy differs from the validated local MP3.");
            results.Add(new { status.JobId, status.OutputFile, status.RequestedVoice, status.ActualVoice, status.FallbackReason,
                state = status.State.ToString(), job.DurationSeconds, bytes = new FileInfo(output).Length, sha256 = job.FinalHash });
        }
        var report = new
        {
            engine, build = typeof(Job).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            provider = info.Fingerprint, serial = true, jobs = results, destination = "isolated local folder", oneDriveConfirmed = false
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        var reportFile = Path.Combine(root, "report.json");
        await File.WriteAllTextAsync(reportFile, json, ct);
        Console.WriteLine(json);
        Console.WriteLine($"Report: {reportFile}");
    }
}
