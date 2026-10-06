using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

internal static class QueueHostLossAcceptance
{
    internal static async Task ChildAsync(string[] args)
    {
        if (args.Length != 4 || args[2] is not ("kokoro" or "piper") || args[3] is not ("initial" or "resume")) throw new ArgumentException("Invalid queue child scope.");
        var root = GuardRoot(args[1]); var workspace = new Workspace(Path.Combine(root, "private")); using var lease = WorkspaceLease.Acquire(workspace);
        var store = new SqliteJobStore(workspace); var seed = (await store.LoadAsync()).Single();
        if (seed.Settings.Engine != args[2] || seed.Destination != Path.Combine(root, "output")) throw new IOException("The child fixture scope differs from its saved job.");
        using var provider = new LocalSpeechProvider(workspace); using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(7));
        await using var queue = new QueueCoordinator(workspace, store, provider, new(new()), new(workspace, store));
        await queue.InitializeAsync(deadline.Token);
        var retried = false;
        while (true)
        {
            var job = queue.Snapshot().Single();
            if (job.Stage == JobStage.Exported)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { processId = Environment.ProcessId, completed = true, explicitRetryAfterPendingSettlement = retried, job.Id })); return;
            }
            if (job.Stage == JobStage.Failed)
            {
                if (args[3] != "resume" || retried || job.FailureCategory != FailureCategory.Timeout) throw new IOException(job.Error);
                // Preserve the first pending-settlement result rather than hiding
                // it with a pre-initialize readiness wait. Retry is explicit.
                await File.WriteAllTextAsync(Path.Combine(root, "resume-pending.json"), JsonSerializer.Serialize(new { job.Stage, job.Error, job.FailureCategory, job.CompletedChunks }));
                while (true)
                {
                    var info = await provider.ProbeAsync(job.Settings.Engine, deadline.Token);
                    if (info.Active == 0) break;
                    await Task.Delay(200, deadline.Token);
                }
                await queue.RetryAsync(job.Id).WaitAsync(deadline.Token); retried = true;
            }
            await Task.Delay(100, deadline.Token);
        }
    }
    internal static async Task RunAsync(string root, Workspace workspace, ProviderInfo info, CancellationToken ct)
    {
        GuardRoot(root);
        var destination = Path.Combine(root, "output");
        const string sentence = "Preserve every paragraph in its original order. A reliable narration keeps technical details and survives interruptions. ";
        var source = string.Join("\n\n", Enumerable.Range(1, 4).Select(n => "Section " + n + ". " + string.Concat(Enumerable.Repeat(sentence, 3))));
        var job = new Job { Title = "Isolated real queue host loss", Source = source, Prepared = TextPreparation.Prepare(source), Destination = destination,
            Settings = new(info.Engine, info.Engine == "kokoro" ? "af_heart" : "en_US-lessac-medium", 1, false, "", info.Fingerprint, ProviderImageId: info.ImageId) };
        var fingerprint = job.Fingerprint; var settings = job.Settings; var store = new SqliteJobStore(workspace);
        using (var lease = WorkspaceLease.Acquire(workspace)) await store.SaveAsync(job, ct);
        using var provider = new LocalSpeechProvider(workspace);
        using var first = Start(root, info.Engine, "initial"); var firstOutput = first.StandardOutput.ReadToEndAsync(); var firstError = first.StandardError.ReadToEndAsync();
        ChunkReceipt? retained = null; Job? boundary = null;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (first.HasExited) throw new IOException("The queue child ended before the requested active second-chunk boundary. " + await firstError);
                var current = (await store.LoadAsync(ct)).Single();
                if (current.Receipts.Count == 1 && current.Stage == JobStage.Synthesizing)
                {
                    var active = await provider.ProbeAsync(info.Engine, ct);
                    if (active.Active == 1)
                    {
                        retained = current.Receipts.Single(); boundary = current;
                        if (retained.Index != 0 || retained.Fingerprint != fingerprint || await Workspace.HashFileAsync(workspace.ChunkPath(current, 0), ct) != retained.Hash ||
                            !File.Exists(Path.Combine(workspace.Root, "speech-admission.json"))) throw new IOException("The child boundary lacks a verified completed chunk and active request fence.");
                        await File.WriteAllTextAsync(Path.Combine(root, "host-loss-boundary.json"), JsonSerializer.Serialize(new { childProcessId = first.Id, current.Id, current.Stage, retained, active.InstanceId, active.AdmissionSequence }));
                        first.Kill(entireProcessTree: true); await first.WaitForExitAsync(ct); break;
                    }
                }
                await Task.Delay(75, ct);
            }
        }
        finally
        {
            if (!first.HasExited) { first.Kill(entireProcessTree: true); await first.WaitForExitAsync(CancellationToken.None); }
        }
        await firstOutput; await firstError;
        if (Directory.GetFiles(destination).Length != 0 || !File.Exists(Path.Combine(workspace.Root, "speech-admission.json"))) throw new IOException("Host loss published early audio or lost its outstanding request fence.");
        using var resumed = Start(root, info.Engine, "resume"); var resumedOutput = resumed.StandardOutput.ReadToEndAsync(); var resumedError = resumed.StandardError.ReadToEndAsync();
        try { await resumed.WaitForExitAsync(ct); }
        finally { if (!resumed.HasExited) { resumed.Kill(entireProcessTree: true); await resumed.WaitForExitAsync(CancellationToken.None); } }
        var output = await resumedOutput; var error = await resumedError;
        if (resumed.ExitCode != 0) throw new IOException("Resumed queue host failed: " + error);
        var final = (await store.LoadAsync(ct)).Single();
        Chunker.ValidateManifest(final.Chunks, final.Prepared.Script);
        if (final.Stage != JobStage.Exported || final.Source != source || final.Settings != settings || final.Fingerprint != fingerprint || final.Receipts.Count != final.Chunks.Count ||
            final.Title != job.Title || final.CreatedUtc != job.CreatedUtc || final.Destination != destination || !final.Chunks.SequenceEqual(boundary!.Chunks) ||
            final.Receipts.Single(r => r.Index == 0) != retained || await Workspace.HashFileAsync(workspace.ChunkPath(final, 0), ct) != retained!.Hash ||
            string.Concat(final.Prepared.Spans.Select(s => s.Original)) != source || string.Concat(final.Prepared.Spans.Select(s => s.Narration)) != final.Prepared.Script ||
            File.Exists(Path.Combine(workspace.Root, "speech-admission.json"))) throw new IOException("Resumed ordered queue state or retained chunk changed.");
        var exported = Path.Combine(destination, final.ExportName);
        if (Directory.GetFiles(destination).Length != 1 || await Workspace.HashFileAsync(exported, ct) != final.FinalHash) throw new IOException("Resumed final export checksum or scope failed.");
        await new AudioPipeline(new()).ValidateFinalAsync(final, exported, ct);
        var ready = await provider.ProbeAsync(info.Engine, ct);
        if (ready.Active != 0 || ready.InstanceId != info.InstanceId) throw new IOException("Host loss restarted or left active inference in the owned service.");
        var report = new { engine = info.Engine, applicationBuild = typeof(Job).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            info.ImageId, info.Fingerprint, firstChildPid = first.Id, resumedChildPid = resumed.Id, killedDuringActiveSecondChunk = true,
            completedChunkReceiptAndBytesRetained = true, immutableSourceAndSettings = true, orderedManifestAndFinalMp3Validated = true,
            noEarlyExport = true, serviceProcessUnchanged = true, chunks = final.Chunks.Count, final.DurationSeconds, mp3 = exported, sha256 = final.FinalHash,
            explicitRetryAfterPendingSettlement = File.Exists(Path.Combine(root, "resume-pending.json")), childCompletion = output.Trim(),
            scope = "Real command-line queue-host termination during second-chunk inference, relaunch, retained first chunk and final MP3. WPF host interaction, other file-write boundaries, sleep/wake and listening remain separate." };
        await File.WriteAllTextAsync(Path.Combine(root, "queue-host-loss-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static string GuardRoot(string value)
    {
        var root = Path.GetFullPath(value); var parent = Path.GetFullPath(Path.Combine("artifacts", "pilot"));
        if (!Workspace.IsWithin(parent, root) || root.Equals(parent, StringComparison.OrdinalIgnoreCase)) throw new IOException("Use an isolated pilot fixture.");
        Workspace.RejectReparsePoints(root); return root;
    }
    private static Process Start(string root, string engine, string mode)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { typeof(QueueHostLossAcceptance).Assembly.Location, "--queue-host-child", root, engine, mode }) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("The owned queue child could not start.");
    }
}
