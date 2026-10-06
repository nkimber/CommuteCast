using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public class InstalledLauncherTests
{
    private sealed class Interrupt(InstallationCheckpoint checkpoint) : IInstallationObserver
    { public Task ReachedAsync(InstallationCheckpoint actual, string? item, CancellationToken ct) => actual == checkpoint ? throw new IOException("Synthetic launcher interruption") : Task.CompletedTask; }
    private sealed class InterruptRemoval : IInstallationObserver
    { public Task ReachedAsync(InstallationCheckpoint point, string? item, CancellationToken ct) => point == InstallationCheckpoint.ItemRemoved && item?.StartsWith("Package/", StringComparison.Ordinal) == true ? throw new IOException("Synthetic partial uninstall") : Task.CompletedTask; }
    private static async Task<string> PackageAsync(TestWorkspace test, string label)
    {
        var package = await InstallationTests.PackageAsync(test, label);
        var path = Path.Combine(package, InstalledLauncher.PackageFile.Replace('/', Path.DirectorySeparatorChar));
        File.Copy(Path.Combine(package, "app", "CommuteCast.Desktop.exe"), path);
        await using (var append = new FileStream(path, FileMode.Append)) await append.WriteAsync(System.Text.Encoding.UTF8.GetBytes(label));
        await ReleasePackage.SealAsync(package); return package;
    }
    [Fact] public async Task StableEntryFollowsUpdateAndRollbackAndStagesAnExternalVerifiedSetup()
    {
        using var test = new TestWorkspace(); await InstallationTests.SeedAsync(test, "Original queued work");
        var a = await PackageAsync(test, "a"); var b = await PackageAsync(test, "b"); var root = Path.Combine(test.Parent, "program"); var installation = new Installation(root);
        InstallationResult first, next;
        using (var lease = WorkspaceLease.Acquire(test.Workspace)) first = await installation.ActivateAsync(lease, a);
        var firstPlan = await LauncherPlan.CreateAsync(root); Assert.Equal(first.Executable, firstPlan.Executable); Assert.False(firstPlan.ExternalSetup);
        var setup = await LauncherPlan.CreateAsync(root, setup: true); Assert.True(setup.ExternalSetup); Assert.False(Workspace.IsWithin(root, setup.Executable));
        Assert.Equal(first.State.CurrentPackageId, (await ReleasePackage.ValidateAsync(Directory.GetParent(Path.GetDirectoryName(setup.Executable)!)!.FullName)).PackageId);
        Assert.Equal(["--setup", "--install-root", root], setup.Arguments);
        using (var lease = WorkspaceLease.Acquire(test.Workspace)) next = await installation.ActivateAsync(lease, b);
        Assert.Equal(next.Executable, (await LauncherPlan.CreateAsync(root)).Executable);
        using (var lease = WorkspaceLease.Acquire(test.Workspace)) await installation.RollbackAsync(lease);
        Assert.Equal(first.Executable, (await LauncherPlan.CreateAsync(root)).Executable);
        Assert.Equal(first.Executable, (await LauncherPlan.CreateAsync(root, maintenance: true)).Executable);
        using (var lease = WorkspaceLease.Acquire(test.Workspace)) await installation.UninstallAsync(lease, false);
        Assert.False(File.Exists(Path.Combine(root, InstalledLauncher.FileName))); Assert.True(File.Exists(setup.Executable));
        Assert.Single(await new SqliteJobStore(test.Workspace).LoadAsync());
    }
    [Theory]
    [InlineData(InstallationCheckpoint.LauncherPrepared)] [InlineData(InstallationCheckpoint.LauncherRemoved)]
    [InlineData(InstallationCheckpoint.LauncherActivated)] [InlineData(InstallationCheckpoint.LauncherRecorded)]
    public async Task InterruptedLauncherReplacementRecoversCommittedReleaseWithoutChangingQueuedWork(InstallationCheckpoint point)
    {
        using var test = new TestWorkspace(); var original = await InstallationTests.SeedAsync(test, "Frozen work"); var a = await PackageAsync(test, "a"); var b = await PackageAsync(test, "b"); var root = Path.Combine(test.Parent, "program"); var installation = new Installation(root);
        using var lease = WorkspaceLease.Acquire(test.Workspace); await installation.ActivateAsync(lease, a);
        await Assert.ThrowsAsync<IOException>(() => installation.ActivateAsync(lease, b, new Interrupt(point)));
        Assert.True(installation.HasPendingOperation); await Assert.ThrowsAsync<IOException>(() => installation.InspectAsync(lease));
        Assert.True(await installation.RecoverAsync(lease)); Assert.False(installation.HasPendingOperation);
        await InstalledLauncher.VerifyAsync(root, await installation.ReadOwnerAsync());
        var active = await installation.InspectAsync(lease); Assert.Equal((await ReleasePackage.ValidateAsync(b)).PackageId, active.State.CurrentPackageId);
        Assert.Equal(original.Id, Assert.Single(await new SqliteJobStore(test.Workspace).LoadAsync()).Id);
    }
    [Theory] [InlineData("launcher")] [InlineData("stage")] [InlineData("receipt")]
    public async Task ChangedLauncherRecoveryInputsArePreservedAndNeverOverwritten(string change)
    {
        using var test = new TestWorkspace(); var a = await PackageAsync(test, "a"); var b = await PackageAsync(test, "b"); var root = Path.Combine(test.Parent, "program"); var installation = new Installation(root);
        using var lease = WorkspaceLease.Acquire(test.Workspace); await installation.ActivateAsync(lease, a);
        await Assert.ThrowsAsync<IOException>(() => installation.ActivateAsync(lease, b, new Interrupt(InstallationCheckpoint.LauncherPrepared)));
        var path = change == "launcher" ? Path.Combine(root, InstalledLauncher.FileName) : change == "stage" ? Assert.Single(Directory.GetFiles(root, "launcher-stage-*.exe")) : Path.Combine(root, "launcher.owner.json");
        await File.AppendAllTextAsync(path, "Changed file must survive"); var hash = await Workspace.HashFileAsync(path);
        await Assert.ThrowsAsync<IOException>(() => installation.RecoverAsync(lease)); Assert.Equal(hash, await Workspace.HashFileAsync(path)); Assert.True(installation.HasPendingOperation);
    }
    [Fact] public async Task AChangedLauncherCannotBeLaunchedUpdatedOrUninstalled()
    {
        using var test = new TestWorkspace(); var source = await PackageAsync(test, "source"); var root = Path.Combine(test.Parent, "program"); var installation = new Installation(root);
        using (var lease = WorkspaceLease.Acquire(test.Workspace)) await installation.ActivateAsync(lease, source);
        var launcher = Path.Combine(root, InstalledLauncher.FileName); await File.AppendAllTextAsync(launcher, "Preserve alteration"); var hash = await Workspace.HashFileAsync(launcher);
        await Assert.ThrowsAsync<IOException>(() => LauncherPlan.CreateAsync(root));
        using var held = WorkspaceLease.Acquire(test.Workspace); await Assert.ThrowsAsync<IOException>(() => installation.ActivateAsync(held, source)); await Assert.ThrowsAsync<IOException>(() => installation.UninstallAsync(held, true));
        Assert.Equal(hash, await Workspace.HashFileAsync(launcher)); Assert.NotNull((await installation.ReadOverviewAsync()).State!.CurrentPackageId);
    }
    [Fact] public async Task OlderEditorRollbackKeepsTheNewerSetupKitAndCacheRejectsAlterations()
    {
        using var test = new TestWorkspace(); var legacy = await InstallationTests.PackageAsync(test, "legacy"); var next = await PackageAsync(test, "next"); var root = Path.Combine(test.Parent, "program"); var installation = new Installation(root);
        using (var lease = WorkspaceLease.Acquire(test.Workspace)) { await installation.ActivateAsync(lease, legacy); await installation.ActivateAsync(lease, next); await installation.RollbackAsync(lease); }
        var editor = await LauncherPlan.CreateAsync(root); Assert.Equal((await ReleasePackage.ValidateAsync(legacy)).PackageId, editor.PackageId);
        var setup = await LauncherPlan.CreateAsync(root, setup: true); Assert.Equal((await ReleasePackage.ValidateAsync(next)).PackageId, setup.PackageId);
        await File.AppendAllTextAsync(Path.Combine(Directory.GetParent(Path.GetDirectoryName(setup.Executable)!)!.FullName, "README.md"), "Changed cache");
        await Assert.ThrowsAsync<IOException>(() => LauncherPlan.CreateAsync(root, setup: true));
    }
    [Fact] public async Task SetupPlanCannotOverwriteAnUnownedExternalCache()
    {
        using var test = new TestWorkspace(); var source = await PackageAsync(test, "source"); var root = Path.Combine(test.Parent, "program"); var installation = new Installation(root);
        using (var lease = WorkspaceLease.Acquire(test.Workspace)) await installation.ActivateAsync(lease, source);
        var cache = LauncherPlan.SetupCacheRoot(root, await installation.ReadOwnerAsync()); Directory.CreateDirectory(cache); var sentinel = Path.Combine(cache, "keep.txt"); await File.WriteAllTextAsync(sentinel, "Unrelated files");
        await Assert.ThrowsAsync<IOException>(() => LauncherPlan.CreateAsync(root, setup: true)); Assert.Equal("Unrelated files", await File.ReadAllTextAsync(sentinel));
    }
    [Fact] public async Task CachedSetupRemainsUsableDuringCommittedPartialUninstallAndRecoveryRemovesTheOwnedLauncher()
    {
        using var test = new TestWorkspace(); await InstallationTests.SeedAsync(test, "Retain work"); var source = await PackageAsync(test, "source"); var root = Path.Combine(test.Parent, "program"); var installation = new Installation(root);
        using (var lease = WorkspaceLease.Acquire(test.Workspace)) await installation.ActivateAsync(lease, source);
        var setup = await LauncherPlan.CreateAsync(root, setup: true);
        using (var lease = WorkspaceLease.Acquire(test.Workspace)) await Assert.ThrowsAsync<IOException>(() => installation.UninstallAsync(lease, false, new InterruptRemoval()));
        var recoveredSetup = await LauncherPlan.CreateAsync(root, setup: true); Assert.Equal(setup.Executable, recoveredSetup.Executable); Assert.Equal(setup.Arguments, recoveredSetup.Arguments); Assert.Equal(setup.PackageId, recoveredSetup.PackageId);
        using (var lease = WorkspaceLease.Acquire(test.Workspace)) Assert.True(await installation.RecoverAsync(lease));
        Assert.False(File.Exists(Path.Combine(root, InstalledLauncher.FileName))); Assert.True(File.Exists(setup.Executable)); Assert.Single(await new SqliteJobStore(test.Workspace).LoadAsync());
        Assert.False((await installation.ReadOverviewAsync()).PendingRecovery);
    }
    [Theory] [InlineData("CommuteCast.exe")] [InlineData("launcher.owner.json")]
    public async Task ChangingTheStableLauncherAfterReviewCannotActivateOrRemoveAnotherState(string name)
    {
        using var test = new TestWorkspace(); var source = await PackageAsync(test, "source"); var root = Path.Combine(test.Parent, "program"); var installation = new Installation(root);
        using (var lease = WorkspaceLease.Acquire(test.Workspace)) await installation.ActivateAsync(lease, source);
        var session = new DeploymentSession(source, root, test.Workspace.Root); var review = await session.ReviewAsync(); var pointer = await Workspace.HashFileAsync(Path.Combine(root, "installation.json"));
        await File.AppendAllTextAsync(Path.Combine(root, name), name.EndsWith("json", StringComparison.Ordinal) ? " " : "changed");
        await Assert.ThrowsAsync<IOException>(() => session.ApplyAsync(review, DeploymentAction.UninstallRetain, true));
        Assert.Equal(pointer, await Workspace.HashFileAsync(Path.Combine(root, "installation.json"))); Assert.True(File.Exists(Path.Combine(root, InstalledLauncher.FileName)));
    }
    [Theory]
    [InlineData(InstallationCheckpoint.LauncherPrepared)] [InlineData(InstallationCheckpoint.LauncherRemoved)]
    [InlineData(InstallationCheckpoint.LauncherActivated)] [InlineData(InstallationCheckpoint.LauncherRecorded)]
    public async Task SetupCanBePlannedAcrossLauncherInterruptionWithoutImplicitRecovery(InstallationCheckpoint point)
    {
        using var test = new TestWorkspace(); var a = await PackageAsync(test, "a"); var b = await PackageAsync(test, "b"); var root = Path.Combine(test.Parent, "program"); var installation = new Installation(root);
        using (var lease = WorkspaceLease.Acquire(test.Workspace)) { await installation.ActivateAsync(lease, a); await Assert.ThrowsAsync<IOException>(() => installation.ActivateAsync(lease, b, new Interrupt(point))); }
        var journal = Path.Combine(root, "launcher.pending.json"); var hash = await Workspace.HashFileAsync(journal);
        var setup = await LauncherPlan.CreateAsync(root, setup: true); Assert.True(setup.ExternalSetup); Assert.Equal((await ReleasePackage.ValidateAsync(b)).PackageId, setup.PackageId);
        Assert.Equal(hash, await Workspace.HashFileAsync(journal)); Assert.True(installation.HasPendingOperation);
        await Assert.ThrowsAsync<IOException>(() => LauncherPlan.CreateAsync(root));
        using var held = WorkspaceLease.Acquire(test.Workspace); Assert.True(await installation.RecoverAsync(held));
    }
}
