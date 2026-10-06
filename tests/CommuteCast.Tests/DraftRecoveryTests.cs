using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public class DraftRecoveryTests
{
    [Fact] public async Task UnicodeDraftRoundTripRetainsTheEntireSource()
    {
        using var test = new TestWorkspace(); var store = new DraftStore(test.Workspace); var draft = new Draft("A thought 😀", new string('x', 250000) + "\r\nA final café paragraph.");
        await store.SaveAsync(draft); Assert.Equal(draft, await store.LoadAsync()); Assert.False(Directory.Exists(Path.Combine(test.Workspace.Root, "recovery")));
    }
    [Theory] [InlineData("not JSON")] [InlineData("[]")] [InlineData("[null,\"source\"]")] [InlineData("[\"title\"]")]
    public async Task UnreadableDraftIsPreservedBeforeNewEditsAreSaved(string original)
    {
        using var test = new TestWorkspace(); var path = Path.Combine(test.Workspace.Root, "draft.json"); await File.WriteAllTextAsync(path, original); var store = new DraftStore(test.Workspace);
        await Assert.ThrowsAsync<IOException>(() => store.LoadAsync()); Assert.Equal(original, await File.ReadAllTextAsync(path));
        var preserved = await store.SaveAsync(new("Recovered title", "New explicitly edited source")); Assert.NotNull(preserved); Assert.Equal(original, await File.ReadAllTextAsync(preserved));
        Assert.Equal(new Draft("Recovered title", "New explicitly edited source"), await store.LoadAsync());
        Assert.Null(await store.SaveAsync(new("Next title", "Next edit"))); Assert.Single(Directory.GetFiles(Path.GetDirectoryName(preserved)!, "*.json"));
    }
    [Fact] public async Task CancelledDraftSavePreservesTheCurrentDraft()
    {
        using var test = new TestWorkspace(); var store = new DraftStore(test.Workspace); var original = new Draft("Keep", "Original draft"); await store.SaveAsync(original);
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(new("Discard", "Not accepted"), cancel.Token));
        Assert.Equal(original, await store.LoadAsync());
    }
}
