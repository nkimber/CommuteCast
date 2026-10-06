using CommuteCast.Infrastructure;
using System.Text.Json;

namespace CommuteCast.Tests;

public class SetupCacheTests
{
    private sealed class Interrupt(InstallationCheckpoint point) : IInstallationObserver
    { public Task ReachedAsync(InstallationCheckpoint actual, string? item, CancellationToken ct) => actual == point ? throw new IOException("Synthetic cleanup interruption") : Task.CompletedTask; }
    private sealed class Fixture : IDisposable
    {
        public TestWorkspace Test { get; } = new();
        public Installation Install { get; }
        public string OldCopy { get; private set; } = "";
        public string NewCopy { get; private set; } = "";
        public string NewSource { get; private set; } = "";
        public string CacheRoot => Directory.GetParent(OldCopy)!.FullName;
        private Fixture() { Install = new(Path.Combine(Test.Parent, "program")); }
        private static async Task<string> PackageAsync(TestWorkspace test, string label)
        {
            var source = await InstallationTests.PackageAsync(test, label);
            File.Copy(Path.Combine(source, "app", "CommuteCast.Desktop.exe"), Path.Combine(source, InstalledLauncher.PackageFile.Replace('/', Path.DirectorySeparatorChar)));
            await ReleasePackage.SealAsync(source); return source;
        }
        public static async Task<Fixture> CreateAsync(bool legacy = false)
        {
            var fixture = new Fixture();
            try
            {
                await InstallationTests.SeedAsync(fixture.Test, "Retained private narration");
                var a = legacy ? await InstallationTests.PackageAsync(fixture.Test, "a") : await PackageAsync(fixture.Test, "a"); fixture.NewSource = await PackageAsync(fixture.Test, "b");
                using (var lease = WorkspaceLease.Acquire(fixture.Test.Workspace)) await fixture.Install.ActivateAsync(lease, a);
                if (!legacy) fixture.OldCopy = Directory.GetParent(Path.GetDirectoryName((await LauncherPlan.CreateAsync(fixture.Install.Root, setup: true)).Executable)!)!.FullName;
                using (var lease = WorkspaceLease.Acquire(fixture.Test.Workspace)) await fixture.Install.ActivateAsync(lease, fixture.NewSource);
                fixture.NewCopy = Directory.GetParent(Path.GetDirectoryName((await LauncherPlan.CreateAsync(fixture.Install.Root, setup: true)).Executable)!)!.FullName;
                if (legacy)
                {
                    fixture.OldCopy = Path.Combine(Directory.GetParent(fixture.NewCopy)!.FullName, (await ReleasePackage.ValidateAsync(a)).PackageId);
                    foreach (var input in Directory.GetFiles(a, "*", SearchOption.AllDirectories))
                    { var output = Path.Combine(fixture.OldCopy, Path.GetRelativePath(a, input)); Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.Copy(input, output); }
                }
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }
        public void Dispose() => Test.Dispose();
    }
    private static async Task<Dictionary<string, string>> InventoryAsync(string root)
    {
        var result = new Dictionary<string, string>();
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) != "instance.lease")) result.Add(Path.GetRelativePath(root, file), await Workspace.HashFileAsync(file));
        return result;
    }
    [Fact] public async Task ReviewedCleanupReclaimsOldCopyAndPreservesCurrentKitPrivateStateAndUnknownEntries()
    {
        using var fixture = await Fixture.CreateAsync(); var before = await InventoryAsync(fixture.Test.Workspace.Root);
        await File.WriteAllTextAsync(Path.Combine(fixture.CacheRoot, "user-note.txt"), "Keep me"); var stage = Path.Combine(fixture.CacheRoot, "stage-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage); await File.WriteAllTextAsync(Path.Combine(stage, "partial.txt"), "Keep unknown staging");
        var review = await fixture.Install.ReviewSetupCacheAsync(); Assert.Equal(1, review.Copies); Assert.True(review.Files > 0); Assert.True(review.Bytes > 0); Assert.Equal(3, review.Preserved.Count);
        using var lease = WorkspaceLease.Acquire(fixture.Test.Workspace);
        await Assert.ThrowsAsync<IOException>(() => fixture.Install.CleanSetupCacheAsync(lease, review, false)); Assert.False(fixture.Install.HasPendingOperation);
        var result = await fixture.Install.CleanSetupCacheAsync(lease, review, true); Assert.Equal(review.Bytes, result.Bytes); Assert.Equal(review.Files, result.Files);
        Assert.False(Directory.Exists(fixture.OldCopy)); Assert.True(Directory.Exists(fixture.NewCopy)); Assert.True(File.Exists(Path.Combine(fixture.CacheRoot, "setup-cache.owner.json")));
        Assert.Equal("Keep me", await File.ReadAllTextAsync(Path.Combine(fixture.CacheRoot, "user-note.txt"))); Assert.Equal("Keep unknown staging", await File.ReadAllTextAsync(Path.Combine(stage, "partial.txt")));
        Assert.Equal(before.OrderBy(p => p.Key), (await InventoryAsync(fixture.Test.Workspace.Root)).OrderBy(p => p.Key));
        Assert.Equal((await ReleasePackage.ValidateAsync(fixture.NewCopy)).PackageId, (await fixture.Install.InspectAsync(lease)).State.CurrentPackageId);
    }
    [Fact] public async Task AfterUninstallExternalCleanupCanReclaimAllCopiesWhileACachedSourceKeepsItself()
    {
        using var fixture = await Fixture.CreateAsync(); using var lease = WorkspaceLease.Acquire(fixture.Test.Workspace); await fixture.Install.UninstallAsync(lease, false);
        var cached = await fixture.Install.ReviewSetupCacheAsync(fixture.NewCopy.ToLowerInvariant()); Assert.Equal(1, cached.Copies);
        await fixture.Install.CleanSetupCacheAsync(lease, cached, true, fixture.NewCopy.ToLowerInvariant()); Assert.True(Directory.Exists(fixture.NewCopy)); Assert.False(Directory.Exists(fixture.OldCopy));
        var external = await fixture.Install.ReviewSetupCacheAsync(); Assert.Equal(1, external.Copies); await fixture.Install.CleanSetupCacheAsync(lease, external, true);
        Assert.False(Directory.Exists(fixture.NewCopy)); Assert.Single(await new SqliteJobStore(fixture.Test.Workspace).LoadAsync());
        Assert.True(File.Exists(Path.Combine(fixture.CacheRoot, "setup-cache.owner.json"))); Assert.False(fixture.Install.HasPendingOperation);
    }
    [Fact] public async Task CachedHostUseLeasePreservesCopyUntilExitAndReviewCreatesNoNewLease()
    {
        using var fixture = await Fixture.CreateAsync(); var before = await InventoryAsync(fixture.CacheRoot);
        var initial = await fixture.Install.ReviewSetupCacheAsync(); Assert.Equal(1, initial.Copies);
        Assert.Equal(before.OrderBy(p => p.Key), (await InventoryAsync(fixture.CacheRoot)).OrderBy(p => p.Key));
        using (var host = await SetupCache.AcquireHostUseAsync(Path.Combine(fixture.OldCopy, "app")))
        { Assert.NotNull(host); Assert.Equal(fixture.Install.Root, host.InstallationRoot); Assert.Equal(fixture.Test.Workspace.Root, host.PrivateRoot); var busy = await fixture.Install.ReviewSetupCacheAsync(); Assert.Equal(0, busy.Copies); Assert.Contains(busy.Preserved, p => p.Reason.Contains("in use")); }
        Assert.Equal(1, (await fixture.Install.ReviewSetupCacheAsync()).Copies);
        Assert.Null(await SetupCache.AcquireHostUseAsync(Path.Combine(fixture.NewSource, "app")));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RollbackKeepsTheActiveCacheAndAnyNewerRequiredRecoveryKit(bool legacy)
    {
        using var fixture = await Fixture.CreateAsync(legacy); using var lease = WorkspaceLease.Acquire(fixture.Test.Workspace); await fixture.Install.RollbackAsync(lease);
        var review = await fixture.Install.ReviewSetupCacheAsync(); Assert.Equal(legacy ? 0 : 1, review.Copies); await fixture.Install.CleanSetupCacheAsync(lease, review, true);
        Assert.True(Directory.Exists(fixture.OldCopy)); Assert.Equal(legacy, Directory.Exists(fixture.NewCopy));
        lease.Dispose();
        Assert.Equal(Path.GetFileName(legacy ? fixture.NewCopy : fixture.OldCopy), (await LauncherPlan.CreateAsync(fixture.Install.Root, setup: true)).PackageId);
    }
    [Theory] [InlineData(InstallationCheckpoint.SetupCachePrepared)] [InlineData(InstallationCheckpoint.SetupCacheItemRemoved)]
    public async Task InterruptedCleanupRecoversOnlyItsReviewedFilesWithoutChangingQueue(InstallationCheckpoint point)
    {
        using var fixture = await Fixture.CreateAsync(); var privateBefore = await InventoryAsync(fixture.Test.Workspace.Root); var review = await fixture.Install.ReviewSetupCacheAsync(); using var lease = WorkspaceLease.Acquire(fixture.Test.Workspace);
        await Assert.ThrowsAsync<IOException>(() => fixture.Install.CleanSetupCacheAsync(lease, review, true, observer: new Interrupt(point)));
        Assert.True(fixture.Install.HasPendingOperation); Assert.Equal("Setup cache", (await fixture.Install.ReadOverviewAsync()).PendingOperation);
        await Assert.ThrowsAsync<IOException>(() => fixture.Install.InspectAsync(lease)); await Assert.ThrowsAsync<IOException>(() => fixture.Install.ReviewSetupCacheAsync());
        Assert.True(await fixture.Install.RecoverAsync(lease)); Assert.False(fixture.Install.HasPendingOperation); Assert.False(Directory.Exists(fixture.OldCopy)); Assert.True(Directory.Exists(fixture.NewCopy));
        Assert.Equal(privateBefore.OrderBy(p => p.Key), (await InventoryAsync(fixture.Test.Workspace.Root)).OrderBy(p => p.Key));
    }
    [Theory] [InlineData("file")] [InlineData("unknown-file")] [InlineData("unknown-directory")]
    public async Task ChangedOrUnrecognizedCopyRequiresANewReviewAndIsPreserved(string change)
    {
        using var fixture = await Fixture.CreateAsync(); var review = await fixture.Install.ReviewSetupCacheAsync();
        if (change == "file") await File.AppendAllTextAsync(Path.Combine(fixture.OldCopy, "README.md"), "Changed");
        else if (change == "unknown-file") await File.WriteAllTextAsync(Path.Combine(fixture.OldCopy, "unknown.txt"), "Keep unknown");
        else Directory.CreateDirectory(Path.Combine(fixture.OldCopy, "unknown-empty-folder"));
        var before = await InventoryAsync(fixture.OldCopy); using var lease = WorkspaceLease.Acquire(fixture.Test.Workspace);
        await Assert.ThrowsAsync<IOException>(() => fixture.Install.CleanSetupCacheAsync(lease, review, true)); Assert.False(fixture.Install.HasPendingOperation);
        Assert.Equal(0, (await fixture.Install.ReviewSetupCacheAsync()).Copies); Assert.Equal(before.OrderBy(p => p.Key), (await InventoryAsync(fixture.OldCopy)).OrderBy(p => p.Key));
    }
    [Theory] [InlineData("file")] [InlineData("owner")] [InlineData("unknown-directory")] [InlineData("journal")]
    public async Task ChangedPendingCleanupOwnershipCannotDeleteRemainingFiles(string change)
    {
        using var fixture = await Fixture.CreateAsync(); var review = await fixture.Install.ReviewSetupCacheAsync(); using var lease = WorkspaceLease.Acquire(fixture.Test.Workspace);
        await Assert.ThrowsAsync<IOException>(() => fixture.Install.CleanSetupCacheAsync(lease, review, true, observer: new Interrupt(InstallationCheckpoint.SetupCachePrepared)));
        var changed = change switch { "file" => Path.Combine(fixture.OldCopy, "README.md"), "owner" => Path.Combine(fixture.CacheRoot, "setup-cache.owner.json"), "journal" => Path.Combine(fixture.Install.Root, "setup-cache.pending.json"), _ => null };
        if (changed is not null) await File.AppendAllTextAsync(changed, "Changed outside cleanup"); else Directory.CreateDirectory(Path.Combine(fixture.OldCopy, "unrecorded"));
        var before = await InventoryAsync(fixture.OldCopy); await Assert.ThrowsAsync<IOException>(() => fixture.Install.RecoverAsync(lease)); Assert.True(fixture.Install.HasPendingOperation);
        Assert.Equal(before.OrderBy(p => p.Key), (await InventoryAsync(fixture.OldCopy)).OrderBy(p => p.Key)); Assert.Single(await new SqliteJobStore(fixture.Test.Workspace).LoadAsync());
    }
    [Fact] public async Task OneLockedFilePreventsAnyFileInThatCopyFromBeingDeletedAndRecoveryFinishesAfterRelease()
    {
        using var fixture = await Fixture.CreateAsync(); var review = await fixture.Install.ReviewSetupCacheAsync(); var before = await InventoryAsync(fixture.OldCopy); using var lease = WorkspaceLease.Acquire(fixture.Test.Workspace);
        using (var held = new FileStream(Path.Combine(fixture.OldCopy, "README.md"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAsync<IOException>(() => fixture.Install.CleanSetupCacheAsync(lease, review, true)); Assert.True(fixture.Install.HasPendingOperation);
            Assert.Equal(before.OrderBy(p => p.Key), (await InventoryAsync(fixture.OldCopy)).OrderBy(p => p.Key));
        }
        Assert.True(await fixture.Install.RecoverAsync(lease)); Assert.False(Directory.Exists(fixture.OldCopy)); Assert.True(Directory.Exists(fixture.NewCopy));
    }
    [Fact] public async Task NativeCacheConfirmationIsSingleUseAndBindsTheDistributionScope()
    {
        using var fixture = await Fixture.CreateAsync(); var session = new DeploymentSession(fixture.NewSource, fixture.Install.Root, fixture.Test.Workspace.Root); var review = await session.ReviewAsync(); Assert.Equal(1, review.Cache!.Copies);
        await Assert.ThrowsAsync<IOException>(() => session.ApplyAsync(review, DeploymentAction.CleanSetupCache, false));
        var outcome = await session.ApplyAsync(review, DeploymentAction.CleanSetupCache, true); Assert.Equal(1, outcome.CacheCleanup!.Copies); Assert.False(Directory.Exists(fixture.OldCopy));
        await Assert.ThrowsAsync<IOException>(() => session.ApplyAsync(review, DeploymentAction.CleanSetupCache, true)); Assert.Single(await new SqliteJobStore(fixture.Test.Workspace).LoadAsync());
    }
    [Fact] public async Task NoCacheReviewPreservesAbsentCacheAndUnrecognizedRootContents()
    {
        using var test = new TestWorkspace(); await InstallationTests.SeedAsync(test, "Original"); var package = await InstallationTests.PackageAsync(test, "legacy"); var install = new Installation(Path.Combine(test.Parent, "program")); using var lease = WorkspaceLease.Acquire(test.Workspace); await install.ActivateAsync(lease, package);
        var cache = LauncherPlan.SetupCacheRoot(install.Root, await install.ReadOwnerAsync()); Assert.False(Directory.Exists(cache)); Assert.Equal(0, (await install.ReviewSetupCacheAsync()).Copies); Assert.False(Directory.Exists(cache));
        Directory.CreateDirectory(cache); await File.WriteAllTextAsync(Path.Combine(cache, "note.txt"), "Unowned"); await Assert.ThrowsAsync<IOException>(() => install.ReviewSetupCacheAsync()); Assert.Equal("Unowned", await File.ReadAllTextAsync(Path.Combine(cache, "note.txt")));
    }
    private sealed class CancelAtRemoval(CancellationTokenSource stop) : IInstallationObserver
    { public Task ReachedAsync(InstallationCheckpoint point, string? item, CancellationToken ct) { if (point == InstallationCheckpoint.SetupCacheItemRemoved) stop.Cancel(); return Task.CompletedTask; } }
    [Fact] public async Task CancelledPartialCleanupLeavesItsJournalAndRecoversTheRemainingOwnedCopy()
    {
        using var fixture = await Fixture.CreateAsync(); var review = await fixture.Install.ReviewSetupCacheAsync(); using var lease = WorkspaceLease.Acquire(fixture.Test.Workspace); using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Install.CleanSetupCacheAsync(lease, review, true, observer: new CancelAtRemoval(stop), ct: stop.Token));
        Assert.True(fixture.Install.HasPendingOperation); Assert.True(await fixture.Install.RecoverAsync(lease)); Assert.False(Directory.Exists(fixture.OldCopy)); Assert.True(Directory.Exists(fixture.NewCopy));
        Assert.Single(await new SqliteJobStore(fixture.Test.Workspace).LoadAsync());
    }
    [Theory] [InlineData(DesktopMode.Editor)] [InlineData(DesktopMode.Maintenance)]
    public void CachedDesktopCannotSilentlyStartAnotherWorkspace(DesktopMode mode)
    { Assert.Throws<IOException>(() => new DesktopStartup(mode).BindCachedSetup(Path.Combine(Path.GetTempPath(), "owned-program"), Path.Combine(Path.GetTempPath(), "owned-private"))); }
    [Theory] [InlineData(true)] [InlineData(false)]
    public void CachedSetupBindsMissingPathsAndRefusesDifferentExplicitBindings(bool wrongPrivate)
    {
        var root = Path.Combine(Path.GetTempPath(), "owned-program"); var data = Path.Combine(Path.GetTempPath(), "owned-private");
        var bound = new DesktopStartup(DesktopMode.Setup).BindCachedSetup(root, data); Assert.Equal(root, bound.InstallRoot); Assert.Equal(data, bound.PrivateRoot);
        var changed = new DesktopStartup(DesktopMode.Setup, wrongPrivate ? root : root + "-other", wrongPrivate ? data + "-other" : data);
        Assert.Throws<IOException>(() => changed.BindCachedSetup(root, data));
        Assert.Equal(bound, new DesktopStartup(DesktopMode.Setup, root.ToLowerInvariant(), data.ToLowerInvariant()).BindCachedSetup(root, data));
    }
    [Fact] public async Task SemanticallyEditedJournalCannotClaimAnUnrecognizedFileAsPartOfAReleasedPackage()
    {
        using var fixture = await Fixture.CreateAsync(); var review = await fixture.Install.ReviewSetupCacheAsync(); using var lease = WorkspaceLease.Acquire(fixture.Test.Workspace);
        await Assert.ThrowsAsync<IOException>(() => fixture.Install.CleanSetupCacheAsync(lease, review, true, observer: new Interrupt(InstallationCheckpoint.SetupCachePrepared)));
        var unknown = Path.Combine(fixture.OldCopy, "unrelated.txt"); await File.WriteAllTextAsync(unknown, "Preserve this unrecognized file");
        var journalPath = Path.Combine(fixture.Install.Root, "setup-cache.pending.json"); var journal = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(journalPath))!;
        journal["Packages"]![0]!["Files"]!.AsArray().Add(JsonSerializer.SerializeToNode(new ReleaseFile("unrelated.txt", new FileInfo(unknown).Length, await Workspace.HashFileAsync(unknown))));
        await File.WriteAllTextAsync(journalPath, journal.ToJsonString(new() { WriteIndented = true })); var before = await InventoryAsync(fixture.OldCopy);
        await Assert.ThrowsAsync<IOException>(() => fixture.Install.RecoverAsync(lease)); Assert.True(fixture.Install.HasPendingOperation);
        Assert.Equal(before.OrderBy(p => p.Key), (await InventoryAsync(fixture.OldCopy)).OrderBy(p => p.Key));
    }
}
