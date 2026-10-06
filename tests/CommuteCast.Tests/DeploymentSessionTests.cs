using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.ComponentModel;
using System.Text.Json;

namespace CommuteCast.Tests;

public class DeploymentSessionTests
{
    private static async Task<string> InventoryAsync(string root)
    {
        var files = new List<string>();
        foreach (var path in Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            files.Add(Path.GetRelativePath(root, path) + ":" + await Workspace.HashFileAsync(path));
        return string.Join("\n", files);
    }
    [Fact] public async Task RepeatedQueueReviewNeverCreatesOrChangesDatabaseSidecars()
    {
        using var test = new TestWorkspace(); await InstallationTests.SeedAsync(test, "Private narration");
        var source = await InstallationTests.PackageAsync(test, "readonly"); var before = await InventoryAsync(test.Workspace.Root);
        var session = new DeploymentSession(source, Path.Combine(test.Parent, "program"), test.Workspace.Root, new Runtime());
        Assert.Equal(1, (await session.ReviewAsync()).Narrations); Assert.Equal(1, (await session.ReviewAsync()).Narrations);
        Assert.Equal(before, await InventoryAsync(test.Workspace.Root));
        var journal = Path.Combine(test.Workspace.Root, "queue.db-wal"); await File.WriteAllTextAsync(journal, "Uncheckpointed journal must be kept"); before = await InventoryAsync(test.Workspace.Root);
        Assert.Null((await session.ReviewAsync()).Narrations); Assert.Equal(before, await InventoryAsync(test.Workspace.Root));
    }
    private sealed class Runtime : ISetupRuntime
    {
        public int Commands; public Func<CancellationToken, Task<ProcessResult>>? Pending;
        public SetupHost InspectHost() => new(true, "X64", new(10, 0, 26200), "10.0.12", 4, 8UL * 1073741824, null, false);
        public Task<ProcessResult> RunAsync(string tool, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
        { Commands++; return Pending is null ? Task.FromException<ProcessResult>(new Win32Exception(2)) : Pending(ct); }
        public Task<ProviderInfo> ProbeSpeechAsync(string engine, CancellationToken ct) => throw new InvalidOperationException("Missing Docker must never inspect a provider.");
    }
    private sealed class Observer : IInstallationObserver
    { public Task ReachedAsync(InstallationCheckpoint checkpoint, string? item, CancellationToken ct) => checkpoint == InstallationCheckpoint.Prepared ? throw new IOException("Interrupted setup") : Task.CompletedTask; }
    [Fact] public async Task FreshReviewAndMissingPrerequisiteCheckDoNotCreatePrivateOrInstallationState()
    {
        using var test = new TestWorkspace(); var source = await InstallationTests.PackageAsync(test, "fresh"); var root = Path.Combine(test.Parent, "not-created"); var privateRoot = Path.Combine(test.Parent, "private-not-created"); var runtime = new Runtime(); var session = new DeploymentSession(source, root, privateRoot, runtime);
        var review = await session.ReviewAsync(); Assert.Null(review.Overview.Owner); Assert.Equal(0, review.LocalFiles); Assert.Equal(0, review.Narrations); Assert.DoesNotContain("Saved draft", review.Summary);
        var setup = await session.CheckSetupAsync(review); Assert.False(setup.SelectedSpeechAvailable); Assert.Equal(SetupStatus.ReviewRequired, setup.Checks.Single(c => c.Id == "policy").Status);
        Assert.False(Directory.Exists(root)); Assert.False(Directory.Exists(privateRoot));
        await Assert.ThrowsAsync<IOException>(() => session.ApplyAsync(review, DeploymentAction.Install, false)); Assert.False(Directory.Exists(root)); Assert.False(Directory.Exists(privateRoot));
        var result = await session.ApplyAsync(review, DeploymentAction.Install, true); Assert.NotNull(result.Installation); Assert.NotNull(result.Setup); Assert.True(File.Exists(result.Installation.Executable));
        await Assert.ThrowsAsync<IOException>(() => session.ApplyAsync(review, DeploymentAction.Install, true));
    }
    [Fact] public async Task NativeWorkflowUpdateRollbackAndUndoPreserveFrozenQueuedWork()
    {
        using var test = new TestWorkspace(); var original = await InstallationTests.SeedAsync(test, "Original private narration"); var a = await InstallationTests.PackageAsync(test, "a"); var b = await InstallationTests.PackageAsync(test, "b"); var root = Path.Combine(test.Parent, "program");
        var first = new DeploymentSession(a, root, test.Workspace.Root, new Runtime()); var initial = await first.ApplyAsync(await first.ReviewAsync(), DeploymentAction.Install, true);
        var next = new DeploymentSession(b, root, test.Workspace.Root, new Runtime()); var updated = await next.ApplyAsync(await next.ReviewAsync(), DeploymentAction.Install, true); Assert.NotEqual(initial.Installation!.State.CurrentPackageId, updated.Installation!.State.CurrentPackageId);
        await InstallationTests.SeedAsync(test, "Queued later"); var review = await next.ReviewAsync(); Assert.Equal(2, review.Narrations); Assert.DoesNotContain("Queued later", review.Summary);
        var rollback = await next.ApplyAsync(review, DeploymentAction.Rollback, true); Assert.Equal(initial.Installation.State.CurrentPackageId, rollback.Installation!.State.CurrentPackageId);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(Assert.Single(await new SqliteJobStore(test.Workspace).LoadAsync())));
        var undo = await next.ApplyAsync(await next.ReviewAsync(), DeploymentAction.Rollback, true); Assert.Equal(updated.Installation.State.CurrentPackageId, undo.Installation!.State.CurrentPackageId);
        Assert.Equal(2, (await new SqliteJobStore(test.Workspace).LoadAsync()).Count); Assert.Equal(undo.Installation.Executable, await next.FindLaunchAsync(await next.ReviewAsync()));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task UninstallUsesTheReviewedScopeAndKeepsSeparateModelsExportsAndUnknownRootFiles(bool remove)
    {
        using var test = new TestWorkspace(); await InstallationTests.SeedAsync(test, "Retained source"); var source = await InstallationTests.PackageAsync(test, "package"); var root = Path.Combine(test.Parent, "program"); var session = new DeploymentSession(source, root, test.Workspace.Root, new Runtime()); await session.ApplyAsync(await session.ReviewAsync(), DeploymentAction.Install, true);
        var unknown = Path.Combine(test.Workspace.Root, "keep.txt"); var model = Path.Combine(test.Workspace.Root, "provisioning-models", "model.txt"); var export = Path.Combine(test.Destination, "external.mp3"); Directory.CreateDirectory(Path.GetDirectoryName(model)!); foreach (var path in new[] { unknown, model, export }) await File.WriteAllTextAsync(path, "Keep this separate file");
        var review = await session.ReviewAsync(); Assert.True(review.LocalFiles > 0); var result = await session.ApplyAsync(review, remove ? DeploymentAction.UninstallRemove : DeploymentAction.UninstallRetain, true);
        Assert.Null(result.Installation!.State.CurrentPackageId); Assert.Equal(!remove, File.Exists(Path.Combine(test.Workspace.Root, "queue.db"))); Assert.Equal(!remove, Directory.Exists(Path.Combine(test.Workspace.Root, "backups")));
        foreach (var path in new[] { unknown, model, export }) Assert.Equal("Keep this separate file", await File.ReadAllTextAsync(path));
        Assert.Null((await session.ReviewAsync()).Overview.State!.CurrentPackageId);
    }
    [Theory] [InlineData("source")] [InlineData("draft")] [InlineData("state")]
    public async Task AChangedReviewCannotActivateOrRemoveDifferentState(string change)
    {
        using var test = new TestWorkspace(); await InstallationTests.SeedAsync(test, "Current state"); var source = await InstallationTests.PackageAsync(test, "package"); var root = Path.Combine(test.Parent, "program"); var session = new DeploymentSession(source, root, test.Workspace.Root, new Runtime()); await session.ApplyAsync(await session.ReviewAsync(), DeploymentAction.Install, true);
        var review = await session.ReviewAsync(); var pointer = Path.Combine(root, "installation.json");
        if (change == "source") await File.AppendAllTextAsync(Path.Combine(source, "README.md"), "Changed package");
        if (change == "draft") await new DraftStore(test.Workspace).SaveAsync(new("Later", "Later source"));
        if (change == "state") await File.AppendAllTextAsync(pointer, " ");
        var before = await Workspace.HashFileAsync(pointer); var database = await Workspace.HashFileAsync(Path.Combine(test.Workspace.Root, "queue.db"));
        await Assert.ThrowsAsync<IOException>(() => session.ApplyAsync(review, DeploymentAction.UninstallRemove, true));
        Assert.Equal(before, await Workspace.HashFileAsync(pointer)); Assert.Equal(database, await Workspace.HashFileAsync(Path.Combine(test.Workspace.Root, "queue.db"))); Assert.True(File.Exists(Path.Combine(test.Workspace.Root, "draft.json")));
        await Assert.ThrowsAsync<IOException>(() => session.ApplyAsync(review, DeploymentAction.Install, true));
    }
    [Fact] public async Task AnotherReviewInvalidatesAnEarlierDialogEvenForTheSamePackage()
    {
        using var test = new TestWorkspace(); var source = await InstallationTests.PackageAsync(test, "package"); var session = new DeploymentSession(source, Path.Combine(test.Parent, "program"), test.Workspace.Root, new Runtime()); var old = await session.ReviewAsync(); await session.ReviewAsync();
        await Assert.ThrowsAsync<IOException>(() => session.ApplyAsync(old, DeploymentAction.Install, true));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task InterruptedDeploymentIsReviewedWithoutImplicitRecoveryThenRecoveredExplicitly(bool changeJournal)
    {
        using var test = new TestWorkspace(); var original = await InstallationTests.SeedAsync(test, "Keep original"); var a = await InstallationTests.PackageAsync(test, "a"); var b = await InstallationTests.PackageAsync(test, "b"); var root = Path.Combine(test.Parent, "program"); var install = new Installation(root);
        using (var lease = WorkspaceLease.Acquire(test.Workspace)) { await install.ActivateAsync(lease, a); await Assert.ThrowsAsync<IOException>(() => install.ActivateAsync(lease, b, new Observer())); }
        var journal = Path.Combine(root, "deployment.pending.json"); var before = await Workspace.HashFileAsync(journal); var session = new DeploymentSession(b, root, test.Workspace.Root, new Runtime()); var review = await session.ReviewAsync(); Assert.True(review.Overview.PendingRecovery); Assert.Equal(before, await Workspace.HashFileAsync(journal));
        await Assert.ThrowsAsync<IOException>(() => session.ApplyAsync(review, DeploymentAction.Install, true));
        if (changeJournal)
        {
            await File.AppendAllTextAsync(journal, " "); var changed = await Workspace.HashFileAsync(journal);
            await Assert.ThrowsAsync<IOException>(() => session.ApplyAsync(review, DeploymentAction.Recover, true));
            Assert.Equal(changed, await Workspace.HashFileAsync(journal)); review = await session.ReviewAsync();
        }
        Assert.True((await session.ApplyAsync(review, DeploymentAction.Recover, true)).Recovered); Assert.False(File.Exists(journal)); Assert.Equal(original.Id, Assert.Single(await new SqliteJobStore(test.Workspace).LoadAsync()).Id);
    }
    [Fact] public async Task CorruptQueueCanBeReviewedButAnInstallNeverResetsItsOriginalBytes()
    {
        using var test = new TestWorkspace(); await InstallationTests.SeedAsync(test, "Healthy state"); var a = await InstallationTests.PackageAsync(test, "a"); var b = await InstallationTests.PackageAsync(test, "b"); var root = Path.Combine(test.Parent, "program"); var initial = new DeploymentSession(a, root, test.Workspace.Root, new Runtime()); await initial.ApplyAsync(await initial.ReviewAsync(), DeploymentAction.Install, true);
        byte[] corrupt = [0, 1, 4, 9]; await File.WriteAllBytesAsync(Path.Combine(test.Workspace.Root, "queue.db"), corrupt); var next = new DeploymentSession(b, root, test.Workspace.Root, new Runtime()); var review = await next.ReviewAsync(); Assert.Null(review.Narrations); Assert.Contains("verification unavailable", review.QueueStatus);
        await Assert.ThrowsAsync<IOException>(() => next.ApplyAsync(review, DeploymentAction.Install, true)); Assert.Equal(corrupt, await File.ReadAllBytesAsync(Path.Combine(test.Workspace.Root, "queue.db")));
    }
    [Fact] public async Task SetupCannotRebindAnOwnedInstallationToAnotherPrivateFolder()
    {
        using var test = new TestWorkspace(); var source = await InstallationTests.PackageAsync(test, "package"); var root = Path.Combine(test.Parent, "program"); var session = new DeploymentSession(source, root, test.Workspace.Root, new Runtime()); await session.ApplyAsync(await session.ReviewAsync(), DeploymentAction.Install, true);
        var other = Path.Combine(test.Parent, "other-private"); await Assert.ThrowsAsync<IOException>(() => new DeploymentSession(source, root, other, new Runtime()).ReviewAsync()); Assert.False(Directory.Exists(other));
    }
    [Fact] public async Task InvalidOrUnownedTargetCannotBecomeASetupDestination()
    {
        using var test = new TestWorkspace(); var source = await InstallationTests.PackageAsync(test, "package"); var root = Path.Combine(test.Parent, "unowned"); Directory.CreateDirectory(root); var existing = Path.Combine(root, "keep.txt"); await File.WriteAllTextAsync(existing, "Unrelated data");
        await Assert.ThrowsAsync<IOException>(() => new DeploymentSession(source, root, Path.Combine(test.Parent, "unused"), new Runtime()).ReviewAsync()); Assert.Equal("Unrelated data", await File.ReadAllTextAsync(existing)); Assert.False(Directory.Exists(Path.Combine(test.Parent, "unused")));
        await Assert.ThrowsAsync<IOException>(() => new DeploymentSession(source, Path.Combine(test.Workspace.Root, "program"), test.Workspace.Root, new Runtime()).ReviewAsync());
    }
    [Fact] public async Task InstallerCannotRemoveItsOwnSourcePackage()
    {
        using var test = new TestWorkspace(); var source = await InstallationTests.PackageAsync(test, "package"); var root = Path.Combine(test.Parent, "program"); var session = new DeploymentSession(source, root, test.Workspace.Root, new Runtime()); var result = await session.ApplyAsync(await session.ReviewAsync(), DeploymentAction.Install, true);
        var ownPackage = Directory.GetParent(Path.GetDirectoryName(result.Installation!.Executable)!)!.FullName; var own = new DeploymentSession(ownPackage, root, test.Workspace.Root, new Runtime()); var review = await own.ReviewAsync();
        await Assert.ThrowsAsync<IOException>(() => own.ApplyAsync(review, DeploymentAction.UninstallRetain, true)); Assert.True(File.Exists(result.Installation.Executable));
    }
    [Fact] public async Task CancellationConsumesConfirmationAndConcurrentWorkCannotEnterSetup()
    {
        using var test = new TestWorkspace(); var source = await InstallationTests.PackageAsync(test, "package"); var runtime = new Runtime(); var session = new DeploymentSession(source, Path.Combine(test.Parent, "program"), test.Workspace.Root, runtime); var review = await session.ReviewAsync(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ApplyAsync(review, DeploymentAction.Install, true, cancel.Token)); await Assert.ThrowsAsync<IOException>(() => session.ApplyAsync(review, DeploymentAction.Install, true));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var resume = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously); runtime.Pending = _ => { entered.TrySetResult(); return resume.Task; };
        var applying = session.ApplyAsync(await session.ReviewAsync(), DeploymentAction.Install, true); await entered.Task;
        await Assert.ThrowsAsync<IOException>(() => session.ReviewAsync()); await Assert.ThrowsAsync<IOException>(() => session.ApplyAsync(review, DeploymentAction.Install, true));
        resume.SetResult(new(1, "", "")); await applying;
    }
}
