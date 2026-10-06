using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public class PrivateWriteTests
{
    [Theory]
    [InlineData("write")]
    [InlineData("cancel")]
    [InlineData("checkpoint")]
    [InlineData("after-save")]
    public async Task HandledFailureRemovesOnlyTheHeldCreationAndAllowsDurableRetry(string failure)
    {
        using var test = new TestWorkspace(); var job = new Job(); var store = new SqliteJobStore(test.Workspace);
        var directory = test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "unknown.txt"), "Preserve");
        using var cancellation = new CancellationTokenSource();
        var operation = PrivateJobFiles.WriteRecordedAsync(job, directory, "assembled.wav", async output =>
        {
            await output.WriteAsync(new byte[] { 1, 2, 3 });
            if (failure == "write") throw new IOException("Injected write failure");
            if (failure == "cancel") cancellation.Cancel();
        }, async () =>
        {
            if (failure == "after-save") await store.SaveAsync(job);
            throw new IOException("Injected checkpoint failure");
        }, cancellation.Token);
        if (failure == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        else await Assert.ThrowsAsync<IOException>(() => operation);
        Assert.False(File.Exists(Path.Combine(directory, "assembled.wav")));
        Assert.DoesNotContain(job.PrivateArtifacts, r => r.RelativePath == "assembled.wav");
        Assert.Equal("Preserve", await File.ReadAllTextAsync(Path.Combine(directory, "unknown.txt")));
        var retry = failure == "after-save" ? Assert.Single(await store.LoadAsync()) : job;
        await PrivateJobFiles.WriteRecordedAsync(retry, directory, "assembled.wav",
            stream => stream.WriteAsync(new byte[] { 4, 5, 6 }).AsTask(), () => store.SaveAsync(retry), default);
        Assert.Equal(await Workspace.HashFileAsync(Path.Combine(directory, "assembled.wav")),
            PrivateJobFiles.Inventory(Assert.Single(await store.LoadAsync()))["assembled.wav"]);
    }

    [Fact] public async Task ReceiptIsDurableBeforeTheCreationHandleCanBeReplaced()
    {
        using var test = new TestWorkspace(); var job = new Job(); var store = new SqliteJobStore(test.Workspace);
        var directory = test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "assembled.wav");
        await PrivateJobFiles.WriteRecordedAsync(job, directory, "assembled.wav",
            stream => stream.WriteAsync(new byte[] { 7, 8, 9 }).AsTask(), async () =>
            {
                Assert.Throws<IOException>(() => File.Delete(path));
                Assert.Throws<IOException>(() => File.WriteAllBytes(path, new byte[] { 7, 8, 9 }));
                await store.SaveAsync(job);
            }, default);
        await PrivateJobFiles.RemoveAsync(Assert.Single(await store.LoadAsync()), directory);
        Assert.False(Directory.Exists(directory));
    }

    [Fact] public async Task UnknownOutputIsPreservedBeforeTheWriterRuns()
    {
        using var test = new TestWorkspace(); var job = new Job();
        var directory = test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "assembled.wav"); await File.WriteAllTextAsync(path, "Unknown");
        await Assert.ThrowsAsync<IOException>(() => PrivateJobFiles.WriteRecordedAsync(job, directory, "assembled.wav",
            _ => throw new InvalidOperationException("Must not write"), () => throw new InvalidOperationException("Must not save"), default));
        Assert.Equal("Unknown", await File.ReadAllTextAsync(path)); Assert.Empty(job.PrivateArtifacts);
    }
}
