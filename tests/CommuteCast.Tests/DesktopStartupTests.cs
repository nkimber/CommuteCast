using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public class DesktopStartupTests
{
    [Fact] public void EntryPointAndExplicitModeRemainDistinct()
    {
        Assert.Equal(DesktopMode.Editor, DesktopStartup.Parse([]).Mode); Assert.Equal(DesktopMode.Setup, DesktopStartup.Parse([], true).Mode);
        Assert.Equal(DesktopMode.Setup, DesktopStartup.Parse(["--setup"]).Mode); Assert.Equal(DesktopMode.Maintenance, DesktopStartup.Parse(["--maintenance"]).Mode);
        var path = Path.Combine(Path.GetTempPath(), "setup target with spaces"); Assert.Equal(path, DesktopStartup.Parse(["--install-root", path], true).InstallRoot);
    }
    [Theory] [InlineData("--root", "missing")] [InlineData("--maintenance", "--root")] [InlineData("--setup", "--root")]
    [InlineData("--setup", "--unknown")] [InlineData("--install-root", "target")]
    public void IncorrectModeOptionsCannotSilentlySelectAnotherWorkspace(string first, string second)
    { Assert.Throws<ArgumentException>(() => DesktopStartup.Parse([first, second])); }
    [Fact] public void DuplicateSetupPathsAreRefused()
    { Assert.Throws<ArgumentException>(() => DesktopStartup.Parse(["--setup", "--root", Path.GetTempPath(), "--root", Path.GetTempPath()])); }
    [Fact] public void AnOptionCannotBeUsedAsAnotherOptionsFolder()
    { Assert.Throws<ArgumentException>(() => DesktopStartup.Parse(["--setup", "--install-root", "--root", Path.GetTempPath()])); }
}
