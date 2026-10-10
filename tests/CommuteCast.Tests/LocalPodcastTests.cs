using System.Text.Json;
using System.Text.Json.Nodes;
using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public partial class ProviderContractTests
{
    [Theory] [InlineData("kokoro")] [InlineData("piper")]
    public async Task ProviderRouterRendersFiveLocalSpeakersWithTheirCapturedVoices(string engine)
    {
        using var fixture = new Fixture(engine); var local = fixture.Provider();
        var voices = engine == "kokoro" ? new[] { "af_heart", "am_adam", "af_bella", "am_fenrir", "bf_alice" } : new[] { "en_US-lessac-medium", "en_US-amy-medium", "en_US-bryce-medium", "en_US-joe-medium", "en_US-ljspeech-medium" };
        fixture.Http.Health = _ => { var health = JsonNode.Parse(fixture.Health())!; health["voices"] = JsonSerializer.SerializeToNode(voices); return health.ToJsonString(); };
        using var provider = new SpeechProviderService(fixture.Test.Workspace, new(), local);
        var episode = PodcastTests.Episode(5) with { Speakers = PodcastTests.Episode(5).Speakers.Select((s, i) => s with { Voice = voices[i] }).ToArray() };
        var source = string.Join('\n', episode.Speakers.Select(s => $"{s.Name}: This is my contribution.")); var prepared = PodcastScript.Prepare(source, episode, fixture.Settings);
        var job = new Job { Title = "Local panel", Source = source, Prepared = prepared.Prepared, Chunks = prepared.Units, Episode = episode, Settings = fixture.Settings, Destination = fixture.Test.Destination, AudioContractVersion = PodcastScript.AudioVersion, ChunkingVersion = PodcastScript.ChunkVersion };
        var store = new SqliteJobStore(fixture.Test.Workspace); await using var queue = new QueueCoordinator(fixture.Test.Workspace, store, provider, new(new()), new(fixture.Test.Workspace, store)); await queue.InitializeAsync(); await queue.AddAsync(job);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60)); while (queue.Snapshot().Single().Stage is not (JobStage.Exported or JobStage.Failed)) await Task.Delay(50, deadline.Token);
        var done = queue.Snapshot().Single(); Assert.True(done.Stage == JobStage.Exported, done.Error); Assert.Equal(5, fixture.Http.Posts);
        var requests = fixture.Http.Bodies.Select(b => JsonSerializer.Deserialize<JsonElement>(b)).ToArray(); Assert.Equal(voices, requests.Select(r => r.GetProperty("voice").GetString()));
        Assert.All(requests, r => Assert.DoesNotContain(":", r.GetProperty("text").GetString())); Assert.Null(done.SpeechAttempts); Assert.Single(Directory.GetFiles(fixture.Test.Destination, "*.mp3"));
    }
}

public class SpeechSecretTests
{
    [Fact]
    public void WindowsKeyRoundTripUsesAnIsolatedCredentialTarget()
    {
        var secrets = new WindowsSpeechSecrets("CommuteCast/Tests/" + Guid.NewGuid().ToString("N") + "/");
        try { Assert.Null(secrets.Get("openai")); secrets.Set("openai", "fixture-credential-value"); Assert.Equal("fixture-credential-value", secrets.Get("openai")); secrets.Remove("openai"); Assert.Null(secrets.Get("openai")); }
        finally { secrets.Remove("openai"); }
    }
}
