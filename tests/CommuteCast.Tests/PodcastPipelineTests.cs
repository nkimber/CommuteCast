using System.Net;
using System.Text.Json;
using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public class PodcastPipelineTests
{
    private sealed class Secrets : ISpeechSecrets
    { public string? Get(string provider) => "fixture-key"; public void Set(string provider, string key) { } public void Remove(string provider) { } }
    private sealed class SpeechHttp : HttpMessageHandler
    {
        public List<(string Voice, string Text)> Posts { get; } = [];
        public int FailAt = -1;
        public bool BlockPost;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("api.openai.com", request.RequestUri!.Host);
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"gpt-4o-mini-tts\"}") };
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Posts.Add((json.RootElement.GetProperty("voice").GetString()!, json.RootElement.GetProperty("input").GetString()!));
            if (BlockPost) { Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
            if (Posts.Count == FailAt) throw new HttpRequestException("Fixture interruption");
            var bytes = new byte[48000];
            for (var i = 0; i < 24000; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 2, 2), (short)(8000 * Math.Sin(i * 2 * Math.PI * (180 + Posts.Count * 70) / 24000)));
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
    }
    private static Job Job(TestWorkspace test, int count)
    {
        var voices = new[] { "coral", "onyx", "sage", "nova", "echo" };
        var episode = PodcastTests.Episode(count) with { Speakers = PodcastTests.Episode(count).Speakers.Select((s, i) => s with { Voice = voices[i] }).ToArray() };
        var settings = PodcastTests.Settings("openai"); settings = settings with { ProviderFingerprint = settings.Speech!.Identity("openai") };
        var source = string.Join('\n', episode.Speakers.Select((s, i) => $"{s.Name}: Contribution number {i + 1}."));
        var prepared = PodcastScript.Prepare(source, episode, settings);
        return new() { Title = "Synthetic podcast", Source = source, Prepared = prepared.Prepared, Chunks = prepared.Units, Settings = settings, Episode = episode, ChunkingVersion = PodcastScript.ChunkVersion, AudioContractVersion = PodcastScript.AudioVersion, Destination = test.Destination };
    }
    private static async Task<Job> Settle(QueueCoordinator queue)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (queue.Snapshot().Single().Stage is not (JobStage.Failed or JobStage.Exported)) await Task.Delay(50, deadline.Token);
        return queue.Snapshot().Single();
    }
    [Theory] [InlineData(2)] [InlineData(5)]
    public async Task TwoAndFiveSpeakerHostedEpisodesProduceOneValidatedMp3WithoutDocker(int count)
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace); var handler = new SpeechHttp(); using var http = new HttpClient(handler);
        using var provider = new SpeechProviderService(test.Workspace, new(), secrets: new Secrets(), http: http);
        await using var queue = new QueueCoordinator(test.Workspace, store, provider, new(new()), new(test.Workspace, store)); await queue.InitializeAsync(); var job = Job(test, count); await queue.AddAsync(job);
        var done = await Settle(queue); Assert.True(done.Stage == JobStage.Exported, done.Error);
        Assert.Equal(count, handler.Posts.Count); Assert.Equal(job.Episode!.Speakers.Select(s => s.Voice), handler.Posts.Select(p => p.Voice)); Assert.All(handler.Posts, p => Assert.DoesNotContain(":", p.Text));
        Assert.InRange(done.DurationSeconds, count + (count - 1) * .08 - .2, count + (count - 1) * .08 + .2); Assert.Single(Directory.GetFiles(test.Destination, "*.mp3"));
        Assert.Equal(count, done.SpeechAttempts!.Count); Assert.All(done.SpeechAttempts, a => Assert.Equal("received", a.State));
        var metadata = await ProcessRunner.RunAsync("ffprobe", ["-v", "error", "-show_entries", "format_tags=comment", "-of", "json", test.Workspace.FinalPath(done)], TimeSpan.FromSeconds(10));
        Assert.Contains("Podcast participants: " + count, metadata.Output); Assert.DoesNotContain("Contribution number", metadata.Output);
    }
    [Fact]
    public async Task FailedSecondTurnRetainsFirstAndExplicitRetryDoesNotRepeatIt()
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace); var handler = new SpeechHttp { FailAt = 2 }; using var http = new HttpClient(handler);
        using var provider = new SpeechProviderService(test.Workspace, new(), secrets: new Secrets(), http: http);
        await using var queue = new QueueCoordinator(test.Workspace, store, provider, new(new()), new(test.Workspace, store)); await queue.InitializeAsync(); var job = Job(test, 2); await queue.AddAsync(job);
        var failed = await Settle(queue); Assert.Equal(JobStage.Failed, failed.Stage); Assert.Single(failed.Receipts); Assert.Equal("uncertain", failed.SpeechAttempts![1].State);
        await queue.RetryAsync(job.Id); var done = await Settle(queue); Assert.True(done.Stage == JobStage.Exported, done.Error); Assert.Equal(3, handler.Posts.Count); Assert.Equal("onyx", handler.Posts[2].Voice);
    }
    [Fact]
    public async Task HostLossDoesNotAutomaticallyRepeatUnresolvedHostedRequest()
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace); var job = Job(test, 2); job.Stage = JobStage.Synthesizing;
        job.SpeechAttempts = [new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, "openai", job.Settings.Speech!.Model, CommuteCast.Core.Job.Hash(job.Chunks[0].Text), "started", UnitIndex: 0)]; await store.SaveAsync(job);
        var handler = new SpeechHttp(); using var http = new HttpClient(handler); using var provider = new SpeechProviderService(test.Workspace, new(), secrets: new Secrets(), http: http);
        await using var queue = new QueueCoordinator(test.Workspace, store, provider, new(new()), new(test.Workspace, store)) { Paused = true }; await queue.InitializeAsync();
        var recovered = queue.Snapshot().Single(); Assert.Equal(JobStage.Failed, recovered.Stage); Assert.Contains("may have been billed", recovered.Error); Assert.Equal("uncertain", recovered.SpeechAttempts![0].State); Assert.Empty(handler.Posts);
        await queue.RetryAsync(job.Id); Assert.NotNull(queue.Snapshot().Single().HostedRetryAuthorizedUtc);
    }
    [Fact]
    public async Task LostPreviouslyReceivedAudioRequiresExplicitRetryBeforeAnotherPost()
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace); var job = Job(test, 2); job.Stage = JobStage.Synthesizing;
        job.Receipts = [new(0, new string('a', 64), job.Fingerprint, 1)];
        job.SpeechAttempts = [new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, "openai", job.Settings.Speech!.Model, CommuteCast.Core.Job.Hash(job.Chunks[0].Text), "received", UnitIndex: 0)]; await store.SaveAsync(job);
        var handler = new SpeechHttp(); using var http = new HttpClient(handler); using var provider = new SpeechProviderService(test.Workspace, new(), secrets: new Secrets(), http: http);
        await using var queue = new QueueCoordinator(test.Workspace, store, provider, new(new()), new(test.Workspace, store)); await queue.InitializeAsync();
        var failed = await Settle(queue); Assert.Equal(JobStage.Failed, failed.Stage); Assert.Empty(handler.Posts); Assert.Contains("may have been billed", failed.Error);
        await queue.RetryAsync(job.Id); Assert.Equal(JobStage.Exported, (await Settle(queue)).Stage); Assert.Equal(2, handler.Posts.Count);
    }
    [Fact]
    public async Task PowerInterruptStopsUnresolvedCloudInferenceForExplicitRetry()
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace); var handler = new SpeechHttp { BlockPost = true }; using var http = new HttpClient(handler); using var provider = new SpeechProviderService(test.Workspace, new(), secrets: new Secrets(), http: http);
        await using var queue = new QueueCoordinator(test.Workspace, store, provider, new(new()), new(test.Workspace, store)); await queue.InitializeAsync(); await queue.AddAsync(Job(test, 2)); await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await queue.SuspendForPowerAsync(); var stopped = queue.Snapshot().Single(); Assert.Equal(JobStage.Failed, stopped.Stage); Assert.Contains("may have been billed", stopped.Error); Assert.Single(handler.Posts); queue.ReleasePowerHold(); Assert.Equal(JobStage.Failed, queue.Snapshot().Single().Stage);
    }
    [Fact]
    public async Task HostedAuditionKeepsSourceFreeRequestHistoryAfterFileRemoval()
    {
        using var test = new TestWorkspace(); var handler = new SpeechHttp(); using var http = new HttpClient(handler); using var provider = new SpeechProviderService(test.Workspace, new(), secrets: new Secrets(), http: http);
        using var gate = new SemaphoreSlim(1); var auditions = new AuditionGenerator(test.Workspace, provider, gate);
        var audio = await auditions.GenerateAsync(AuditionRequest.ApprovedExcerpt("Private sample.", PodcastTests.Settings("openai")), default); Assert.True(await auditions.RemoveAsync(audio));
        var history = await new HostedAuditionHistory(test.Workspace).RecentAsync(); Assert.Single(history); Assert.Equal("received", history[0].State); Assert.DoesNotContain("Private sample", JsonSerializer.Serialize(history));
        Assert.Empty(await new AuditionOwnershipStore(test.Workspace).LoadAsync());
    }
}
