using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public sealed class RemoteQueueWorkerTests
{
    [Fact]
    public async Task AssignedWorkerResumesSavedChunksAndPublishesTheFallbackOnlyOnce()
    {
        using var test = new TestWorkspace();
        using var other = new TestWorkspace();
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
            var worker = new RemoteQueueWorker(id, "Basement", folder, test.Workspace, queue, planner);
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
            var worker = new RemoteQueueWorker(id, "Basement", folder, test.Workspace, queue, planner, syncStatus: _ => CloudSyncState.NotInSync);
            var until = DateTime.UtcNow.AddSeconds(30);
            while (queue.Snapshot().Single().Stage is not (JobStage.Generated or JobStage.Failed) && DateTime.UtcNow < until)
                await Task.Delay(50);
            var finished = Assert.Single(queue.Snapshot());
            Assert.True(finished.Stage == JobStage.Generated, finished.Error);
            Assert.Equal(finished.Chunks.Count, speech.Syntheses);
            await worker.PollAsync();
            Assert.Equal(RemoteJobState.Uploading, (await folder.ReadStatusAsync(request.JobId))?.State);
        }

        await using (var queue = OpenQueue(test.Workspace, store, speech))
        {
            var calls = speech.Syntheses;
            await queue.InitializeAsync();
            var worker = new RemoteQueueWorker(id, "Basement", folder, test.Workspace, queue, planner, syncStatus: _ => CloudSyncState.InSync);
            await worker.PollAsync();
            var status = Assert.IsType<RemoteJobStatus>(await folder.ReadStatusAsync(request.JobId));
            Assert.Equal(RemoteJobState.Ready, status.State);
            Assert.Equal("piper", status.ActualVoice?.Engine);
            Assert.Equal(folder.OutputPath(status.OutputFile!), Assert.Single(Directory.GetFiles(folder.OutputFolder, "*.mp3")));
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

    private sealed class RestartableSpeech : ISpeechProvider
    {
        public bool BlockSecondChunk { get; set; } = true;
        public string[] KokoroVoices { get; set; } = ["af_heart"];
        public int Syntheses { get; private set; }
        public TaskCompletionSource SecondChunk { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ProviderInfo> ReadyAsync(string engine, CancellationToken ct) => Task.FromResult(new ProviderInfo(
            engine, "fixture-" + engine, engine == "kokoro" ? KokoroVoices : ["en_US-lessac-medium"], "ready", 0));

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
