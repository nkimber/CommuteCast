using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Text.Json;

namespace CommuteCast.Tests;

public class NarrationVersionStorageTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task VersionDraftRoundTripsWithOrWithoutPodcast(bool podcast)
    {
        using var test = new TestWorkspace(); var store = new DraftStore(test.Workspace);
        var episode = new PodcastEpisode(PodcastDefaults.Formats[0], PodcastDefaults.Personalities.Take(2)
            .Select(p => new PodcastSpeaker(p.Name, "Host", p.Expertise, p.Style, "af_heart")).ToArray());
        var draft = new Draft("Version 2", "Original 😀 source", Podcast: podcast ? new(true, episode) : null,
            Version: new(new("root", "parent", "Original", 2, Job.Hash("Original 😀 source")), new Job().Settings));
        await store.SaveAsync(draft); var restored = await store.LoadAsync();
        Assert.Equal(draft.Version, restored.Version); Assert.Equal(draft.Source, restored.Source);
        Assert.Equal(draft.Podcast?.Episode.Identity, restored.Podcast?.Episode.Identity);
        Assert.Null(restored.PromptDraft);
        await store.SaveAsync(new("Unrelated", "New source")); Assert.Null((await store.LoadAsync()).Version);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task MalformedVersionDraftIsPreservedForRecovery(bool invalidSettings)
    {
        using var test = new TestWorkspace(); var store = new DraftStore(test.Workspace);
        var path = Path.Combine(test.Workspace.Root, "draft.json");
        var payload = invalidSettings ? JsonSerializer.Serialize(new VersionDraft(new("root", "parent", "Title", 2, Job.Hash("Source")), new Job().Settings with { Engine = "unknown" }))
            : "{\"Version\":null,\"Settings\":null}";
        var original = JsonSerializer.Serialize(new[] { "Title", "Source", "null", "null", payload });
        await File.WriteAllTextAsync(path, original);
        await Assert.ThrowsAsync<IOException>(() => store.LoadAsync());
        var preserved = await store.SaveAsync(new("Recovered", "New draft"));
        Assert.Equal(original, await File.ReadAllTextAsync(preserved!));
    }
    [Fact] public async Task VersionMetadataSurvivesJobStorageWithoutChangingAudioFingerprint()
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace);
        var job = new Job { Source = "Saved source", Prepared = TextPreparation.Prepare("Saved source") };
        var fingerprint = job.Fingerprint;
        job.Version = new("root", "parent", "Script title", 2, Job.Hash(job.Source));
        await store.SaveAsync(job); var restored = Assert.Single(await store.LoadAsync());
        Assert.Equal(job.Version, restored.Version); Assert.Equal(fingerprint, restored.Fingerprint);
        Assert.Null(JsonSerializer.Deserialize<Job>("{}")!.Version);
        var comment = Mp3Comments.Describe(restored);
        Assert.Contains("Narration version: 2", comment); Assert.Contains("original job ID: root", comment);
        Assert.Contains("Script compared with parent: unchanged", comment);
        Assert.DoesNotContain(job.Source, comment);
        restored.Source += " edited";
        Assert.Contains("Script compared with parent: edited", Mp3Comments.Describe(restored));
    }
}
