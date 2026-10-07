using CommuteCast.Core;
using CommuteCast.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace CommuteCast.Tests;

public partial class ProviderContractTests
{
    [Theory] [InlineData("kokoro")] [InlineData("piper")]
    public async Task DurableAuditionKeepsSourceOutOfHistoryAndRetainsOriginalIdentityThroughCleanup(string engine)
    {
        using var fixture = new Fixture(engine); using var provider = fixture.Provider(); using var gate = new SemaphoreSlim(1);
        var generator = new AuditionGenerator(fixture.Test.Workspace, provider, gate); var store = new AuditionOwnershipStore(fixture.Test.Workspace);
        const string source = "Never persist this selected secret preview."; var sawCreation = false;
        fixture.Http.Speech = (_, _) => Task.FromResult(fixture.AudioContent(new StreamContent(new ObservedStream(fixture.Wave, () =>
        {
            var saved = store.LoadAsync().GetAwaiter().GetResult().Single();
            Assert.NotNull(Assert.Single(saved.Artifacts).CreationIdentity); sawCreation = true;
        }))));
        var audio = await generator.GenerateAsync(AuditionRequest.Selection(source, 0, source.Length, fixture.Settings), default);
        Assert.True(sawCreation); Assert.NotNull(audio.OwnershipId);
        var record = Assert.Single(await store.LoadAsync()); var receipt = Assert.Single(record.Artifacts);
        Assert.NotNull(receipt.PromotionIdentity); Assert.Null(receipt.CreationIdentity); Assert.Equal(audio.Hash, receipt.Hash);
        var payload = JsonSerializer.Serialize(record); Assert.DoesNotContain(source, payload); Assert.DoesNotContain("secret", payload);
        using (var json = JsonDocument.Parse(payload)) Assert.Equal(new[] { "Id", "CreatedUtc", "Artifacts" }, json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Empty(await new SqliteJobStore(fixture.Test.Workspace).LoadAsync()); Assert.Empty(Directory.GetFiles(fixture.Test.Destination));
        using var lease = WorkspaceLease.Acquire(fixture.Test.Workspace);
        await Assert.ThrowsAsync<IOException>(() => WorkspaceBackup.CreateAsync(lease));
        Assert.Equal(fixture.Wave, await File.ReadAllBytesAsync(OwnedFileRemoval.Resolve(fixture.Test.Workspace.Root, audio.RelativePath)));
        Assert.True(await generator.RemoveAsync(audio)); Assert.False(await generator.RemoveAsync(audio)); Assert.Empty(await store.LoadAsync());
        await WorkspaceBackup.CreateAsync(lease);
    }

    [Fact] public async Task CompletedPreviewIdenticalReplacementIsPreservedByStopAndStartupRecovery()
    {
        using var fixture = new Fixture(); using var provider = fixture.Provider(); using var gate = new SemaphoreSlim(1);
        var generator = new AuditionGenerator(fixture.Test.Workspace, provider, gate);
        var audio = await generator.GenerateAsync(AuditionRequest.Standard(fixture.Settings), default);
        var path = OwnedFileRemoval.Resolve(fixture.Test.Workspace.Root, audio.RelativePath);
        File.Move(path, Path.Combine(fixture.Test.Parent, "preserved-original.wav")); await File.WriteAllBytesAsync(path, fixture.Wave);
        await Assert.ThrowsAsync<IOException>(() => generator.RemoveAsync(audio));
        await Assert.ThrowsAsync<IOException>(() => generator.RecoverAsync());
        Assert.Equal(fixture.Wave, await File.ReadAllBytesAsync(path)); Assert.Single(await new AuditionOwnershipStore(fixture.Test.Workspace).LoadAsync());
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact] public async Task DurablePreviewOutputScopeIsRefusedBeforeRuntimeOrHttp()
    {
        using var fixture = new Fixture(); using var provider = fixture.Provider();
        var journal = new AuditionWriteJournal(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, []);
        await Assert.ThrowsAsync<IOException>(() => provider.SynthesizeAuditionAsync(journal, fixture.Settings, "source", fixture.Output, () => Task.CompletedTask, default));
        Assert.Empty(fixture.Runtime.Calls); Assert.Empty(fixture.Http.Uris);
    }
}

public class AuditionOwnershipTests
{
    [Theory] [InlineData("creation")] [InlineData("before-rename")] [InlineData("after-rename")]
    public async Task StartupRemovesOnlyOriginalRecordedPreviewAtInterruptedBoundaries(string point)
    {
        using var test = new TestWorkspace(); using var gate = new SemaphoreSlim(1); var store = new AuditionOwnershipStore(test.Workspace);
        var journal = new AuditionWriteJournal(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, []);
        var directory = Path.Combine(test.Workspace.Root, "auditions"); Directory.CreateDirectory(directory);
        var source = "audition-" + journal.Id + ".wav.attempt-" + Guid.NewGuid().ToString("N") + ".partial"; var target = "audition-" + journal.Id + ".wav";
        await File.WriteAllTextAsync(Path.Combine(directory, "unknown.wav"), "preserve");
        await using (var held = ExportStagingFile.Create(directory, source))
        {
            await held.Stream.WriteAsync(new byte[] { 1, 2, 3 }); journal.Artifacts.Add(new(source, "", CreationIdentity: held.Identity)); await store.SaveAsync(journal);
            if (point != "creation")
            {
                var saves = 0;
                await Assert.ThrowsAsync<IOException>(() => PrivateJobFiles.CompleteAndMoveCreatedAsync(new Job { PrivateArtifacts = journal.Artifacts }, directory, source, target, held, async () =>
                {
                    saves++;
                    if (saves == 2) throw new IOException("Loss before final save");
                    await store.SaveAsync(journal); if (point == "before-rename") throw new IOException("Loss before rename");
                }, default, retainDestinationIdentity: true));
            }
        }
        var generator = new AuditionGenerator(test.Workspace, new NeverProvider(), gate);
        Assert.Equal(1, await generator.RecoverAsync()); Assert.Equal(0, await generator.RecoverAsync());
        Assert.False(File.Exists(Path.Combine(directory, source))); Assert.False(File.Exists(Path.Combine(directory, target)));
        Assert.Equal("preserve", await File.ReadAllTextAsync(Path.Combine(directory, "unknown.wav"))); Assert.Empty(await store.LoadAsync());
        Assert.Empty(await new SqliteJobStore(test.Workspace).LoadAsync());
    }

    [Fact] public async Task UnrecordedOutputBeforeIdentityCheckpointPreservesIntentAndUnknownBytes()
    {
        using var test = new TestWorkspace(); using var gate = new SemaphoreSlim(1); var store = new AuditionOwnershipStore(test.Workspace);
        var journal = new AuditionWriteJournal(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, []); await store.SaveAsync(journal);
        var directory = Path.Combine(test.Workspace.Root, "auditions"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "audition-" + journal.Id + ".wav.attempt-" + Guid.NewGuid().ToString("N") + ".partial"); await File.WriteAllTextAsync(path, "unknown creation");
        var generator = new AuditionGenerator(test.Workspace, new NeverProvider(), gate);
        await Assert.ThrowsAsync<IOException>(() => generator.RecoverAsync()); Assert.Equal("unknown creation", await File.ReadAllTextAsync(path));
        Assert.Single(await store.LoadAsync()); Assert.Equal(1, gate.CurrentCount);
    }

    [Fact] public async Task InvalidReceiptCannotEnterOwnershipOrDeleteAnotherFile()
    {
        using var test = new TestWorkspace(); var store = new AuditionOwnershipStore(test.Workspace);
        var journal = new AuditionWriteJournal(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            [new("other-user.wav", new string('A', 64), new(1, 1, new string('B', 32), 1))]);
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(journal)); Assert.False(File.Exists(Path.Combine(test.Workspace.Root, "queue.db")));
    }

    [Fact] public async Task ActualSqliteAbortIsReportedWithoutEchoAndCanBeRetried()
    {
        using var test = new TestWorkspace(); await new SqliteJobStore(test.Workspace).LoadAsync();
        await using var connection = new SqliteConnection(SqliteSchema.ConnectionString(Path.Combine(test.Workspace.Root, "queue.db"))); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = "CREATE TRIGGER abort_preview BEFORE INSERT ON audition_ownership BEGIN SELECT RAISE(ABORT,'Private preview echo'); END"; await command.ExecuteNonQueryAsync();
        var store = new AuditionOwnershipStore(test.Workspace); var journal = new AuditionWriteJournal(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, []);
        var error = await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(journal)); Assert.Contains("checkpoint", error.Message); Assert.DoesNotContain("Private preview echo", error.Message); Assert.Empty(await store.LoadAsync());
        command.CommandText = "DROP TRIGGER abort_preview"; await command.ExecuteNonQueryAsync();
        await store.SaveAsync(journal); Assert.Equal(journal.Id, Assert.Single(await store.LoadAsync()).Id);
        await store.RemoveAsync(Assert.Single(await store.LoadAsync())); Assert.Empty(await store.LoadAsync());
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CorruptPreviewMetadataRefusesRecoveryAndPreservesFiles(bool nullIdentity)
    {
        using var test = new TestWorkspace(); await new SqliteJobStore(test.Workspace).LoadAsync(); using var gate = new SemaphoreSlim(1);
        var id = Guid.NewGuid().ToString("N"); var directory = Path.Combine(test.Workspace.Root, "auditions"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "audition-" + id + ".wav"); await File.WriteAllTextAsync(path, "preserve unknown");
        await using (var connection = new SqliteConnection(SqliteSchema.ConnectionString(Path.Combine(test.Workspace.Root, "queue.db"))))
        {
            await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = "INSERT INTO audition_ownership VALUES($id,$created,$payload)";
            command.Parameters.AddWithValue("$id", nullIdentity ? DBNull.Value : id); command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$payload", "Private preview echo, not JSON"); await command.ExecuteNonQueryAsync();
        }
        var generator = new AuditionGenerator(test.Workspace, new NeverProvider(), gate);
        var error = await Assert.ThrowsAsync<IOException>(() => generator.RecoverAsync()); Assert.DoesNotContain("Private preview echo", error.Message);
        Assert.Equal("preserve unknown", await File.ReadAllTextAsync(path)); Assert.Equal(1, gate.CurrentCount);
    }

    private sealed class NeverProvider : ISpeechProvider
    {
        public Task<ProviderInfo> ReadyAsync(string engine, CancellationToken ct) => throw new InvalidOperationException();
        public Task SynthesizeAsync(NarrationSettings settings, string text, string output, CancellationToken ct) => throw new InvalidOperationException();
    }
}
