using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public class CacheMaintenanceTests
{
    [Fact] public async Task QuotaCleanupPreservesFinalHistoryExportsAndUnrecognizedFiles()
    {
        using var test = new TestWorkspace(); var job = new Job { Source = "Private source", ExportCommitted = true, Stage = JobStage.Exported };
        var directory = test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(test.Workspace.FinalPath(job), "validated private audio"); job.FinalHash = await Workspace.HashFileAsync(test.Workspace.FinalPath(job));
        var chunk = test.Workspace.ChunkPath(job, 0); await File.WriteAllBytesAsync(chunk, new byte[2 * 1048576]);
        var history = Path.Combine(directory, "source.json"); await File.WriteAllTextAsync(history, "keep");
        var unrelated = Path.Combine(directory, "unrelated.wav"); await File.WriteAllTextAsync(unrelated, "keep");
        var export = Path.Combine(test.Destination, "export.mp3"); await File.WriteAllTextAsync(export, "keep");
        var cleaner = new CacheMaintenance(test.Workspace); var before = await cleaner.MeasureAsync([job], null, default);
        var result = await cleaner.CleanAsync([job], null, 1, 7, default);
        var after = await cleaner.MeasureAsync([job], null, default);
        Assert.Equal(1, result.FilesRemoved); Assert.Equal(2 * 1048576, result.BytesRemoved); Assert.Equal(before.TotalBytes - result.BytesRemoved, after.TotalBytes);
        Assert.False(File.Exists(chunk)); Assert.True(File.Exists(test.Workspace.FinalPath(job))); Assert.True(File.Exists(history)); Assert.True(File.Exists(unrelated)); Assert.True(File.Exists(export)); Assert.Equal("Private source", job.Source);
    }
    [Fact] public async Task OldScratchIsCleanedButRetryReceiptsAndActiveArtifactsAreProtected()
    {
        using var test = new TestWorkspace(); var job = new Job { Stage = JobStage.Failed };
        Directory.CreateDirectory(test.Workspace.JobDirectory(job.Id));
        var chunk = test.Workspace.ChunkPath(job, 0); await File.WriteAllBytesAsync(chunk, new byte[2 * 1048576]);
        job.Receipts.Add(new(0, "hash", job.Fingerprint, 1)); File.SetLastWriteTimeUtc(chunk, DateTime.UtcNow.AddDays(-30));
        var scratch = Path.Combine(test.Workspace.JobDirectory(job.Id), "inference.partial.wav"); await File.WriteAllTextAsync(scratch, "partial"); File.SetLastWriteTimeUtc(scratch, DateTime.UtcNow.AddDays(-30));
        var cleaner = new CacheMaintenance(test.Workspace);
        var active = await cleaner.CleanAsync([job], job.Id, 1, 7, default); Assert.Equal(0, active.FilesRemoved); Assert.True(File.Exists(scratch));
        var result = await cleaner.CleanAsync([job], null, 1, 7, default); Assert.Equal(1, result.FilesRemoved); Assert.True(File.Exists(chunk)); Assert.False(File.Exists(scratch));
    }
    [Fact] public async Task MissingOrChangedFinalDoesNotMakeReceiptChunksReclaimable()
    {
        using var test = new TestWorkspace(); var job = new Job { ExportCommitted = true, FinalHash = "original" };
        Directory.CreateDirectory(test.Workspace.JobDirectory(job.Id));
        var chunk = test.Workspace.ChunkPath(job, 0); await File.WriteAllBytesAsync(chunk, new byte[2 * 1048576]); job.Receipts.Add(new(0, "hash", job.Fingerprint, 1));
        var cleaner = new CacheMaintenance(test.Workspace);
        Assert.Equal(0, (await cleaner.CleanAsync([job], null, 1, 7, default)).FilesRemoved);
        await File.WriteAllTextAsync(test.Workspace.FinalPath(job), "changed");
        Assert.Equal(0, (await cleaner.CleanAsync([job], null, 1, 7, default)).FilesRemoved); Assert.True(File.Exists(chunk));
    }
    [Fact] public async Task CancelledCleanupLeavesFilesAndTombstonesUntouched()
    {
        using var test = new TestWorkspace(); var job = new Job { DeletionRequested = true };
        Directory.CreateDirectory(test.Workspace.JobDirectory(job.Id)); var scratch = Path.Combine(test.Workspace.JobDirectory(job.Id), "assembled.wav"); await File.WriteAllBytesAsync(scratch, new byte[2 * 1048576]);
        var cleaner = new CacheMaintenance(test.Workspace);
        Assert.Equal(0, (await cleaner.CleanAsync([job], null, 1, 7, default)).FilesRemoved);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cleaner.CleanAsync([job], null, 1, 7, cancel.Token)); Assert.True(File.Exists(scratch));
    }
}
