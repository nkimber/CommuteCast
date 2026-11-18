using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public sealed class OneDriveFoldersTests
{
    [Fact]
    public void PreviouslyImpliedFolderRequiresConfirmationForThatPath()
    {
        var previous = Path.Combine(Path.GetTempPath(), "Personal OneDrive", "CommuteCast");
        var custom = Path.Combine(Path.GetTempPath(), "Work OneDrive", "CommuteCast");

        Assert.True(OneDriveFolders.NeedsConfirmation(previous, null, previous));
        Assert.True(OneDriveFolders.NeedsConfirmation(previous, custom, previous));
        Assert.False(OneDriveFolders.NeedsConfirmation(previous, previous + Path.DirectorySeparatorChar, previous));
        Assert.False(OneDriveFolders.NeedsConfirmation(custom, null, previous));
        Assert.False(OneDriveFolders.NeedsConfirmation("", null, previous));
    }

    [Fact]
    public void AdminShowsBothFolderChoicesSeparately()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "private");
        var shared = Path.Combine(Path.GetTempPath(), "shared");
        var exports = Path.Combine(Path.GetTempPath(), "mp3s");
        var locations = OneDriveFolders.Describe(shared, exports, workspace, Path.Combine(workspace, "logs"));

        Assert.Contains(locations, row => row.Label == "Job status" && row.Path == Path.Combine(shared, "Status"));
        Assert.Contains(locations, row => row.Label == "Remote worker MP3s" && row.Path == Path.Combine(shared, "Ready to Listen"));
        Assert.Contains(locations, row => row.Label == "Local generation MP3 exports" && row.Path == exports);
    }
}
