using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CommuteCast.Core;
using CommuteCast.Infrastructure;

internal static class RemoteWorkerLossAcceptance
{
    private const string PendingReservation = "speech-admission.json";

    public static async Task RunAsync(string[] args)
    {
        if (args.Length != 2 || args[1] is not ("kokoro" or "piper"))
            throw new ArgumentException("Use --verify-remote-worker-loss kokoro or --verify-remote-worker-loss piper.");
        var engine = args[1];
        var root = GuardRoot(Path.Combine("artifacts", "pilot", $"remote-loss-{engine}-{Guid.NewGuid():N}"));
        var workspace = new Workspace(Path.Combine(root, "private"));
        var pin = Path.Combine(new Workspace(createRoot: false).Root, "provider-lock.local.json");
        Workspace.RejectFileReparsePoint(pin);
        File.Copy(pin, Path.Combine(workspace.Root, "provider-lock.local.json"));
        var folder = new RemoteQueueFolder(Directory.CreateDirectory(Path.Combine(root, "shared")).FullName, workspace);
        var store = new SqliteJobStore(workspace);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(7));
        var ct = deadline.Token;
        using var provider = new LocalSpeechProvider(workspace);
        var info = await provider.ProbeAsync(engine, ct);
        var voice = SpeechVoiceCatalog.DefaultVoice(engine);
        if (info.State != "ready" || info.Active != 0 || !info.Voices.Contains(voice))
            throw new IOException("The installed speech service and pilot voice must be ready and idle.");

        var workerId = Guid.NewGuid().ToString("N");
        var options = new NarrationOptions(engine, voice, 1, false, "", new());
        var text = string.Join("\n\n", Enumerable.Range(1, 4).Select(n => "Section " + n + ". " + string.Concat(Enumerable.Repeat(
            "A dependable narration preserves every sentence and keeps its place when the worker restarts. The next request belongs after this one. ", 3))));
        var first = new RemoteRequest(Guid.NewGuid().ToString("N"), workerId, DateTimeOffset.UtcNow, "Remote restart pilot", text,
            options with { Voice = "fixture_unavailable" });
        var next = new RemoteRequest(Guid.NewGuid().ToString("N"), workerId, first.CreatedUtc.AddSeconds(1), "Waiting after restart",
            "This second job waits its turn while the first narration recovers.", options);
        await folder.SubmitAsync(first, ct);
        await folder.SubmitAsync(next, ct);
        var fixture = new Fixture(engine, workerId, first.JobId, next.JobId);
        await File.WriteAllTextAsync(Path.Combine(root, "fixture.json"), JsonSerializer.Serialize(fixture), ct);
        _ = await store.LoadAsync(ct);

        Observation boundary;
        int firstPid;
        using (var child = Start(root, "initial"))
        {
            firstPid = child.Id;
            var errors = child.StandardError.ReadToEndAsync();
            try
            {
                boundary = await WaitForActiveSecondChunkAsync(child, workspace, store, folder, provider, first, errors, ct);
                // Terminate only the isolated queue host started above. The model stays running.
                child.Kill();
                await child.WaitForExitAsync(ct);
                if (child.ExitCode == 0) throw new IOException("The first host exited normally before termination.");
            }
            finally { await StopIfRunningAsync(child); }
        }
        await File.WriteAllTextAsync(Path.Combine(root, "interrupted.json"), JsonSerializer.Serialize(new
        { childPid = firstPid, first.JobId, receipt = boundary.Receipt, boundary.AdmissionSequence }), ct);
        if (Directory.Exists(folder.OutputFolder) && Directory.GetFiles(folder.OutputFolder, "*.mp3").Length != 0 ||
            !File.Exists(Path.Combine(workspace.Root, PendingReservation)))
            throw new IOException("The interrupted host published early or lost the active speech reservation.");

        int resumedPid;
        using (var child = Start(root, "resume"))
        {
            resumedPid = child.Id;
            var errors = child.StandardError.ReadToEndAsync();
            try { await child.WaitForExitAsync(ct); }
            finally { await StopIfRunningAsync(child); }
            if (child.ExitCode != 0) throw new IOException("The resumed remote worker failed: " + await errors);
        }

        var saved = await store.LoadAsync(ct);
        var firstResult = saved.Single(j => j.Id == first.JobId);
        var nextResult = saved.Single(j => j.Id == next.JobId);
        if (saved.Count != 2 || firstResult.Settings != boundary.Settings || firstResult.Remote != boundary.Remote ||
            firstResult.Receipts.Single(r => r.Index == 0) != boundary.Receipt ||
            await Workspace.HashFileAsync(workspace.ChunkPath(firstResult, 0), ct) != boundary.Receipt.Hash ||
            File.Exists(Path.Combine(workspace.Root, PendingReservation)))
            throw new IOException("The restarted worker changed the frozen fallback, lost its first chunk, or left a speech reservation outstanding.");
        var events = await store.ReadDiagnosticEventsAsync(ct);
        if (events.First(e => e.JobId == next.JobId && e.Stage == JobStage.Synthesizing).Timestamp <
            events.First(e => e.JobId == first.JobId && e.Stage == JobStage.Generated).Timestamp)
            throw new IOException("The waiting request started before the interrupted request recovered.");
        var outputs = new List<object>();
        foreach (var job in new[] { firstResult, nextResult })
        {
            var status = await folder.ReadStatusAsync(job.Id, ct);
            if (status?.State != RemoteJobState.Uploading || status.WorkerId != workerId || status.OutputFile is null ||
                status.ChunksDone != job.Chunks.Count || job.Stage != JobStage.Generated)
                throw new IOException("The restarted worker did not publish complete local delivery status.");
            var output = folder.OutputPath(status.OutputFile);
            if (await Workspace.HashFileAsync(output, ct) != job.FinalHash) throw new IOException("The resumed listening copy differs from the validated MP3.");
            await new AudioPipeline(new()).ValidateFinalAsync(job, output, ct);
            outputs.Add(new { job.Id, status.OutputFile, job.DurationSeconds, status.FallbackReason, state = status.State.ToString() });
        }
        if (Directory.GetFiles(folder.OutputFolder, "*.mp3").Length != 2) throw new IOException("The restart produced a duplicate or missing MP3.");
        var ready = await provider.ProbeAsync(engine, ct);
        if (ready.Active != 0 || ready.InstanceId != info.InstanceId) throw new IOException("The pilot interrupted the owned speech service.");

        var report = new
        {
            engine, build = typeof(Job).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            firstPid, resumedPid, interruptedDuringSecondChunk = true, firstChunkRetained = true,
            fallbackRetained = true, serviceInstanceUnchanged = true, waitingRequestRanSecond = true, outputs, oneDriveConfirmed = false
        };
        var reportFile = Path.Combine(root, "report.json");
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(reportFile, json, ct);
        Console.WriteLine(json);
        Console.WriteLine($"Report: {reportFile}");
    }

    public static async Task ChildAsync(string[] args)
    {
        if (args.Length != 3 || args[2] is not ("initial" or "resume")) throw new ArgumentException("Invalid remote pilot child arguments.");
        var root = GuardRoot(args[1]);
        var fixture = JsonSerializer.Deserialize<Fixture>(await File.ReadAllTextAsync(Path.Combine(root, "fixture.json")))
            ?? throw new IOException("The remote pilot has no fixture identity.");
        if (fixture.Engine is not ("kokoro" or "piper") || !RemoteIdentity.IsValid(fixture.WorkerId) ||
            !RemoteIdentity.IsValid(fixture.First) || !RemoteIdentity.IsValid(fixture.Next)) throw new IOException("The remote pilot fixture identity is invalid.");

        var workspace = new Workspace(Path.Combine(root, "private"));
        var folder = new RemoteQueueFolder(Path.Combine(root, "shared"), workspace);
        using var lease = WorkspaceLease.Acquire(workspace);
        using var provider = new LocalSpeechProvider(workspace);
        var store = new SqliteJobStore(workspace);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var ct = deadline.Token;
        var options = new NarrationOptions(fixture.Engine, SpeechVoiceCatalog.DefaultVoice(fixture.Engine), 1, false, "", new());
        var planner = new RemoteRenderPlanner((settings, token) => provider.CaptureForSubmissionAsync(settings.Engine, null, token),
            [new("Verified local fallback", options)]);
        await using var queue = new QueueCoordinator(workspace, store, provider, new(new()), new(workspace, store));
        await queue.InitializeAsync(ct);
        var worker = new RemoteQueueWorker(fixture.WorkerId, "Restart pilot", folder, workspace, queue, planner);
        var ids = new[] { fixture.First, fixture.Next };
        if (!folder.RequestIds().Order().SequenceEqual(ids.Order())) throw new IOException("The isolated worker request scope changed.");
        foreach (var id in ids)
        {
            var request = await folder.ReadRequestAsync(id, ct);
            if (request?.TargetWorkerId != fixture.WorkerId || request.Options.Engine != fixture.Engine)
                throw new IOException("The isolated request identity changed.");
        }
        await worker.CheckInAsync(RemoteWorkerState.Ready, []);
        while (true)
        {
            await worker.PollAsync(ct);
            var jobs = queue.Snapshot();
            if (jobs.FirstOrDefault(j => j.Stage == JobStage.Failed) is { } failed) throw new IOException(failed.Error);
            if (jobs.Count == 2 && jobs.All(j => j.Stage == JobStage.Generated))
            {
                await worker.PollAsync(ct);
                return;
            }
            await Task.Delay(75, ct);
        }
    }

    private static async Task<Observation> WaitForActiveSecondChunkAsync(Process child, Workspace workspace, SqliteJobStore store,
        RemoteQueueFolder folder, LocalSpeechProvider provider, RemoteRequest request, Task<string> errors, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (child.HasExited) throw new IOException("The initial worker exited before the interruption boundary: " + await errors);
            var job = (await store.LoadAsync(ct)).FirstOrDefault(j => j.Id == request.JobId);
            if (job?.Stage == JobStage.Synthesizing && job.Receipts.Count == 1 && job.Remote is { FallbackReason: not null } remote &&
                (await folder.ReadStatusAsync(request.JobId, ct)) is { State: RemoteJobState.Generating, ChunksDone: 1 })
            {
                var active = await provider.ProbeAsync(job.Settings.Engine, ct);
                var reservation = Path.Combine(workspace.Root, PendingReservation);
                if (active.Active == 1 && active.AdmissionSequence is { } sequence && File.Exists(reservation))
                {
                    using var pending = JsonDocument.Parse(await File.ReadAllTextAsync(reservation, ct));
                    if (pending.RootElement.GetProperty("Sequence").GetInt64() == sequence &&
                        pending.RootElement.GetProperty("Instance").GetString() == active.InstanceId)
                    {
                        var receipt = job.Receipts.Single();
                        if (receipt.Index != 0 || await Workspace.HashFileAsync(workspace.ChunkPath(job, 0), ct) != receipt.Hash)
                            throw new IOException("The interrupted worker has no verified first chunk.");
                        return new(receipt, job.Settings, remote, sequence);
                    }
                }
            }
            await Task.Delay(50, ct);
        }
    }

    private static string GuardRoot(string path)
    {
        var root = Path.GetFullPath(path);
        if (Path.GetDirectoryName(root) != Path.GetFullPath(Path.Combine("artifacts", "pilot")) ||
            !Path.GetFileName(root).StartsWith("remote-loss-", StringComparison.Ordinal)) throw new IOException("Use an isolated remote pilot folder.");
        Workspace.RejectReparsePoints(root);
        return root;
    }

    private static Process Start(string root, string phase)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var argument in new[] { typeof(RemoteWorkerLossAcceptance).Assembly.Location, "--remote-worker-child", root, phase })
            start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("The isolated remote worker could not start.");
    }

    private static async Task StopIfRunningAsync(Process child)
    {
        if (child.HasExited) return;
        child.Kill();
        await child.WaitForExitAsync(CancellationToken.None);
    }

    private sealed record Fixture(string Engine, string WorkerId, string First, string Next);
    private sealed record Observation(ChunkReceipt Receipt, NarrationSettings Settings, RemoteRender Remote, long AdmissionSequence);
}
