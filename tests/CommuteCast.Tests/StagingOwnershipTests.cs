using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Text.Json;

namespace CommuteCast.Tests;

public class StagingOwnershipTests
{
    [Fact]
    public async Task UnicodeTitleAtTheFilenameLimitRemainsAddressableAfterHandleRename()
    {
        using var test = new TestWorkspace(); var (job, store, _, bytes) = await SeedAsync(test, 81920);
        job.Title = new string('x', 69) + "🚆 end"; job.ExportName = ExportPublisher.Filename(job); await store.SaveAsync(job);
        Assert.DoesNotContain("\uD83D", job.ExportName);
        await new ExportPublisher(test.Workspace, store).PublishAsync(job, default);
        Assert.True(job.ExportCommitted); Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(test.Destination, job.ExportName)));
        Assert.Equal(job.Title, Assert.Single(await store.LoadAsync()).Title);
    }
    [Theory] [InlineData(0)] [InlineData(81920)] [InlineData(180000)]
    public async Task DurableIdentityAndVerifiedPrefixResumeTheSameFileWithoutTruncation(int length)
    {
        using var test = new TestWorkspace(); var (job, store, path, bytes) = await SeedAsync(test, length);
        var identity = job.ExportStagingIdentity; var observed = false;
        var observer = new Observer((point, _) =>
        {
            if (point == ExportCheckpoint.StagingResumed) { observed = true; Assert.Equal(length, new FileInfo(path).Length); }
            return Task.CompletedTask;
        });
        var restored = Assert.Single(await store.LoadAsync()); Assert.Equal(identity, restored.ExportStagingIdentity);
        Assert.False(restored.ExportStagingOwned); // Older readers cannot use a boolean to erase this partial.
        await new ExportPublisher(test.Workspace, store, observer).PublishAsync(restored, default);
        Assert.True(observed); Assert.Null(restored.ExportStagingIdentity); Assert.True(restored.ExportCommitted);
        var final = Path.Combine(test.Destination, restored.ExportName); Assert.Equal(bytes, await File.ReadAllBytesAsync(final));
        await using var reopened = ExportStagingFile.OpenIfPresent(test.Destination, restored.ExportName);
        Assert.NotNull(reopened); Assert.Equal(identity, reopened.Identity); Assert.False(File.Exists(path));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ReplacementWithIdenticalBytesCannotUseThePreviousFilesIdentity(bool complete)
    {
        using var test = new TestWorkspace(); var (job, store, path, bytes) = await SeedAsync(test, complete ? 180000 : 81920);
        File.Delete(path); await File.WriteAllBytesAsync(path, bytes[..(complete ? bytes.Length : 81920)]);
        await using (var replacement = ExportStagingFile.OpenIfPresent(test.Destination, Path.GetFileName(path)))
            Assert.NotEqual(job.ExportStagingIdentity, replacement!.Identity);
        var publisher = new ExportPublisher(test.Workspace, store); var before = await Workspace.HashFileAsync(path);
        await Assert.ThrowsAsync<IOException>(() => publisher.PublishAsync(job, default));
        await Assert.ThrowsAsync<IOException>(() => publisher.RemoveOwnedStagingAsync(job, default));
        Assert.Equal(before, await Workspace.HashFileAsync(path)); Assert.False(job.ExportCommitted);
        Assert.Empty(Directory.GetFiles(test.Destination, "*.mp3"));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SameFileChangedOrExtendedBeyondTheSourceIsPreserved(bool extended)
    {
        using var test = new TestWorkspace(); var (job, store, path, bytes) = await SeedAsync(test, 81920);
        if (extended) await File.WriteAllBytesAsync(path, bytes.Concat(new byte[] { 99 }).ToArray());
        else { using var writer = new FileStream(path, FileMode.Open, FileAccess.Write); writer.WriteByte(255); writer.Flush(true); }
        await using (var changed = ExportStagingFile.OpenIfPresent(test.Destination, Path.GetFileName(path))) Assert.Equal(job.ExportStagingIdentity, changed!.Identity);
        var before = await Workspace.HashFileAsync(path); var publisher = new ExportPublisher(test.Workspace, store);
        await Assert.ThrowsAsync<IOException>(() => publisher.PublishAsync(job, default));
        await Assert.ThrowsAsync<IOException>(() => publisher.RemoveOwnedStagingAsync(job, default));
        Assert.Equal(before, await Workspace.HashFileAsync(path)); Assert.Equal(job.ExportStagingIdentity, Assert.Single(await store.LoadAsync()).ExportStagingIdentity);
    }

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task IncompatibleOrChangedIdentityRefusesRemovalEvenForACompleteCopy(int change)
    {
        using var test = new TestWorkspace(); var (job, store, path, _) = await SeedAsync(test, 180000);
        var identity = job.ExportStagingIdentity!;
        job.ExportStagingIdentity = change switch
        {
            0 => identity with { FormatVersion = 2 }, 1 => identity with { FileId = new string('0', 32) },
            2 => identity with { VolumeSerialNumber = identity.VolumeSerialNumber ^ 1 }, _ => identity with { CreationFileTime = identity.CreationFileTime + 1 }
        };
        await Assert.ThrowsAsync<IOException>(() => new ExportPublisher(test.Workspace, store).RemoveOwnedStagingAsync(job, default));
        Assert.Equal(job.ExportHash, await Workspace.HashFileAsync(path));
    }

    [Fact]
    public async Task LegacyBooleanCannotRemoveAnIncompleteFileButCompleteLegacyCopyCanPublish()
    {
        using var test = new TestWorkspace(); var (job, store, path, bytes) = await SeedAsync(test, 81920);
        job.ExportStagingIdentity = null; job.ExportStagingOwned = true; await store.SaveAsync(job);
        var publisher = new ExportPublisher(test.Workspace, store);
        await Assert.ThrowsAsync<IOException>(() => publisher.PublishAsync(job, default));
        await Assert.ThrowsAsync<IOException>(() => publisher.RemoveOwnedStagingAsync(job, default));
        Assert.Equal(bytes[..81920], await File.ReadAllBytesAsync(path));
        await File.WriteAllBytesAsync(path, bytes);
        await publisher.PublishAsync(job, default); Assert.True(job.ExportCommitted); Assert.False(job.ExportStagingOwned);
    }

    [Theory] [InlineData(ExportCheckpoint.BeforeRename)] [InlineData(ExportCheckpoint.StagingRemovalValidated)]
    public async Task AnotherProcessCannotReplaceOrChangeTheStagingHandleDuringCommitOrRemoval(ExportCheckpoint boundary)
    {
        using var test = new TestWorkspace(); var (job, store, path, bytes) = await SeedAsync(test, 81920);
        var other = Path.Combine(test.Destination, "unrelated.mp3"); await File.WriteAllTextAsync(other, "Keep audio"); var observed = false;
        var observer = new Observer(async (point, _) =>
        {
            if (point != boundary) return;
            observed = true; using var result = await ExportRemovalTests.CompeteAsync(path, other);
            foreach (var operation in new[] { "write", "replace", "delete" }) Assert.Equal("refused", result.RootElement.GetProperty(operation).GetString());
        });
        var publisher = new ExportPublisher(test.Workspace, store, observer);
        if (boundary == ExportCheckpoint.BeforeRename) await publisher.PublishAsync(job, default);
        else await publisher.RemoveOwnedStagingAsync(job, default);
        Assert.True(observed); Assert.False(File.Exists(path)); Assert.Equal("Keep audio", await File.ReadAllTextAsync(other));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(test.Workspace.FinalPath(job)));
        if (boundary == ExportCheckpoint.BeforeRename) Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(test.Destination, job.ExportName)));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CancelledOrFailedResumePreservesItsExistingVerifiedPrefixForAnotherAttempt(bool cancel)
    {
        using var test = new TestWorkspace(); var (job, store, path, bytes) = await SeedAsync(test, 81920); using var cancellation = new CancellationTokenSource();
        var observer = new Observer((point, _) =>
        {
            if (point == ExportCheckpoint.StagingResumed) { if (cancel) cancellation.Cancel(); else throw new IOException("Injected resume interruption"); }
            return Task.CompletedTask;
        });
        var publisher = new ExportPublisher(test.Workspace, store, observer);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publisher.PublishAsync(job, cancellation.Token));
        else await Assert.ThrowsAsync<IOException>(() => publisher.PublishAsync(job, default));
        Assert.Equal(bytes[..81920], await File.ReadAllBytesAsync(path)); Assert.NotNull(job.ExportStagingIdentity);
        await new ExportPublisher(test.Workspace, store).PublishAsync(job, default); Assert.True(job.ExportCommitted);
    }

    private static async Task<(Job Job, SqliteJobStore Store, string Path, byte[] Bytes)> SeedAsync(TestWorkspace test, int length)
    {
        var job = new Job { Title = "Staging identity fixture", Destination = test.Destination, Stage = JobStage.Exporting };
        var bytes = Enumerable.Range(0, 180000).Select(i => (byte)(i % 251)).ToArray();
        Directory.CreateDirectory(test.Workspace.JobDirectory(job.Id)); await File.WriteAllBytesAsync(test.Workspace.FinalPath(job), bytes);
        job.FinalHash = job.ExportHash = await Workspace.HashFileAsync(test.Workspace.FinalPath(job)); job.ExportName = ExportPublisher.Filename(job);
        var path = Path.Combine(test.Destination, $".commutecast-{job.Id}.partial");
        await using (var staging = ExportStagingFile.Create(test.Destination, Path.GetFileName(path)))
        { await staging.Stream.WriteAsync(bytes.AsMemory(0, length)); staging.Stream.Flush(true); job.ExportStagingIdentity = staging.Identity; }
        var store = new SqliteJobStore(test.Workspace); await store.SaveAsync(job);
        return (job, store, path, bytes);
    }

    private sealed class Observer(Func<ExportCheckpoint, CancellationToken, Task> reached) : IExportObserver
    { public Task ReachedAsync(ExportCheckpoint checkpoint, CancellationToken ct) => reached(checkpoint, ct); }
}
