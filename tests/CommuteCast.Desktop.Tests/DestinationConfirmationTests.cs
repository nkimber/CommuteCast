using System.IO;
using CommuteCast.Core;
using CommuteCast.Desktop;
using CommuteCast.Infrastructure;

namespace CommuteCast.Desktop.Tests;

[Collection("Desktop")]
public sealed class DestinationConfirmationTests
{
    [Fact]
    public Task PreviouslyImpliedExportStaysUnconfirmedAfterChangingTheRemoteFolder() => DesktopHost.Run(async () =>
    {
        var workspace = DesktopHost.Workspace();
        var original = new OneDriveFolders(workspace.Root + "-original-shared");
        var replacement = Directory.CreateDirectory(workspace.Root + "-replacement-shared").FullName;
        var settings = new AppSettings { Destination = original.Listening, RemoteQueue = new(original.Root, false, "Original", "", []) };
        const string source = "This narration stays in the draft until its destination is confirmed.";

        await using (var model = new MainViewModel(settings, workspace, powerEvents: new LifecycleTests.Events(), notifications: new LifecycleTests.Notifications()))
        {
            await model.InitializeAsync();
            Assert.Contains("Choose folder to confirm", model.DestinationDisplay);
            await model.SaveRemoteSettingsAsync(replacement, false, "Replacement", [], false);
            model.Source = source;
            await DesktopHost.Execute(model.QueueCommand);
            Assert.Contains("select or confirm your MP3 output folder", model.AttentionMessage);
            Assert.Equal(source, model.Source);
            Assert.Empty(model.Jobs);
            Assert.False(Directory.Exists(original.Root));
        }

        var saved = await workspace.LoadSettingsAsync();
        Assert.Equal(original.Listening, saved.Destination);
        Assert.Equal(replacement, saved.RemoteQueue?.Folder);
        await using var reopened = new MainViewModel(saved, workspace, powerEvents: new LifecycleTests.Events(), notifications: new LifecycleTests.Notifications());
        Assert.Contains("Choose folder to confirm", reopened.DestinationDisplay);
    });

    [Fact]
    public Task PreviouslyImpliedPersonalQueueRequiresSavingTheDisplayedFolder() => DesktopHost.Run(async () =>
    {
        var workspace = DesktopHost.Workspace();
        var previous = new OneDriveFolders(Directory.CreateDirectory(workspace.Root + "-personal-shared").FullName);
        var settings = new AppSettings { RemoteQueue = new(previous.Root, false, "Personal", "", []) };
        await using var model = new MainViewModel(settings, workspace, powerEvents: new LifecycleTests.Events(),
            notifications: new LifecycleTests.Notifications(), previousPersonalFolders: previous);
        await model.InitializeAsync();

        Assert.True(model.RemoteFolderNeedsConfirmation);
        Assert.Equal(previous.Root, model.OpenRemoteFolder()?.Root);
        await model.SaveRemoteSettingsAsync(previous.Root, false, "Personal", [], false);

        Assert.False(model.RemoteFolderNeedsConfirmation);
        Assert.Equal(previous.Root, (await workspace.LoadSettingsAsync()).ConfirmedRemoteQueueFolder);
        Assert.Empty(settings.Destination);
    });
}
