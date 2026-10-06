using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Text.Json;

namespace CommuteCast.Tests;

public class PrivateJobFilesTests
{
    [Theory]
    [InlineData("unrelated.txt")]
    [InlineData("inference.partial.wav")]
    [InlineData("assembled.wav")]
    [InlineData("encoded.partial.mp3")]
    [InlineData("chunk-00000.wav")]
    public async Task UnknownFilesSurviveDeletionAndCacheCleanupAndKeepDurableIntent(string name)
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace);
        var job = new Job { Stage = JobStage.Failed }; var directory = test.Workspace.JobDirectory(job.Id);
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, name);
        await File.WriteAllTextAsync(path, "Untracked bytes must survive"); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30));
        await store.SaveAsync(job);
        await using (var queue = Queue(test, store))
        {
            await queue.InitializeAsync();
            Assert.Equal(0, (await queue.CleanCacheAsync()).FilesRemoved);
            var result = await queue.DeleteManyAsync([job.Id], false);
            Assert.Equal(1, result.Failed); Assert.Contains("untracked", result.Items[0].Error);
            Assert.True(Assert.Single(await store.LoadAsync()).DeletionRequested);
        }
        await using var reopened = Queue(test, store); await reopened.InitializeAsync();
        Assert.Equal(JobStage.Deleting, Assert.Single(reopened.Snapshot()).Stage);
        Assert.Equal("Untracked bytes must survive", await File.ReadAllTextAsync(path));
        // Explicit external inspection/removal lets the retained intent finish on retry.
        File.Delete(path); await reopened.DeleteAsync(job.Id, false);
        Assert.Empty(await store.LoadAsync()); Assert.False(Directory.Exists(directory));
    }

    [Fact] public async Task ChangedOwnedScratchSurvivesBothRemovalPaths()
    {
        using var test = new TestWorkspace(); var job = new Job { Stage = JobStage.Failed };
        var directory = test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "assembled.wav"); await File.WriteAllTextAsync(path, "Original scratch");
        await PrivateJobFiles.RecordAsync(job, directory, "assembled.wav", default);
        await File.WriteAllTextAsync(path, "Changed scratch"); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30));
        var cleaned = await new CacheMaintenance(test.Workspace).CleanAsync([job], null, 1, 7, default);
        Assert.Equal(0, cleaned.FilesRemoved); Assert.Equal(1, cleaned.Failures);
        await Assert.ThrowsAsync<IOException>(() => PrivateJobFiles.RemoveAsync(job, directory));
        await Assert.ThrowsAsync<IOException>(() => PrivateJobFiles.PrepareOutputAsync(job, directory, "assembled.wav", default));
        Assert.Equal("Changed scratch", await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("inference.partial.wav")]
    [InlineData("normalized.partial.wav")]
    [InlineData("assembled.wav")]
    [InlineData("encoded.partial.mp3")]
    [InlineData("complete.mp3")]
    public async Task GenerationDoesNotAdoptOrOverwriteAnUntrackedOutput(string name)
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace);
        const string text = "This synthetic narration verifies that untracked output bytes are preserved.";
        var job = new Job { Source = text, Prepared = TextPreparation.Prepare(text), Destination = test.Destination,
            Settings = new("kokoro", "af_heart", 1, false, "", "fixture") };
        var directory = test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name); await File.WriteAllTextAsync(path, "Untracked output");
        await using var queue = new QueueCoordinator(test.Workspace, store, new ToneProvider(), new(new()), new(test.Workspace, store));
        await queue.InitializeAsync(); await queue.AddAsync(job);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!queue.Snapshot().Any(j => j.Stage == JobStage.Failed)) await Task.Delay(50, deadline.Token);
        var failed = Assert.Single(await store.LoadAsync()); Assert.Contains("untracked", failed.Error);
        Assert.DoesNotContain(failed.PrivateArtifacts, r => r.RelativePath == name);
        Assert.Equal("Untracked output", await File.ReadAllTextAsync(path)); Assert.Empty(Directory.GetFiles(test.Destination));
    }

    [Fact] public async Task LegacyChunkAndFinalReceiptsRemoveOnlyTheirExactFiles()
    {
        using var test = new TestWorkspace(); var job = new Job(); var directory = test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(test.Workspace.ChunkPath(job, 0), "Legacy chunk");
        await File.WriteAllTextAsync(test.Workspace.FinalPath(job), "Legacy final");
        job.Receipts.Add(new(0, Job.Hash("Legacy chunk"), job.Fingerprint, 1)); job.FinalHash = Job.Hash("Legacy final");
        var fingerprint = job.Fingerprint;
        var legacyPayload = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(job))!.AsObject();
        legacyPayload.Remove(nameof(Job.PrivateArtifacts)); legacyPayload.Remove(nameof(Job.PrivateStorageNotice));
        job = JsonSerializer.Deserialize<Job>(legacyPayload.ToJsonString())!;
        Assert.Empty(job.PrivateArtifacts); Assert.Equal(fingerprint, job.Fingerprint);
        var frozen = JsonSerializer.Serialize(job.Settings); PrivateJobFiles.RetainReceipts(job);
        Assert.Equal(fingerprint, job.Fingerprint); Assert.Equal(frozen, JsonSerializer.Serialize(job.Settings));
        var restored = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(job))!;
        await PrivateJobFiles.RemoveAsync(restored, directory); await PrivateJobFiles.RemoveAsync(restored, directory);
        Assert.False(Directory.Exists(directory));
    }

    [Fact] public async Task SettledReceiptRetryToleratesEarlierRemovalAndAnActualWindowsLock()
    {
        using var test = new TestWorkspace(); var job = new Job { DeletionRequested = true, Stage = JobStage.Deleting };
        var directory = test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        foreach (var name in new[] { "first.wav", "second.wav" })
        { await File.WriteAllTextAsync(Path.Combine(directory, name), name); await PrivateJobFiles.RecordAsync(job, directory, name, default); }
        var store = new SqliteJobStore(test.Workspace); await store.SaveAsync(job);
        using (var handle = new FileStream(Path.Combine(directory, "second.wav"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await using var queue = Queue(test, store); await queue.InitializeAsync();
            Assert.Equal(JobStage.Deleting, Assert.Single(queue.Snapshot()).Stage);
            Assert.False(File.Exists(Path.Combine(directory, "first.wav"))); Assert.True(File.Exists(Path.Combine(directory, "second.wav")));
        }
        await using var reopened = Queue(test, store); await reopened.InitializeAsync();
        Assert.Empty(await store.LoadAsync()); Assert.False(Directory.Exists(directory));
    }

    [Fact] public async Task UnknownNestedDirectoryIsNeverRecursivelyRemoved()
    {
        using var test = new TestWorkspace(); var job = new Job(); var directory = test.Workspace.JobDirectory(job.Id);
        var nested = Path.Combine(directory, "untracked"); Directory.CreateDirectory(nested);
        var path = Path.Combine(nested, "private.txt"); await File.WriteAllTextAsync(path, "Keep nested data");
        await Assert.ThrowsAsync<IOException>(() => PrivateJobFiles.RemoveAsync(job, directory));
        Assert.Equal("Keep nested data", await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("../outside.wav")]
    [InlineData("subdir/file.wav")]
    [InlineData("file.wav:stream")]
    [InlineData("NUL.wav")]
    [InlineData("file.wav.")]
    public async Task MalformedInventoryFailsBeforeRemovingAnyOwnedFile(string unsafeName)
    {
        using var test = new TestWorkspace(); var job = new Job(); var directory = test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "owned.wav"); await File.WriteAllTextAsync(path, "Owned");
        job.PrivateArtifacts.Add(new("owned.wav", Job.Hash("Owned"))); job.PrivateArtifacts.Add(new(unsafeName, Job.Hash("Bad")));
        await Assert.ThrowsAsync<IOException>(() => PrivateJobFiles.RemoveAsync(job, directory)); Assert.True(File.Exists(path));
    }

    [Fact] public async Task ConflictingLegacyAndNewReceiptsAreRefusedBeforeRemoval()
    {
        using var test = new TestWorkspace(); var job = new Job { FinalHash = Job.Hash("Old") };
        var directory = test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(test.Workspace.FinalPath(job), "New"); job.PrivateArtifacts.Add(new("complete.mp3", Job.Hash("New")));
        await Assert.ThrowsAsync<IOException>(() => PrivateJobFiles.RemoveAsync(job, directory));
        Assert.Equal("New", await File.ReadAllTextAsync(test.Workspace.FinalPath(job)));
    }

    private static QueueCoordinator Queue(TestWorkspace test, IJobStore store) => new(test.Workspace, store, new NeverProvider(), new(new()), new(test.Workspace, store)) { Paused = true };
    private sealed class NeverProvider : ISpeechProvider
    {
        public Task<ProviderInfo> ReadyAsync(string engine, CancellationToken ct) => throw new InvalidOperationException("Deletion must not start inference.");
        public Task SynthesizeAsync(NarrationSettings settings, string text, string output, CancellationToken ct) => throw new InvalidOperationException("Deletion must not start inference.");
    }
    private sealed class ToneProvider : ISpeechProvider
    {
        public Task<ProviderInfo> ReadyAsync(string engine, CancellationToken ct) => Task.FromResult(new ProviderInfo(engine, "fixture", ["af_heart"], "ready", 0));
        public Task SynthesizeAsync(NarrationSettings settings, string text, string output, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); TestWorkspace.WriteWave(output, 2); return Task.CompletedTask; }
    }
}
