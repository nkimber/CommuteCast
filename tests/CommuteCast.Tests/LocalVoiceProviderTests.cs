using System.Text.Json;
using System.Text.Json.Nodes;
using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public partial class ProviderContractTests
{
    [Theory] [InlineData("kokoro")] [InlineData("piper")]
    public async Task LocalRecipesReachTheProviderAndLegacyRequestsKeepTheirOriginalShape(string engine)
    {
        using var fixture = new Fixture(engine);
        fixture.Http.Health = _ => { var health = JsonNode.Parse(fixture.Health())!; health["localVoiceContract"] = 1; if (engine == "kokoro") health["voices"] = new JsonArray("af_heart", "af_bella"); return health.ToJsonString(); };
        var recipe = engine == "kokoro" ? new LocalVoiceOptions(BlendVoice: "af_bella") : new LocalVoiceOptions(NoiseScale: .55, NoiseWidth: .65);
        using var provider = fixture.Provider(); await provider.SynthesizeAsync(fixture.Settings with { LocalVoice = recipe }, "Hello.", fixture.Output, default);
        using var request = JsonDocument.Parse(fixture.Http.Bodies.Single());
        Assert.Equal(recipe.NaturalPhrasing, request.RootElement.GetProperty("localVoice").GetProperty("naturalPhrasing").GetBoolean());
        Assert.Equal(recipe.BlendVoice, request.RootElement.GetProperty("localVoice").GetProperty("blendVoice").GetString());
    }
    [Fact] public async Task UnsupportedQualityContractSendsNoSourceOrReservation()
    {
        using var fixture = new Fixture(); using var provider = fixture.Provider();
        var error = await Assert.ThrowsAsync<IOException>(() => provider.SynthesizeAsync(fixture.Settings with { LocalVoice = new() }, "Private source.", fixture.Output, default));
        Assert.Contains("voice-quality update", error.Message); Assert.Equal(0, fixture.Http.Posts); Assert.Empty(fixture.Http.Bodies);
    }
    [Theory] [InlineData("kokoro")] [InlineData("piper")]
    public async Task NaturalPodcastRetryReusesFirstTurnAndRetainsCapturedSpeakerRecipes(string engine)
    {
        using var fixture = new Fixture(engine);
        var second = engine == "kokoro" ? "am_michael" : "en_US-amy-medium";
        fixture.Http.Health = _ => { var health = JsonNode.Parse(fixture.Health())!; health["localVoiceContract"] = 1; health["voices"] = new JsonArray(fixture.Settings.Voice, second, engine == "kokoro" ? "af_bella" : "en_US-bryce-medium"); return health.ToJsonString(); };
        fixture.Http.Speech = (index, _) => Task.FromResult(index == 2 ? new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Conflict) : fixture.Audio());
        var firstRecipe = engine == "kokoro" ? new LocalVoiceOptions(BlendVoice: "af_bella") : new LocalVoiceOptions(NoiseScale: .55, NoiseWidth: .65);
        var episode = new PodcastEpisode(PodcastDefaults.Formats[0], [new("Alex", "Host", "", "", fixture.Settings.Voice, LocalVoice: firstRecipe), new("Casey", "Host", "", "", second, LocalVoice: new(GainDb: -2))]);
        var settings = fixture.Settings with { LocalVoice = new() }; var source = "Alex: Hello.\nCasey: Yes.";
        var prepared = PodcastScript.Prepare(source, episode, settings);
        var job = new Job { Source = source, Prepared = prepared.Prepared, Chunks = prepared.Units, Settings = settings, Episode = episode, Destination = fixture.Test.Destination };
        NaturalChunker.ConfigureNew(job);
        var store = new SqliteJobStore(fixture.Test.Workspace); using var provider = new SpeechProviderService(fixture.Test.Workspace, new(), fixture.Provider());
        await using var queue = new QueueCoordinator(fixture.Test.Workspace, store, provider, new(new()), new(fixture.Test.Workspace, store)); await queue.InitializeAsync(); await queue.AddAsync(job);
        async Task<Job> Settle()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (queue.Snapshot().Single().Stage is not (JobStage.Failed or JobStage.Exported)) await Task.Delay(50, deadline.Token);
            return queue.Snapshot().Single();
        }
        var failed = await Settle(); Assert.Equal(JobStage.Failed, failed.Stage); var receipt = Assert.Single(failed.Receipts);
        Assert.Equal(firstRecipe, failed.Episode!.Speakers[0].LocalVoice);
        await queue.RetryAsync(job.Id); var done = await Settle(); Assert.True(done.Stage == JobStage.Exported, done.Error);
        Assert.Equal(receipt, done.Receipts[0]); Assert.Equal(job.Fingerprint, done.Fingerprint); Assert.Equal(3, fixture.Http.Posts);
        var bodies = fixture.Http.Bodies.Select(b => JsonSerializer.Deserialize<JsonElement>(b)).ToArray();
        Assert.Equal(firstRecipe.BlendVoice, bodies[0].GetProperty("localVoice").GetProperty("blendVoice").GetString());
        Assert.Equal(second, bodies[2].GetProperty("voice").GetString()); Assert.Equal(-2, bodies[2].GetProperty("localVoice").GetProperty("gainDb").GetDouble());
        Assert.Single(Directory.GetFiles(fixture.Test.Destination, "*.mp3"));
    }
}
