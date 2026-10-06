using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public class OwnedFileRemovalTests
{
    [Fact] public async Task ValidatedHandleBlocksReplacementAndDeletesOnlyItsRecordedFile()
    {
        using var test = new TestWorkspace(); var file = Path.Combine(test.Workspace.Root, "owned.txt"); var other = Path.Combine(test.Workspace.Root, "keep.txt");
        await File.WriteAllTextAsync(file, "Recorded bytes"); await File.WriteAllTextAsync(other, "Unrelated bytes");
        var receipt = new ReleaseFile("owned.txt", new FileInfo(file).Length, await Workspace.HashFileAsync(file));
        var observer = new Observer(async (path, _) =>
        {
            await Assert.ThrowsAnyAsync<IOException>(() => File.WriteAllTextAsync(path, "Unexpected replacement"));
            Assert.True(Record.Exception(() => File.Move(other, path, true)) is IOException or UnauthorizedAccessException);
            Assert.True(Record.Exception(() => File.Delete(path)) is IOException or UnauthorizedAccessException);
        });
        Assert.True(await OwnedFileRemoval.DeleteAsync(test.Workspace.Root, receipt, observer)); Assert.False(File.Exists(file)); Assert.Equal("Unrelated bytes", await File.ReadAllTextAsync(other));
        Assert.False(await OwnedFileRemoval.DeleteAsync(test.Workspace.Root, receipt));
    }
    [Theory] [InlineData(false)] [InlineData(true)] public async Task ChangedOrCancelledFileIsPreserved(bool cancel)
    {
        using var test = new TestWorkspace(); var file = Path.Combine(test.Workspace.Root, "owned.txt"); await File.WriteAllTextAsync(file, "Original bytes");
        var receipt = new ReleaseFile("owned.txt", new FileInfo(file).Length, await Workspace.HashFileAsync(file)); using var cancellation = new CancellationTokenSource();
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OwnedFileRemoval.DeleteAsync(test.Workspace.Root, receipt, new Observer((_, _) => { cancellation.Cancel(); return Task.CompletedTask; }), cancellation.Token));
        else
        {
            await File.WriteAllTextAsync(file, "Changed content"); await Assert.ThrowsAsync<IOException>(() => OwnedFileRemoval.DeleteAsync(test.Workspace.Root, receipt));
        }
        Assert.True(File.Exists(file)); Assert.Equal(cancel ? "Original bytes" : "Changed content", await File.ReadAllTextAsync(file));
    }
    [Fact] public async Task ExistingWriteLeaseRefusesRemovalWithoutTouchingItsBytes()
    {
        using var test = new TestWorkspace(); var path = Path.Combine(test.Workspace.Root, "owned.txt"); await File.WriteAllTextAsync(path, "Keep bytes");
        var receipt = new ReleaseFile("owned.txt", new FileInfo(path).Length, await Workspace.HashFileAsync(path));
        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        await Assert.ThrowsAsync<IOException>(() => OwnedFileRemoval.DeleteAsync(test.Workspace.Root, receipt)); Assert.True(File.Exists(path));
    }
    [Theory] [InlineData("../outside")] [InlineData("C:/Windows/file")] [InlineData("nested\\file")] [InlineData("file:stream")] [InlineData("dir/./file")]
    public void UnsafeRemovalPathsAreRefused(string relative)
    { using var test = new TestWorkspace(); Assert.Throws<IOException>(() => OwnedFileRemoval.Resolve(test.Workspace.Root, relative)); }
    [Fact] public void EmptyDirectoryCleanupPreservesUnrecognizedFiles()
    {
        using var test = new TestWorkspace(); var root = Path.Combine(test.Workspace.Root, "managed"); Directory.CreateDirectory(Path.Combine(root, "empty", "nested"));
        File.WriteAllText(Path.Combine(root, "unrelated.txt"), "Retained"); OwnedFileRemoval.RemoveEmptyDirectories(root);
        Assert.False(Directory.Exists(Path.Combine(root, "empty"))); Assert.Equal("Retained", File.ReadAllText(Path.Combine(root, "unrelated.txt")));
    }
    private sealed class Observer(Func<string, CancellationToken, Task> validated) : IFileRemovalObserver
    { public Task ValidatedAsync(string path, CancellationToken ct) => validated(path, ct); }
}
