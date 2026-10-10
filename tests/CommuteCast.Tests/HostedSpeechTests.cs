using System.Net;
using System.Text;
using System.Text.Json;
using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public class HostedSpeechTests
{
    private sealed class Secrets : ISpeechSecrets
    { public string? Get(string provider) => "test-key-never-written"; public void Set(string provider, string key) { } public void Remove(string provider) { } }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { public int Posts; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { if (request.Method == HttpMethod.Post) Posts++; return respond(request); } }
    [Theory] [InlineData("tts-1", 9)] [InlineData("tts-1-hd", 9)] [InlineData("gpt-4o-mini-tts", 13)]
    public async Task ReadinessReturnsOnlyVoicesSupportedByTheCapturedModel(string model, int count)
    {
        var handler = new Handler(request => { Assert.EndsWith("/models/" + model, request.RequestUri!.AbsolutePath); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }); });
        using var http = new HttpClient(handler); using var provider = new HostedSpeechProvider(new Secrets(), http);
        var info = await provider.ReadyAsync("openai", new(model), default);
        Assert.Equal(count, info.Voices.Length); Assert.Contains("coral", info.Voices); Assert.Equal(model.StartsWith("gpt-"), info.Voices.Contains("marin")); Assert.Equal(0, handler.Posts);
    }
    [Theory] [InlineData("openai", "input", "Hello there.")] [InlineData("cartesia", "transcript", "Hello there.")] [InlineData("elevenlabs", "text", "Hello there.")]
    public void ProviderBodiesUseCapturedModelAndVerbatimText(string engine, string field, string expected)
    {
        var settings = PodcastTests.Settings(engine); var spec = HostedSpeechProvider.BuildRequest(settings, expected);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(spec.Body)); Assert.Equal(expected, json.RootElement.GetProperty(field).GetString());
        Assert.Equal(settings.Speech!.Model, json.RootElement.GetProperty(engine == "openai" ? "model" : "model_id").GetString());
        Assert.DoesNotContain("test-key", JsonSerializer.Serialize(spec.Body));
    }
    [Theory] [InlineData("elevenlabs")] [InlineData("gemini")]
    public void JointBodiesMapEveryTurnToCapturedVoice(string engine)
    {
        var episode = PodcastTests.Episode(); var turns = new[] { new PodcastTurn("Alex", "Hello."), new PodcastTurn("Casey", "Hi.") };
        var spec = HostedSpeechProvider.BuildRequest(PodcastTests.Settings(engine), "Hello.\nHi.", turns, episode); var body = JsonSerializer.Serialize(spec.Body);
        Assert.Contains("Kore", body); Assert.Contains("Puck", body); Assert.Contains("Hello.", body); Assert.Contains("Hi.", body);
        if (engine == "elevenlabs") Assert.Contains("text-to-dialogue", spec.Path); else { Assert.Contains("speech_metadata", body); Assert.Contains("interactions", spec.Path); }
    }
    [Fact]
    public async Task CloudTimeoutRecordsUncertainAttemptWithoutAutomaticDuplicatePost()
    {
        var handler = new Handler(_ => throw new HttpRequestException("Interrupted connection")); using var http = new HttpClient(handler); using var provider = new HostedSpeechProvider(new Secrets(), http);
        var job = new Job(); var saves = new List<string>(); using var output = new MemoryStream();
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.WriteAsync(job, PodcastTests.Settings("openai"), "Hello.", output, () => { saves.Add(JsonSerializer.Serialize(job.SpeechAttempts)); return Task.CompletedTask; }, default));
        Assert.Equal(1, handler.Posts); Assert.Equal("uncertain", Assert.Single(job.SpeechAttempts!).State); Assert.Contains("started", saves[0]); Assert.DoesNotContain("Hello.", string.Join("", saves));
    }
    [Fact]
    public async Task HttpClientTimeoutIsReportedAsTimeoutRatherThanUserCancellation()
    {
        var handler = new Handler(_ => throw new TaskCanceledException("Fixture timeout")); using var http = new HttpClient(handler); using var provider = new HostedSpeechProvider(new Secrets(), http); var job = new Job(); using var output = new MemoryStream();
        var error = await Assert.ThrowsAsync<TimeoutException>(() => provider.WriteAsync(job, PodcastTests.Settings("openai"), "Hello.", output, () => Task.CompletedTask, default));
        Assert.Contains("may have been billed", error.Message); Assert.Equal("uncertain", Assert.Single(job.SpeechAttempts!).State); Assert.Equal(1, handler.Posts);
    }
    [Fact]
    public async Task SuccessfulPcmResponseCarriesRequestReceiptAndValidWav()
    {
        var handler = new Handler(async request => { Assert.Equal("Bearer", request.Headers.Authorization!.Scheme); var body = await request.Content!.ReadAsStringAsync(); Assert.Contains("response_format", body); var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[48000]) }; response.Headers.Add("x-request-id", "request-123"); return response; });
        using var http = new HttpClient(handler); using var provider = new HostedSpeechProvider(new Secrets(), http); var job = new Job(); using var output = new MemoryStream();
        await provider.WriteAsync(job, PodcastTests.Settings("openai"), "Hello.", output, () => Task.CompletedTask, default);
        Assert.Equal(48000, WaveAudio.DataRegion(output).Length); Assert.Equal("request-123", Assert.Single(job.SpeechAttempts!).RequestId); Assert.Null(job.SpeechAttempts![0].BilledCharacters);
    }
    [Fact]
    public async Task QuotaFailureDoesNotEchoProviderBodyOrRetry()
    {
        var handler = new Handler(_ => { var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("private transcript and test-key") }; response.Headers.RetryAfter = new(TimeSpan.FromSeconds(10)); return Task.FromResult(response); });
        using var http = new HttpClient(handler); using var provider = new HostedSpeechProvider(new Secrets(), http); var job = new Job(); using var output = new MemoryStream();
        var error = await Assert.ThrowsAsync<IOException>(() => provider.WriteAsync(job, PodcastTests.Settings("openai"), "Private transcript.", output, () => Task.CompletedTask, default));
        Assert.DoesNotContain("private transcript", error.Message); Assert.Equal(1, handler.Posts); Assert.Equal("rejected", job.SpeechAttempts![0].State);
        Assert.True(job.SpeechAttempts![0].RetryAfterUtc > DateTimeOffset.UtcNow);
    }
}
