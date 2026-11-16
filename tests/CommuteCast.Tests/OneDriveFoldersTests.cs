using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public sealed class OneDriveFoldersTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "CommuteCast-folders-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void NewDesktopSettingsUseThePersonalSharedRootAndItsListeningFolder()
    {
        var workspace = new Workspace(Path.Combine(root, "private"));
        var personal = Directory.CreateDirectory(Path.Combine(root, "Personal OneDrive")).FullName;
        var settings = new AppSettings();

        Assert.True(OneDriveFolders.ApplyDesktopDefaults(settings, workspace, personal));
        var remote = Assert.IsType<RemoteQueueSettings>(settings.RemoteQueue);
        Assert.Equal(Path.Combine(personal, "CommuteCast"), remote.Folder);
        Assert.False(remote.EnableWorker);
        Assert.Equal(new RemoteQueueFolder(remote.Folder, workspace).OutputFolder, settings.Destination);
        Assert.True(Directory.Exists(settings.Destination));
        Assert.False(OneDriveFolders.ApplyDesktopDefaults(settings, workspace, personal));
    }

    [Fact]
    public void ExplicitFolderOverridesRemainEffective()
    {
        var workspace = new Workspace(Path.Combine(root, "private"));
        var personal = Directory.CreateDirectory(Path.Combine(root, "Personal OneDrive")).FullName;
        var custom = Directory.CreateDirectory(Path.Combine(root, "Already shared")).FullName;
        var remote = new RemoteQueueSettings(custom, true, "Worker", Guid.NewGuid().ToString("N"), []);
        var settings = new AppSettings { RemoteQueue = remote };

        Assert.True(OneDriveFolders.ApplyDesktopDefaults(settings, workspace, personal));
        Assert.Same(remote, settings.RemoteQueue);
        Assert.Equal(Path.Combine(custom, "Ready to Listen"), settings.Destination);
        Assert.False(Directory.Exists(Path.Combine(personal, "CommuteCast")));
        Assert.Contains(OneDriveFolders.Describe(custom, settings.Destination, workspace.Root, Path.Combine(workspace.Root, "logs")),
            row => row.Label == "Job status" && row.Path == Path.Combine(custom, "Status"));
    }

    [Fact]
    public void DefaultRequiresAnAvailablePersonalSyncRoot()
    {
        var workspace = new Workspace(Path.Combine(root, "private"));
        var settings = new AppSettings();
        Assert.False(OneDriveFolders.ApplyDesktopDefaults(settings, workspace, Path.Combine(root, "Unavailable")));
        Assert.Null(settings.RemoteQueue);
        Assert.Empty(settings.Destination);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
