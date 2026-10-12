using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Text.Json;

namespace CommuteCast.Tests;

public class GenerationPromptTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ExactPromptRoundTripsInDraftWithAndWithoutVersion(bool version)
    {
        using var test = new TestWorkspace(); var store = new DraftStore(test.Workspace);
        var prompt = "Write a story about Café 😀.\r\nKeep these exact instructions.\n";
        var draft = new Draft("Story", "Generated script", Version: version ? new(new("root", "parent", "Story", 2, Job.Hash("Generated script")), new Job().Settings) : null, GenerationPrompt: prompt);
        await store.SaveAsync(draft); Assert.Equal(draft, await store.LoadAsync());
        await store.SaveAsync(new("New script", "No known prompt")); Assert.Null((await store.LoadAsync()).GenerationPrompt);
    }
    [Fact] public async Task AddingPromptRetainsAudioAndScriptAndCanRemoveIt()
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace);
        var job = new Job { Title = "Story", Source = "Script", Stage = JobStage.Exported, ExportCommitted = true, FinalHash = "saved hash" };
        await store.SaveAsync(job);
        await using var queue = new QueueCoordinator(test.Workspace, store, new UnusedProvider(), new(new()), new(test.Workspace, store)) { Paused = true };
        await queue.InitializeAsync(); var fingerprint = job.Fingerprint;
        var prompt = "Exact prompt 😀\r\nLine two.";
        await queue.UpdateGenerationPromptAsync(job.Id, prompt);
        var saved = Assert.Single(await store.LoadAsync()); Assert.Equal(prompt, saved.GenerationPrompt);
        Assert.Equal("Script", saved.Source); Assert.True(saved.ExportCommitted); Assert.Equal("saved hash", saved.FinalHash); Assert.Equal(fingerprint, saved.Fingerprint);
        await queue.UpdateGenerationPromptAsync(job.Id, ""); Assert.Null(Assert.Single(await store.LoadAsync()).GenerationPrompt);
        await Assert.ThrowsAsync<ArgumentException>(() => queue.UpdateGenerationPromptAsync(job.Id, new string('x', 100001)));
        Assert.Null(Assert.Single(await store.LoadAsync()).GenerationPrompt);
        Assert.Null(JsonSerializer.Deserialize<Job>("{}")!.GenerationPrompt);
    }
    private sealed class UnusedProvider : ISpeechProvider
    {
        public Task<ProviderInfo> ReadyAsync(string engine, CancellationToken ct) => throw new InvalidOperationException();
        public Task SynthesizeAsync(NarrationSettings settings, string text, string output, CancellationToken ct) => throw new InvalidOperationException();
    }
    [Fact] public async Task PersistenceFailureLeavesThePreviousPromptInMemoryAndOnDisk()
    {
        using var test = new TestWorkspace(); var durable = new SqliteJobStore(test.Workspace);
        var job = new Job { Stage = JobStage.Failed, GenerationPrompt = "Original prompt" }; await durable.SaveAsync(job);
        var store = new FailingStore(durable);
        await using var queue = new QueueCoordinator(test.Workspace, store, new UnusedProvider(), new(new()), new(test.Workspace, store)) { Paused = true };
        await queue.InitializeAsync(); store.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => queue.UpdateGenerationPromptAsync(job.Id, "Replacement"));
        Assert.Equal("Original prompt", Assert.Single(queue.Snapshot()).GenerationPrompt);
        Assert.Equal("Original prompt", Assert.Single(await durable.LoadAsync()).GenerationPrompt);
    }
    private sealed class FailingStore(IJobStore inner) : IJobStore
    {
        public bool Fail { get; set; }
        public Task SaveAsync(Job job, CancellationToken ct = default) => Fail ? throw new IOException("Injected prompt save failure") : inner.SaveAsync(job, ct);
        public Task<IReadOnlyList<Job>> LoadAsync(CancellationToken ct = default) => inner.LoadAsync(ct);
        public Task<IReadOnlyDictionary<string, bool>> RequestDeletionAsync(IReadOnlyList<string> ids, bool deleteExports, CancellationToken ct = default) => inner.RequestDeletionAsync(ids, deleteExports, ct);
        public Task SaveQueueOrderAsync(IReadOnlyDictionary<string, long> positions, CancellationToken ct = default) => inner.SaveQueueOrderAsync(positions, ct);
        public Task RemoveAsync(string id, CancellationToken ct = default) => inner.RemoveAsync(id, ct);
    }
}
