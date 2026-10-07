using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public partial class ProviderContractTests
{
    [Theory] [InlineData("kokoro")] [InlineData("piper")]
    public async Task DurableSpeechSavesCreationAndBothRenameIdentitiesUnderOriginalHandle(string engine)
    {
        using var fixture = new Fixture(engine); using var provider = fixture.Provider();
        var job = new Job(); var store = new SqliteJobStore(fixture.Test.Workspace);
        var directory = fixture.Test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, "inference.partial.wav"); var saves = 0; ExportStagingIdentity? original = null;
        fixture.Http.Speech = (_, _) => Task.FromResult(fixture.AudioContent(new StreamContent(new ObservedStream(fixture.Wave, () => Assert.NotNull(original)))));
        await provider.SynthesizeAsync(job, fixture.Settings, "exact source", output, async () =>
        {
            await store.SaveAsync(job); var saved = Assert.Single(await store.LoadAsync());
            if (++saves == 1)
            {
                var receipt = Assert.Single(saved.PrivateArtifacts); original = receipt.CreationIdentity;
                Assert.NotNull(original); Assert.Equal("", receipt.Hash);
                Assert.Equal(0, new FileInfo(Path.Combine(directory, receipt.RelativePath)).Length);
                Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(directory, receipt.RelativePath), "replacement"));
            }
            else if (saves == 2)
            {
                Assert.True(fixture.Http.Settles > 0); Assert.False(File.Exists(output));
                Assert.Equal(2, saved.PrivateArtifacts.Count);
                Assert.All(saved.PrivateArtifacts, receipt => { Assert.Equal(original, receipt.PromotionIdentity); Assert.Null(receipt.CreationIdentity); Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fixture.Wave)), receipt.Hash); });
            }
            else
            {
                var receipt = Assert.Single(saved.PrivateArtifacts); Assert.Equal("inference.partial.wav", receipt.RelativePath);
                Assert.Null(receipt.PromotionIdentity); Assert.Null(receipt.CreationIdentity);
                Assert.Throws<IOException>(() => File.WriteAllText(output, "replacement"));
            }
        }, default);
        Assert.Equal(3, saves); Assert.Equal(fixture.Wave, await File.ReadAllBytesAsync(output));
        Assert.Equal(await Workspace.HashFileAsync(output), PrivateJobFiles.Inventory(Assert.Single(await store.LoadAsync()))["inference.partial.wav"]);
        Assert.Empty(Directory.GetFiles(directory, "*.attempt-*.partial"));
    }

    [Theory] [InlineData(1, false)] [InlineData(1, true)] [InlineData(2, false)] [InlineData(2, true)] [InlineData(3, false)] [InlineData(3, true)]
    public async Task DurableSpeechCheckpointFailureRetiresOnlyHeldBytesAndAllowsRetry(int failure, bool afterSave)
    {
        using var fixture = new Fixture(); using var provider = fixture.Provider(); var job = new Job(); var store = new SqliteJobStore(fixture.Test.Workspace);
        var directory = fixture.Test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        var unknown = Path.Combine(directory, "unknown.txt"); await File.WriteAllTextAsync(unknown, "preserve");
        var output = Path.Combine(directory, "inference.partial.wav"); await store.SaveAsync(job); var saves = 0;
        await Assert.ThrowsAsync<IOException>(() => provider.SynthesizeAsync(job, fixture.Settings, "source", output, async () =>
        {
            saves++; if (saves == failure && !afterSave) throw new IOException("Injected checkpoint failure");
            await store.SaveAsync(job); if (saves == failure) throw new IOException("Injected acknowledgement loss");
        }, default));
        Assert.False(File.Exists(output)); Assert.Empty(Directory.GetFiles(directory, "*.attempt-*.partial")); Assert.Empty(job.PrivateArtifacts);
        var reopened = Assert.Single(await store.LoadAsync());
        await PrivateJobFiles.ReconcilePromotionsAsync(reopened, directory, () => store.SaveAsync(reopened), default);
        await provider.SynthesizeAsync(reopened, fixture.Settings, "retry", output, () => store.SaveAsync(reopened), default);
        Assert.Equal(fixture.Wave, await File.ReadAllBytesAsync(output)); Assert.Equal("preserve", await File.ReadAllTextAsync(unknown));
        Assert.Equal(await Workspace.HashFileAsync(output), PrivateJobFiles.Inventory(Assert.Single(await store.LoadAsync()))["inference.partial.wav"]);
    }

    [Fact] public async Task DurableSpeechNeverAdoptsIdenticalUnknownOutputRacingRename()
    {
        using var fixture = new Fixture(); using var provider = fixture.Provider(); var job = new Job(); var store = new SqliteJobStore(fixture.Test.Workspace);
        var directory = fixture.Test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, "inference.partial.wav"); var saves = 0;
        await Assert.ThrowsAsync<IOException>(() => provider.SynthesizeAsync(job, fixture.Settings, "source", output, async () =>
        {
            await store.SaveAsync(job); if (++saves == 2) await File.WriteAllBytesAsync(output, fixture.Wave);
        }, default));
        Assert.Equal(fixture.Wave, await File.ReadAllBytesAsync(output)); Assert.Empty(job.PrivateArtifacts);
        var reopened = Assert.Single(await store.LoadAsync());
        await Assert.ThrowsAsync<IOException>(() => PrivateJobFiles.ReconcilePromotionsAsync(reopened, directory, () => store.SaveAsync(reopened), default));
        Assert.Equal(fixture.Wave, await File.ReadAllBytesAsync(output));
        Assert.Empty(Directory.GetFiles(directory, "*.attempt-*.partial"));
    }

    [Fact] public async Task DurableSpeechRefusesAnotherJobsDirectoryBeforeRuntimeOrHttp()
    {
        using var fixture = new Fixture(); using var provider = fixture.Provider(); var job = new Job();
        await Assert.ThrowsAsync<IOException>(() => provider.SynthesizeAsync(job, fixture.Settings, "source", fixture.Output, () => Task.CompletedTask, default));
        Assert.Empty(fixture.Runtime.Calls); Assert.Empty(fixture.Http.Uris);
    }

    [Fact] public async Task DurableSpeechCancellationDuringBodyCopyLeavesRecoverableSavedIdentity()
    {
        using var fixture = new Fixture(); using var provider = fixture.Provider(); var job = new Job(); var store = new SqliteJobStore(fixture.Test.Workspace);
        var directory = fixture.Test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, "inference.partial.wav"); var stream = new DurableBlockedBody(fixture.Wave);
        fixture.Http.Speech = (_, _) => Task.FromResult(fixture.AudioContent(new StreamContent(stream)));
        using var cancellation = new CancellationTokenSource();
        var operation = provider.SynthesizeAsync(job, fixture.Settings, "source", output, () => store.SaveAsync(job), cancellation.Token);
        await stream.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var saved = Assert.Single(await store.LoadAsync()); var receipt = Assert.Single(saved.PrivateArtifacts);
        Assert.NotNull(receipt.CreationIdentity); Assert.Equal("", receipt.Hash);
        var partial = Path.Combine(directory, receipt.RelativePath); Assert.True(new FileInfo(partial).Length > 0);
        Assert.Throws<IOException>(() => File.WriteAllText(partial, "replacement"));
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.False(File.Exists(partial)); Assert.False(File.Exists(output));
        await PrivateJobFiles.ReconcilePromotionsAsync(saved, directory, () => store.SaveAsync(saved), default);
        Assert.Empty(Assert.Single(await store.LoadAsync()).PrivateArtifacts);
        fixture.Http.Speech = null;
        await provider.SynthesizeAsync(saved, fixture.Settings, "retry", output, () => store.SaveAsync(saved), default);
        Assert.Equal(fixture.Wave, await File.ReadAllBytesAsync(output));
    }

    private sealed class DurableBlockedBody(byte[] prefix) : BlockedStream
    {
        private bool started;
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!started) { started = true; buffer.Span.Clear(); prefix.AsSpan().CopyTo(buffer.Span); return buffer.Length; }
            Blocked.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return 0;
        }
    }

    [Theory] [InlineData("kokoro")] [InlineData("piper")]
    public async Task QueueUsesDurableLocalProviderAndPublishesOneValidatedMp3(string engine)
    {
        using var fixture = new Fixture(engine); using var provider = fixture.Provider(); var store = new SqliteJobStore(fixture.Test.Workspace);
        var source = "One faithful source sentence.";
        var job = new Job { Source = source, Prepared = TextPreparation.Prepare(source), Settings = fixture.Settings, Destination = fixture.Test.Destination };
        job.Chunks = Chunker.Split(job.Prepared.Script, 450);
        var sawDurableCreation = false;
        fixture.Http.Speech = (_, _) => Task.FromResult(fixture.AudioContent(new StreamContent(new ObservedStream(fixture.Wave, () =>
        {
            var saved = store.LoadAsync().GetAwaiter().GetResult().Single();
            sawDurableCreation = saved.PrivateArtifacts.Any(r => r.CreationIdentity is not null);
            Assert.True(sawDurableCreation);
        }))));
        await using var queue = new QueueCoordinator(fixture.Test.Workspace, store, provider, new(new()), new(fixture.Test.Workspace, store));
        await queue.InitializeAsync(); await queue.AddAsync(job);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (queue.Snapshot().Single().Stage is not (JobStage.Exported or JobStage.Failed))
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "Queue did not finish."); await Task.Delay(50);
        }
        var result = Assert.Single(await store.LoadAsync()); Assert.True(result.Stage == JobStage.Exported, result.Error);
        Assert.True(sawDurableCreation); Assert.Equal(source, result.Source); Assert.Equal(1, fixture.Http.Posts);
        Assert.DoesNotContain(result.PrivateArtifacts, r => r.CreationIdentity is not null || r.PromotionIdentity is not null);
        Assert.Single(Directory.GetFiles(fixture.Test.Destination, "*.mp3"));
        Assert.Equal(result.ExportHash, await Workspace.HashFileAsync(Path.Combine(result.Destination, result.ExportName)));
    }
}
