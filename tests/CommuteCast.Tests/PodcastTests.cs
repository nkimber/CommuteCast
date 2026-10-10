using System.Text.Json;
using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public class PodcastTests
{
    public static PodcastEpisode Episode(int count = 2, bool dialogue = true) => new(count == 2 ? PodcastDefaults.Formats[0] : PodcastDefaults.Formats[2],
        PodcastDefaults.Personalities.Take(count).Select((p, i) => new PodcastSpeaker(p.Name, count == 2 || i == 0 ? "Host" : "Guest", p.Expertise, p.Style, i == 0 ? "Kore" : "Puck")).ToArray(), dialogue);
    public static NarrationSettings Settings(string engine) => new(engine, SpeechProviders.Get(engine).DefaultVoice, 1, false, "", "", new(), Speech: SpeechProviders.IsHosted(engine) ? new(SpeechProviders.Get(engine).DefaultModel) : null);
    [Theory]
    [InlineData("Here is your podcast:\nAlex: Hello.\nCasey: Hi.")]
    [InlineData("Alex: Hello.\nUnknown: Hi.")]
    [InlineData("Alex: Hello.\nCasey: [laughs] Hi.")]
    [InlineData("Alex: Hello.\nCasey: https://example.com")]
    [InlineData("Alex: Hello.\nCasey:")]
    [InlineData("Alex: Hello.\nReferences: example")]
    [InlineData("Alex: Hello.")]
    public void InvalidLlmOutputFailsWithUsefulLineError(string source) => Assert.Throws<ArgumentException>(() => PodcastScript.Parse(source, Episode()));
    [Fact]
    public void PromptCarriesExactCastAndPureDialogueContract()
    {
        var text = PodcastScript.Prompt(new() { Topic = "Railways" }, Episode(), new(2026, 10, 10));
        Assert.Contains("Speaker: spoken words", text); Assert.Contains("Alex (Host)", text); Assert.Contains("Casey (Host)", text);
        Assert.Contains("fictional presenters", text); Assert.Contains("No preamble", text); Assert.DoesNotContain(NarrationPrompt.OutputInstructions, text);
    }
    [Theory] [InlineData("kokoro", 2)] [InlineData("openai", 2)] [InlineData("gemini", 1)] [InlineData("elevenlabs", 1)]
    public void ModelCapabilitiesDecideJointOrIndividualRendering(string engine, int blocks)
    {
        var episode = Episode(); var result = PodcastScript.Prepare("Alex: Hello there.\nCasey: That makes sense.", episode, Settings(engine));
        Assert.Equal(blocks, result.Units.Count); Assert.DoesNotContain("Alex:", result.Prepared.Script); Assert.DoesNotContain("Casey:", result.Prepared.Script);
        Assert.Equal("Alex: Hello there.\nCasey: That makes sense.", string.Concat(result.Prepared.Spans.Select(s => s.Original)));
        Assert.Equal(result.Prepared.Script, string.Concat(result.Prepared.Spans.Select(s => s.Narration)));
        var job = new Job { Source = "Alex: Hello there.\nCasey: That makes sense.", Episode = episode, Prepared = result.Prepared, Chunks = result.Units, Settings = Settings(engine), ChunkingVersion = PodcastScript.ChunkVersion, AudioContractVersion = PodcastScript.AudioVersion };
        PodcastScript.ValidateManifest(job); var fingerprint = job.Fingerprint;
        var copy = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(job))!; Assert.Equal(fingerprint, copy.Fingerprint);
        copy.Episode = episode with { Speakers = episode.Speakers.Select((s, i) => i == 0 ? s with { Voice = "Changed" } : s).ToArray() }; Assert.NotEqual(fingerprint, copy.Fingerprint);
        copy.Chunks[0] = copy.Chunks[0] with { Turns = [new("Unknown", copy.Chunks[0].Text)] }; Assert.Throws<ArgumentException>(() => PodcastScript.ValidateManifest(copy));
    }
    [Fact]
    public void FiveSpeakerGeminiPanelAndCustomPaceFallBackToSeparateTurns()
    {
        var episode = Episode(5); var source = string.Join('\n', episode.Speakers.Select(s => $"{s.Name}: A useful contribution."));
        Assert.Equal(5, PodcastScript.Prepare(source, episode, Settings("gemini")).Units.Count);
        var two = Episode() with { Speakers = Episode().Speakers.Select(s => s with { Speed = 1.1 }).ToArray() };
        Assert.Equal(2, PodcastScript.Prepare("Alex: Hi.\nCasey: Hello.", two, Settings("elevenlabs")).Units.Count);
    }
    [Fact]
    public async Task IncompleteEditedCastSurvivesDraftSaveAndBackup()
    {
        using var test = new TestWorkspace(); var episode = Episode() with { Speakers = Episode().Speakers.Select(s => s with { Name = "", Voice = "" }).ToArray() };
        var store = new DraftStore(test.Workspace); await store.SaveAsync(new("Title", "Draft", Podcast: new(true, episode)));
        var loaded = await store.LoadAsync(); Assert.True(loaded.Podcast!.Enabled); Assert.Equal(episode.Identity, loaded.Podcast.Episode.Identity);
        Assert.Throws<ArgumentException>(() => loaded.Podcast.Episode.Validate());
        using var lease = WorkspaceLease.Acquire(test.Workspace); var backup = await WorkspaceBackup.CreateAsync(lease); Assert.True(Directory.Exists(backup));
    }
    [Theory] [InlineData("null")] [InlineData("{\"Enabled\":true,\"Episode\":null}")]
    public async Task MalformedExtendedDraftIsRefusedWithoutOverwritingTheOriginal(string podcast)
    {
        using var test = new TestWorkspace(); var path = Path.Combine(test.Workspace.Root, "draft.json");
        var original = JsonSerializer.Serialize(new[] { "Title", "Script", "null", podcast }); await File.WriteAllTextAsync(path, original);
        await Assert.ThrowsAsync<IOException>(() => new DraftStore(test.Workspace).LoadAsync()); Assert.Equal(original, await File.ReadAllTextAsync(path));
    }
}
