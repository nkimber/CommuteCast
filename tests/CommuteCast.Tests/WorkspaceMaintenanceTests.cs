using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Text.Json;

namespace CommuteCast.Tests;

public class WorkspaceMaintenanceTests
{
    private sealed class Observer(Func<RestoreCheckpoint, Task> action) : IRestoreObserver
    { public Task ReachedAsync(RestoreCheckpoint checkpoint, string? item, CancellationToken ct) => action(checkpoint); }
    [Fact] public async Task InterruptedReplacementSettlesRecoveryBeforeAnotherOperationCanRun()
    {
        using var test = new TestWorkspace(); await SeedAsync(test, "Old backup"); using var lease = WorkspaceLease.Acquire(test.Workspace);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new WorkspaceMaintenance(lease, new Observer(async stage => { if (stage == RestoreCheckpoint.OldMoved) { reached.SetResult(); await resume.Task; throw new IOException("Interrupted replacement"); } }));
        var review = await session.CreateAsync(); await SeedAsync(test, "Current retained state"); var before = await Workspace.HashFileAsync(Path.Combine(test.Workspace.Root, "queue.db"));
        var restore = session.RestoreAsync(review, true); await reached.Task;
        await Assert.ThrowsAsync<IOException>(() => session.CreateAsync()); await Assert.ThrowsAsync<IOException>(() => session.ReviewAsync(review.Directory)); await Assert.ThrowsAsync<IOException>(() => session.RecoverAsync());
        resume.SetResult(); await Assert.ThrowsAsync<IOException>(() => restore);
        Assert.Equal(before, await Workspace.HashFileAsync(Path.Combine(test.Workspace.Root, "queue.db"))); Assert.Equal("Current retained state", (await new DraftStore(test.Workspace).LoadAsync()).Source);
        Assert.False(await session.RecoverAsync()); Assert.Equal(2, (await session.ReviewAsync((await session.CreateAsync()).Directory)).Jobs);
    }
    private static async Task SeedAsync(TestWorkspace test, string text)
    {
        await new SqliteJobStore(test.Workspace).SaveAsync(new Job { Source = text, Title = "Synthetic", Prepared = TextPreparation.Prepare(text), Stage = JobStage.Queued });
        await new DraftStore(test.Workspace).SaveAsync(new("Private draft", text));
        await test.Workspace.SaveSettingsAsync(new() { Engine = "piper", QueuePaused = true });
    }
    [Theory] [InlineData("partial")] [InlineData("oversize")]
    public async Task IncompleteOrUnboundedSelectionIsRefusedBeforeItCanBecomeRestorable(string kind)
    {
        using var test = new TestWorkspace(); await SeedAsync(test, "Private data"); using var lease = WorkspaceLease.Acquire(test.Workspace); var session = new WorkspaceMaintenance(lease); var review = await session.CreateAsync();
        var path = review.Directory;
        if (kind == "partial") { Directory.Move(path, path + ".partial"); path += ".partial"; }
        else { await using var manifest = new FileStream(Path.Combine(path, "manifest.json"), FileMode.Create); manifest.SetLength(32 * 1048576L + 1); }
        await Assert.ThrowsAsync<IOException>(() => session.ReviewAsync(path)); await Assert.ThrowsAsync<IOException>(() => session.RestoreAsync(review, true));
        Assert.Equal("Private data", (await new DraftStore(test.Workspace).LoadAsync()).Source);
    }
    [Fact] public async Task ReviewedRestorePreservesPreviousStateAndRequiresExplicitConsent()
    {
        using var test = new TestWorkspace(); await SeedAsync(test, "Original snapshot"); using var lease = WorkspaceLease.Acquire(test.Workspace); var session = new WorkspaceMaintenance(lease);
        var review = await session.CreateAsync(); Assert.Equal(1, review.Jobs); Assert.Contains("1 narration records", review.Summary); Assert.DoesNotContain("Original snapshot", review.Summary);
        await SeedAsync(test, "Later private state"); var later = await new DraftStore(test.Workspace).LoadAsync();
        await Assert.ThrowsAsync<IOException>(() => session.RestoreAsync(review, false)); Assert.Equal(later, await new DraftStore(test.Workspace).LoadAsync());
        var result = await session.RestoreAsync(review, true); Assert.Equal("Original snapshot", (await new DraftStore(test.Workspace).LoadAsync()).Source);
        Assert.Single(await new SqliteJobStore(test.Workspace).LoadAsync());
        Assert.True(File.Exists(Path.Combine(result.PreviousState, "draft.json"))); Assert.Equal(2, (await new SqliteJobStore(new Workspace(result.PreviousState)).LoadAsync()).Count);
        await Assert.ThrowsAsync<IOException>(() => session.RestoreAsync(review, true));
    }
    [Theory] [InlineData("manifest")] [InlineData("file")]
    public async Task ChangedReviewedBackupCannotReplaceCurrentPrivateData(string change)
    {
        using var test = new TestWorkspace(); await SeedAsync(test, "Original"); using var lease = WorkspaceLease.Acquire(test.Workspace); var session = new WorkspaceMaintenance(lease); var review = await session.CreateAsync();
        await SeedAsync(test, "Keep later state"); var before = await Workspace.HashFileAsync(Path.Combine(test.Workspace.Root, "queue.db"));
        if (change == "manifest") await File.WriteAllTextAsync(Path.Combine(review.Directory, "manifest.json"), JsonSerializer.Serialize(review.Manifest with { CreatedUtc = review.Manifest.CreatedUtc.AddDays(1) }));
        else await File.WriteAllTextAsync(Path.Combine(review.Directory, "draft.json"), "Changed backup content");
        await Assert.ThrowsAsync<IOException>(() => session.RestoreAsync(review, true)); Assert.Equal(before, await Workspace.HashFileAsync(Path.Combine(test.Workspace.Root, "queue.db")));
        Assert.Equal("Keep later state", (await new DraftStore(test.Workspace).LoadAsync()).Source);
        Assert.False(File.Exists(Path.Combine(test.Workspace.Root, "recovery", "restore.pending.json")));
    }
    [Fact] public async Task NewReviewInvalidatesOldConfirmationAndFailedReviewCannotLeaveAStaleSelection()
    {
        using var test = new TestWorkspace(); await SeedAsync(test, "Source"); using var lease = WorkspaceLease.Acquire(test.Workspace); var session = new WorkspaceMaintenance(lease);
        var old = await session.CreateAsync(); var next = await session.ReviewAsync(old.Directory);
        await Assert.ThrowsAsync<IOException>(() => session.RestoreAsync(old, true));
        await File.WriteAllTextAsync(Path.Combine(old.Directory, "draft.json"), "Bad backup");
        await Assert.ThrowsAsync<IOException>(() => session.ReviewAsync(old.Directory)); await Assert.ThrowsAsync<IOException>(() => session.RestoreAsync(next, true));
    }
    [Fact] public async Task CorruptCurrentStateCanBeRestoredWithoutResettingOrDroppingTheOriginalBytes()
    {
        using var test = new TestWorkspace(); await SeedAsync(test, "Healthy backup"); using var lease = WorkspaceLease.Acquire(test.Workspace); var session = new WorkspaceMaintenance(lease); var review = await session.CreateAsync();
        byte[] corrupt = [1, 3, 5, 7]; await File.WriteAllBytesAsync(Path.Combine(test.Workspace.Root, "queue.db"), corrupt); await File.WriteAllTextAsync(Path.Combine(test.Workspace.Root, "settings.json"), "Unreadable settings");
        var result = await session.RestoreAsync(review, true); Assert.Equal(corrupt, await File.ReadAllBytesAsync(Path.Combine(result.PreviousState, "queue.db")));
        Assert.Equal("Unreadable settings", await File.ReadAllTextAsync(Path.Combine(result.PreviousState, "settings.json"))); Assert.True((await test.Workspace.LoadSettingsAsync()).QueuePaused);
    }
    [Fact] public async Task CancelledPreparationKeepsLocalStateAndRequiresAnotherReview()
    {
        using var test = new TestWorkspace(); await SeedAsync(test, "Current data"); using var lease = WorkspaceLease.Acquire(test.Workspace); var session = new WorkspaceMaintenance(lease); var review = await session.CreateAsync();
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); var before = await Workspace.HashFileAsync(Path.Combine(test.Workspace.Root, "queue.db"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RestoreAsync(review, true, cancel.Token)); Assert.Equal(before, await Workspace.HashFileAsync(Path.Combine(test.Workspace.Root, "queue.db")));
        await Assert.ThrowsAsync<IOException>(() => session.RestoreAsync(review, true));
    }
    [Fact] public async Task DisposedLeaseRefusesEveryMaintenanceOperation()
    {
        using var test = new TestWorkspace(); await SeedAsync(test, "Keep data"); var lease = WorkspaceLease.Acquire(test.Workspace); var session = new WorkspaceMaintenance(lease); var review = await session.CreateAsync(); lease.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.CreateAsync()); await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ReviewAsync(review.Directory));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.RestoreAsync(review, true)); await Assert.ThrowsAsync<ObjectDisposedException>(() => session.RecoverAsync());
    }
}
