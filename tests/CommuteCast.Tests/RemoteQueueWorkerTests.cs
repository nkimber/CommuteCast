using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public sealed class RemoteQueueWorkerTests
{
    [Theory]
    [InlineData(1, false, true, false)]
    [InlineData(RemoteRequest.CurrentVersion, false, true, false)]
    [InlineData(RemoteRequest.CurrentVersion, true, false, false)]
    [InlineData(RemoteRequest.CurrentVersion, true, true, true)]
    public async Task HostedFallbackRequiresRequestConsentAndWorkerPermission(int version, bool consent, bool workerAllowsHosted, bool usesHosted)
    {
        using var test = new TestWorkspace();
        var probes = new List<string>();
        var hosted = new NarrationOptions("openai", "alloy", 1, false, "", new(), Speech: new("gpt-4o-mini-tts"));
        var local = new NarrationOptions("piper", "en_US-lessac-medium", 1, false, "", new());
        var worker = Guid.NewGuid().ToString("N");
        var folder = new RemoteQueueFolder(test.Destination, test.Workspace);
        var request = new RemoteRequest(Guid.NewGuid().ToString("N"), worker, DateTimeOffset.UtcNow, "Commute",
            "A synthetic narration.", local with { Voice = "en_US-missing" }, Version: version, AllowHostedFallback: consent);
        await folder.SubmitAsync(request);
        var saved = Assert.IsType<RemoteRequest>(await folder.ReadRequestAsync(request.JobId));
        var planner = new RemoteRenderPlanner((snapshot, _) =>
        {
            probes.Add(snapshot.Engine);
            var settings = snapshot.Engine == hosted.Engine ? hosted : local;
            return Task.FromResult(new ProviderInfo(settings.Engine, settings.Speech?.Identity(settings.Engine) ?? "fixture-piper", [settings.Voice], "ready", 0));
        }, [new("Hosted substitute", hosted), new("Local substitute", local)], workerAllowsHosted);

        var job = await planner.PrepareAsync(saved, worker, folder.Root, default);

        Assert.Equal(usesHosted ? "openai" : "piper", job.Settings.Engine);
        Assert.Equal(consent, job.Remote!.AllowHostedFallback);
        Assert.Contains("unavailable", job.Remote.FallbackReason);
        Assert.Equal(usesHosted ? ["piper", "openai"] : new[] { "piper", "piper" }, probes);

        if (workerAllowsHosted)
        {
            var direct = await planner.PrepareAsync(request with { Options = hosted, AllowHostedFallback = false }, worker, folder.Root, default);
            Assert.Equal(hosted.Engine, direct.Settings.Engine);
            Assert.Null(direct.Remote!.FallbackReason);
        }
    }

    [Fact]
    public async Task RestartedQueueDoesNotRequestHostedSpeechForALegacyFallbackWithoutConsent()
    {
        using var test = new TestWorkspace();
        var store = new SqliteJobStore(test.Workspace);
        var speech = new RestartableSpeech();
        var workerId = Guid.NewGuid().ToString("N");
        var folder = new RemoteQueueFolder(test.Destination, test.Workspace);
        var options = new NarrationOptions("kokoro", "af_missing", 1, false, "", new());
        var request = new RemoteRequest(Guid.NewGuid().ToString("N"), workerId, DateTimeOffset.UtcNow, "Commute", "Some text.", options);
        var hosted = new SpeechConfiguration("gpt-4o-mini-tts");
        var fingerprint = hosted.Identity("openai");
        var job = new Job
        {
            Id = request.JobId, Title = request.Title, Source = request.Text, Stage = JobStage.Queued,
            Prepared = TextPreparation.Prepare(request.Text, profile: options.Profile),
            Settings = new("openai", "alloy", 1, false, "", fingerprint, options.Profile, Speech: hosted),
            Remote = new(workerId, folder.Root, RemoteQueueFolder.Fingerprint(request), RemoteVoice.From(options),
                new("openai", "alloy", 1, hosted.Model), "Requested voice unavailable.")
        };
        await store.SaveAsync(job);
        await folder.SubmitAsync(request);
        await using var queue = OpenQueue(test.Workspace, store, speech);
        await queue.InitializeAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (queue.Snapshot().Single().Stage != JobStage.Failed) await Task.Delay(25, timeout.Token);
        var worker = new RemoteQueueWorker(workerId, "Basement", folder, test.Workspace, queue,
            new((settings, ct) => speech.ReadyAsync(settings.Engine, ct), []));
        await worker.PollAsync(timeout.Token);

        var status = Assert.IsType<RemoteJobStatus>(await folder.ReadStatusAsync(job.Id, timeout.Token));
        Assert.Equal(RemoteJobState.NeedsAttention, status.State);
        Assert.Contains("sender's permission", status.Attention);
        Assert.Equal(0, speech.ReadinessChecks);
        Assert.Equal(0, speech.Syntheses);
    }

    [Fact]
    public async Task AssignedWorkerResumesSavedChunksAndPublishesTheFallbackOnlyOnce()
    {
        using var test = new TestWorkspace();
        using var syncRoot = new CloudFileSyncTests.TestSyncRoot(test.Destination);
        using var other = new TestWorkspace();
        var clock = new DeliveryClock();
        var store = new SqliteJobStore(test.Workspace);
        var speech = new RestartableSpeech();
        var id = Guid.NewGuid().ToString("N");
        var request = new RemoteRequest(Guid.NewGuid().ToString("N"), id, DateTimeOffset.UtcNow, "Commute",
            string.Concat(Enumerable.Repeat("A short sentence about getting home. ", 23)), new("kokoro", "af_missing", 1.1, false, "", new()));
        var planner = new RemoteRenderPlanner((settings, ct) => speech.ReadyAsync(settings.Engine, ct),
            [new("Auditioned fallback", new("piper", "en_US-lessac-medium", 1, false, "", new()))]);
        var folder = new RemoteQueueFolder(test.Destination, test.Workspace);
        await folder.SubmitAsync(request);

        var otherStore = new SqliteJobStore(other.Workspace);
        await using (var otherQueue = OpenQueue(other.Workspace, otherStore, speech))
        {
            await otherQueue.InitializeAsync();
            var otherWorker = new RemoteQueueWorker(Guid.NewGuid().ToString("N"), "Office", folder, other.Workspace, otherQueue, planner);
            await otherWorker.PollAsync();
            Assert.Empty(otherQueue.Snapshot());
            Assert.Null(await folder.ReadStatusAsync(request.JobId));
        }

        await using (var queue = OpenQueue(test.Workspace, store, speech))
        {
            await queue.InitializeAsync();
            var worker = new RemoteQueueWorker(id, "Basement", folder, test.Workspace, queue, planner, clock);
            await worker.PollAsync();
            var until = DateTime.UtcNow.AddSeconds(30);
            while (!speech.SecondChunk.Task.IsCompleted && queue.Snapshot().Single().Stage is not (JobStage.Failed or JobStage.Generated) && DateTime.UtcNow < until)
                await Task.Delay(50);
            Assert.True(speech.SecondChunk.Task.IsCompleted, queue.Snapshot().Single().Error);
            await worker.PollAsync();
            var status = Assert.IsType<RemoteJobStatus>(await folder.ReadStatusAsync(request.JobId));
            Assert.Equal(RemoteJobState.Generating, status.State);
            Assert.Equal(1, status.ChunksDone);
            Assert.Equal("af_missing", status.RequestedVoice.Voice);
            Assert.Equal("Auditioned fallback", status.ActualVoice?.Preset);
            Assert.Contains("unavailable", status.FallbackReason);
            Assert.Equal(1.1, status.ActualVoice?.Speed);
        }

        speech.BlockSecondChunk = false;
        await using (var queue = OpenQueue(test.Workspace, store, speech))
        {
            await queue.InitializeAsync();
            var worker = new RemoteQueueWorker(id, "Basement", folder, test.Workspace, queue, planner, clock);
            var until = DateTime.UtcNow.AddSeconds(30);
            while (queue.Snapshot().Single().Stage is not (JobStage.Generated or JobStage.Failed) && DateTime.UtcNow < until)
                await Task.Delay(50);
            var finished = Assert.Single(queue.Snapshot());
            Assert.True(finished.Stage == JobStage.Generated, finished.Error);
            Assert.Equal(finished.Chunks.Count, speech.Syntheses);
            await worker.PollAsync();
            var uploading = Assert.IsType<RemoteJobStatus>(await folder.ReadStatusAsync(request.JobId));
            Assert.Equal(RemoteJobState.Uploading, uploading.State);
            Assert.Null(uploading.Attention);
            clock.Advance(TimeSpan.FromMinutes(15));
            await worker.PollAsync();
            var stillUploading = Assert.IsType<RemoteJobStatus>(await folder.ReadStatusAsync(request.JobId));
            Assert.Equal(RemoteJobState.Uploading, stillUploading.State);
            Assert.Contains("Files On-Demand", stillUploading.Attention);
            Assert.Equal(finished.Chunks.Count, speech.Syntheses);
        }

        var deliveredFile = Assert.Single(Directory.GetFiles(folder.OutputFolder, "*.mp3"));
        syncRoot.ConfirmFile(deliveredFile);
        await using (var queue = OpenQueue(test.Workspace, store, speech))
        {
            var calls = speech.Syntheses;
            await queue.InitializeAsync();
            var reopenedFolder = new RemoteQueueFolder(test.Destination + Path.DirectorySeparatorChar, test.Workspace);
            var worker = new RemoteQueueWorker(id, "Basement", reopenedFolder, test.Workspace, queue, planner, clock);
            await worker.PollAsync();
            var status = Assert.IsType<RemoteJobStatus>(await folder.ReadStatusAsync(request.JobId));
            Assert.Equal(RemoteJobState.Ready, status.State);
            Assert.Null(status.Attention);
            Assert.Equal("piper", status.ActualVoice?.Engine);
            Assert.Equal(deliveredFile, folder.OutputPath(status.OutputFile!));
            var saved = Assert.Single(await store.LoadAsync());
            var receipt = Assert.IsType<RemoteDelivery>(saved.Remote?.Delivery);
            Assert.Equal(status.OutputFile, receipt.OutputFile);
            Assert.Equal(saved.FinalHash, receipt.Hash);
            var inspection = await reopenedFolder.InspectAsync(request.JobId);
            Assert.True(inspection.RegisteredCloudFilesRoot);
            Assert.NotNull(inspection.Job);
            Assert.True(inspection.Job.RequestFileVisible);
            Assert.Equal(RemoteJobState.Ready, inspection.Job.State);
            Assert.Equal(id, inspection.Job.WorkerId);
            Assert.Equal(CloudSyncState.InSync, inspection.Job.OutputSync);
            Assert.Equal(calls, speech.Syntheses);
        }

        File.Delete(deliveredFile);
        File.Delete(Path.Combine(folder.Root, "Status", request.JobId + ".json"));
        await using (var queue = OpenQueue(test.Workspace, store, speech))
        {
            var calls = speech.Syntheses;
            await queue.InitializeAsync();
            var worker = new RemoteQueueWorker(id, "Basement", folder, test.Workspace, queue, planner, clock);
            await worker.PollAsync();
            var status = Assert.IsType<RemoteJobStatus>(await folder.ReadStatusAsync(request.JobId));
            Assert.Equal(RemoteJobState.NeedsAttention, status.State);
            Assert.Contains("previously delivered MP3", status.Attention);
            Assert.False(await worker.RetryAsync(request.JobId));
            Assert.Empty(Directory.GetFiles(folder.OutputFolder, "*.mp3"));
            Assert.Equal(calls, speech.Syntheses);
        }
    }

    [Fact]
    public async Task WorkerWithNoUsableFallbackReportsAttentionAndAcceptsTheNextJob()
    {
        using var test = new TestWorkspace();
        var store = new SqliteJobStore(test.Workspace);
        var speech = new RestartableSpeech { BlockSecondChunk = false };
        var id = Guid.NewGuid().ToString("N");
        var folder = new RemoteQueueFolder(test.Destination, test.Workspace);
        var first = new RemoteRequest(Guid.NewGuid().ToString("N"), id, DateTimeOffset.UtcNow, "First", "Short narration.",
            new("kokoro", "af_missing", 1, false, "", new()));
        var second = first with { JobId = Guid.NewGuid().ToString("N"), CreatedUtc = first.CreatedUtc.AddSeconds(1), Options = first.Options with { Voice = "af_heart" } };
        await folder.SubmitAsync(first);
        await folder.SubmitAsync(second);
        await using var queue = OpenQueue(test.Workspace, store, speech);
        await queue.InitializeAsync();
        var worker = new RemoteQueueWorker(id, "Basement", folder, test.Workspace, queue,
            new((settings, ct) => speech.ReadyAsync(settings.Engine, ct), []));
        await worker.PollAsync();
        await worker.PollAsync();
        var trouble = Assert.IsType<RemoteJobStatus>(await folder.ReadStatusAsync(first.JobId));
        Assert.Equal(RemoteJobState.NeedsAttention, trouble.State);
        Assert.Contains("fallbacks", trouble.Attention);
        Assert.Equal(second.JobId, Assert.Single(queue.Snapshot()).Id);

        speech.KokoroVoices = ["af_heart", "af_missing"];
        Assert.True(await worker.RetryAsync(first.JobId));
        var recovered = Assert.IsType<RemoteJobStatus>(await folder.ReadStatusAsync(first.JobId));
        Assert.Equal(RemoteJobState.Queued, recovered.State);
        Assert.Equal("af_missing", recovered.ActualVoice?.Voice);
        Assert.Null(recovered.FallbackReason);
        Assert.Equal(2, folder.RequestIds().Count);
        Assert.Equal(new[] { second.JobId, first.JobId }, queue.Snapshot().Select(j => j.Id));
    }

    private static QueueCoordinator OpenQueue(Workspace workspace, IJobStore store, ISpeechProvider speech) =>
        new(workspace, store, speech, new(new()), new(workspace, store));

    private sealed class DeliveryClock : TimeProvider
    {
        private TimeSpan offset;
        public void Advance(TimeSpan time) => offset += time;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + offset;
    }

    private sealed class RestartableSpeech : ISpeechProvider
    {
        public bool BlockSecondChunk { get; set; } = true;
        public string[] KokoroVoices { get; set; } = ["af_heart"];
        public int Syntheses { get; private set; }
        public int ReadinessChecks { get; private set; }
        public TaskCompletionSource SecondChunk { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ProviderInfo> ReadyAsync(string engine, CancellationToken ct)
        {
            ReadinessChecks++;
            return Task.FromResult(new ProviderInfo(engine, "fixture-" + engine,
                engine == "kokoro" ? KokoroVoices : ["en_US-lessac-medium"], "ready", 0));
        }

        public async Task SynthesizeAsync(NarrationSettings settings, string text, string output, CancellationToken ct)
        {
            if (BlockSecondChunk && Syntheses == 1)
            {
                SecondChunk.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            ct.ThrowIfCancellationRequested();
            Syntheses++;
            TestWorkspace.WriteWave(output, Math.Max(2, text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length * .12));
        }
    }
}
