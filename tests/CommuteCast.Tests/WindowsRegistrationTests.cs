using CommuteCast.Infrastructure;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace CommuteCast.Tests;

[SupportedOSPlatform("windows")]
public class WindowsRegistrationTests
{
    private sealed class Interrupt(InstallationCheckpoint point) : IInstallationObserver
    { public Task ReachedAsync(InstallationCheckpoint actual, string? item, CancellationToken ct) => actual == point ? throw new IOException("Synthetic Windows integration interruption") : Task.CompletedTask; }
    private sealed class Fixture : IDisposable
    {
        public TestWorkspace Test { get; } = new();
        public Installation Install { get; }
        public InstallationOwner Owner { get; private set; } = null!;
        public string Source { get; private set; } = "";
        public string Links => WindowsRegistration.ShortcutRoot(Owner);
        public string Key => WindowsRegistration.RegistryPath(Owner);
        private bool ownsIntegration;
        private Fixture() { Install = new(Path.Combine(Test.Parent, "program with spaces")); }
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            try
            {
                await InstallationTests.SeedAsync(fixture.Test, "Private queued narration");
                var legacy = await InstallationTests.PackageAsync(fixture.Test, "legacy");
                using (var lease = WorkspaceLease.Acquire(fixture.Test.Workspace)) await fixture.Install.ActivateAsync(lease, legacy);
                fixture.Owner = await fixture.Install.ReadOwnerAsync();
                using var existing = Registry.CurrentUser.OpenSubKey(fixture.Key);
                if (existing is not null || Directory.Exists(fixture.Links)) throw new IOException("A synthetic integration identity is already occupied.");
                fixture.ownsIntegration = true;
                fixture.Source = await InstallationTests.PackageAsync(fixture.Test, "registered");
                File.Copy(Path.Combine(fixture.Source, "app", "CommuteCast.Desktop.exe"), Path.Combine(fixture.Source, InstalledLauncher.PackageFile.Replace('/', Path.DirectorySeparatorChar)));
                await File.WriteAllTextAsync(Path.Combine(fixture.Source, WindowsRegistration.FeatureFile.Replace('/', Path.DirectorySeparatorChar)), WindowsRegistration.FeatureContent);
                await ReleasePackage.SealAsync(fixture.Source); return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }
        public Task<InstallationResult> ActivateAsync(WorkspaceLease lease, IInstallationObserver? observer = null) => Install.ActivateAsync(lease, Source, observer);
        public void Dispose()
        {
            if (ownsIntegration)
            {
                // This exact random identity was absent before the fixture created it.
                Registry.CurrentUser.DeleteSubKeyTree(Key, false);
                var programs = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Programs));
                var target = Path.GetFullPath(Links);
                if (!Workspace.IsWithin(programs, target) || Path.GetFileName(target) != "CommuteCast-" + Owner.InstallationId) throw new IOException("Unsafe synthetic shortcut cleanup target.");
                Workspace.RejectReparsePoints(target); if (Directory.Exists(target)) Directory.Delete(target, true);
            }
            Test.Dispose();
        }
    }
    [Fact] public async Task RealPerUserKeyAndShellLinksFollowTheStableEntryThroughRollbackAndUninstall()
    {
        using var fixture = await Fixture.CreateAsync(); using var lease = WorkspaceLease.Acquire(fixture.Test.Workspace);
        await fixture.ActivateAsync(lease);
        using (var key = Registry.CurrentUser.OpenSubKey(fixture.Key)!)
        {
            Assert.Equal("CommuteCast", key.GetValue("DisplayName")); Assert.Equal(fixture.Install.Root, key.GetValue("InstallLocation"));
            Assert.Equal("\"" + Path.Combine(fixture.Install.Root, InstalledLauncher.FileName) + "\" --setup", key.GetValue("UninstallString"));
            Assert.Equal(RegistryValueKind.DWord, key.GetValueKind("NoRepair")); Assert.Equal(1, key.GetValue("NoRepair")); Assert.Null(key.GetValue("Publisher"));
        }
        var observed = await ReadLinksAsync(fixture.Links);
        Assert.All(observed, link => { Assert.Equal(Path.Combine(fixture.Install.Root, InstalledLauncher.FileName), link.Target, ignoreCase: true); Assert.Equal(fixture.Install.Root, link.WorkingDirectory, ignoreCase: true); });
        Assert.Equal(["", "--setup"], observed.Select(l => l.Arguments));
        await fixture.Install.RollbackAsync(lease); // Older editor-only release retains the stable setup-capable launcher and integration.
        Assert.True(File.Exists(Path.Combine(fixture.Links, "CommuteCast.lnk"))); await fixture.Install.RollbackAsync(lease);
        await File.WriteAllTextAsync(Path.Combine(fixture.Links, "user-note.txt"), "Preserve unrelated Start Menu file");
        await fixture.Install.UninstallAsync(lease, false);
        using (var absent = Registry.CurrentUser.OpenSubKey(fixture.Key)) Assert.Null(absent);
        Assert.False(File.Exists(Path.Combine(fixture.Links, "CommuteCast.lnk"))); Assert.Equal("Preserve unrelated Start Menu file", await File.ReadAllTextAsync(Path.Combine(fixture.Links, "user-note.txt")));
        Assert.Single(await new SqliteJobStore(fixture.Test.Workspace).LoadAsync());
    }
    private static Task<(string Target, string Arguments, string WorkingDirectory)[]> ReadLinksAsync(string root)
    {
        var completion = new TaskCompletionSource<(string, string, string)[]>();
        var thread = new Thread(() =>
        {
            object? shell = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!); dynamic dispatch = shell!;
                var result = new List<(string, string, string)>();
                foreach (var name in new[] { "CommuteCast.lnk", "CommuteCast setup.lnk" })
                { object link = dispatch.CreateShortcut(Path.Combine(root, name)); try { dynamic value = link; result.Add(((string)value.TargetPath, (string)value.Arguments, (string)value.WorkingDirectory)); } finally { Marshal.FinalReleaseComObject(link); } }
                completion.SetResult(result.ToArray());
            }
            catch (Exception error) { completion.SetException(error); }
            finally { if (shell is not null) Marshal.FinalReleaseComObject(shell); }
        }); thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
    [Theory]
    [InlineData(InstallationCheckpoint.RegistrationPrepared)] [InlineData(InstallationCheckpoint.RegistrationValueWritten)]
    [InlineData(InstallationCheckpoint.RegistrationShortcutRemoved)] [InlineData(InstallationCheckpoint.RegistrationShortcutActivated)] [InlineData(InstallationCheckpoint.RegistrationRecorded)]
    public async Task InterruptedRegistrationRecoversCommittedPackageAndPreservesQueue(InstallationCheckpoint point)
    {
        using var fixture = await Fixture.CreateAsync(); using var lease = WorkspaceLease.Acquire(fixture.Test.Workspace);
        await Assert.ThrowsAsync<IOException>(() => fixture.ActivateAsync(lease, new Interrupt(point)));
        Assert.True(fixture.Install.HasPendingOperation); await Assert.ThrowsAsync<IOException>(() => fixture.Install.InspectAsync(lease));
        Assert.True(await fixture.Install.RecoverAsync(lease)); Assert.False(fixture.Install.HasPendingOperation);
        Assert.Equal((await ReleasePackage.ValidateAsync(fixture.Source)).PackageId, (await fixture.Install.InspectAsync(lease)).State.CurrentPackageId);
        Assert.Single(await new SqliteJobStore(fixture.Test.Workspace).LoadAsync()); Assert.Equal(2, (await ReadLinksAsync(fixture.Links)).Length);
    }
    [Theory]
    [InlineData(InstallationCheckpoint.RegistrationPrepared)] [InlineData(InstallationCheckpoint.RegistrationValueWritten)]
    [InlineData(InstallationCheckpoint.RegistrationShortcutRemoved)] [InlineData(InstallationCheckpoint.RegistrationRecorded)]
    public async Task InterruptedUninstallFinishesRegistrationBeforeDeletingTheStableEntry(InstallationCheckpoint point)
    {
        using var fixture = await Fixture.CreateAsync(); using var lease = WorkspaceLease.Acquire(fixture.Test.Workspace); await fixture.ActivateAsync(lease);
        await Assert.ThrowsAsync<IOException>(() => fixture.Install.UninstallAsync(lease, false, new Interrupt(point)));
        Assert.True(File.Exists(Path.Combine(fixture.Install.Root, InstalledLauncher.FileName))); Assert.True(fixture.Install.HasPendingOperation);
        await fixture.Install.RecoverAsync(lease); Assert.False(fixture.Install.HasPendingOperation); Assert.False(Directory.Exists(fixture.Links));
        using (var key = Registry.CurrentUser.OpenSubKey(fixture.Key)) Assert.Null(key);
        Assert.False(File.Exists(Path.Combine(fixture.Install.Root, InstalledLauncher.FileName))); Assert.Single(await new SqliteJobStore(fixture.Test.Workspace).LoadAsync());
    }
    [Theory] [InlineData("registry")] [InlineData("shortcut")] [InlineData("stage")] [InlineData("receipt")]
    public async Task ChangedRecoveryInputsAreRetainedAndBlockAllFurtherRemoval(string change)
    {
        using var fixture = await Fixture.CreateAsync(); using var lease = WorkspaceLease.Acquire(fixture.Test.Workspace);
        var point = change is "shortcut" or "registry" or "receipt" ? InstallationCheckpoint.RegistrationRecorded : InstallationCheckpoint.RegistrationPrepared;
        await Assert.ThrowsAsync<IOException>(() => fixture.ActivateAsync(lease, new Interrupt(point)));
        string? path = change switch { "shortcut" => Path.Combine(fixture.Links, "CommuteCast.lnk"), "stage" => Directory.GetFiles(fixture.Install.Root, "registration-stage-*.lnk").First(), "receipt" => Path.Combine(fixture.Install.Root, "windows.owner.json"), _ => null };
        if (path is not null) await File.AppendAllTextAsync(path, "Changed synthetic sentinel");
        else { using var key = Registry.CurrentUser.OpenSubKey(fixture.Key, true)!; key.SetValue("UninstallString", "Synthetic changed command"); }
        var hash = path is null ? "" : await Workspace.HashFileAsync(path);
        await Assert.ThrowsAsync<IOException>(() => fixture.Install.RecoverAsync(lease)); Assert.True(fixture.Install.HasPendingOperation);
        if (path is not null) Assert.Equal(hash, await Workspace.HashFileAsync(path));
        else { using var key = Registry.CurrentUser.OpenSubKey(fixture.Key)!; Assert.Equal("Synthetic changed command", key.GetValue("UninstallString")); }
        Assert.True(File.Exists(Path.Combine(fixture.Install.Root, InstalledLauncher.FileName))); Assert.Single(await new SqliteJobStore(fixture.Test.Workspace).LoadAsync());
    }
    [Theory] [InlineData("key")] [InlineData("link")]
    public async Task UnownedEntryPointsAreRefusedBeforeActivation(string kind)
    {
        using var fixture = await Fixture.CreateAsync(); using var lease = WorkspaceLease.Acquire(fixture.Test.Workspace); var before = (await fixture.Install.InspectAsync(lease)).State.CurrentPackageId;
        if (kind == "key") { using var key = Registry.CurrentUser.CreateSubKey(fixture.Key); key.SetValue("DisplayName", "Unrelated fixture"); }
        else { Directory.CreateDirectory(fixture.Links); await File.WriteAllTextAsync(Path.Combine(fixture.Links, "CommuteCast.lnk"), "Unrelated fixture"); }
        await Assert.ThrowsAsync<IOException>(() => fixture.ActivateAsync(lease)); Assert.Equal(before, (await fixture.Install.InspectAsync(lease)).State.CurrentPackageId); Assert.False(fixture.Install.HasPendingOperation);
    }
    [Fact] public async Task RegistrationChangesInvalidateNativeReviewBeforeAnyMutation()
    {
        using var fixture = await Fixture.CreateAsync(); using (var lease = WorkspaceLease.Acquire(fixture.Test.Workspace)) await fixture.ActivateAsync(lease);
        var session = new DeploymentSession(fixture.Source, fixture.Install.Root, fixture.Test.Workspace.Root); var review = await session.ReviewAsync();
        using (var key = Registry.CurrentUser.OpenSubKey(fixture.Key, true)!) key.SetValue("DisplayName", "Altered synthetic entry");
        await Assert.ThrowsAsync<IOException>(() => session.ApplyAsync(review, DeploymentAction.UninstallRetain, true));
        Assert.False(fixture.Install.HasPendingOperation); Assert.True(File.Exists(Path.Combine(fixture.Install.Root, InstalledLauncher.FileName)));
    }
    [Fact] public async Task MissingOwnedEntriesCanBeRepairedButUnknownRegistryValuesPreventUninstall()
    {
        using var fixture = await Fixture.CreateAsync(); using var lease = WorkspaceLease.Acquire(fixture.Test.Workspace); await fixture.ActivateAsync(lease);
        Registry.CurrentUser.DeleteSubKeyTree(fixture.Key); File.Delete(Path.Combine(fixture.Links, "CommuteCast.lnk"));
        await fixture.ActivateAsync(lease); Assert.True(File.Exists(Path.Combine(fixture.Links, "CommuteCast.lnk")));
        using (var key = Registry.CurrentUser.OpenSubKey(fixture.Key, true)!) key.SetValue("UserNote", "Unrelated fixture value");
        await Assert.ThrowsAsync<IOException>(() => fixture.Install.UninstallAsync(lease, true)); Assert.False(fixture.Install.HasPendingOperation);
        Assert.Single(await new SqliteJobStore(fixture.Test.Workspace).LoadAsync()); Assert.True(File.Exists(Path.Combine(fixture.Install.Root, InstalledLauncher.FileName)));
    }
}
