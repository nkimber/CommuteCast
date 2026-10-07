using CommuteCast.Core;
using CommuteCast.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace CommuteCast.Tests;

public class SchemaBackupTests
{
    [Fact] public async Task VersionThreeMigrationAddsEmptyPreviewOwnershipAndPreservesExactPayloadAndSnapshot()
    {
        using var test = new TestWorkspace(); var job = MakeJob();
        job.PrivateArtifacts.Add(new("assembled.wav", "", CreationIdentity: new(1, 1, new string('A', 32), 1)));
        var store = new SqliteJobStore(test.Workspace); await store.SaveAsync(job);
        await ExecuteAsync(Database(test), "DROP TABLE audition_ownership; DELETE FROM schema_history WHERE version>3; PRAGMA user_version=3");
        var original = await PayloadAsync(Database(test)); Assert.Equal(3, await SqliteSchema.ValidateDatabaseAsync(Database(test)));
        await store.LoadAsync(); Assert.Equal(original, await PayloadAsync(Database(test)));
        Assert.Equal(SqliteSchema.CurrentVersion, await SqliteSchema.ValidateDatabaseAsync(Database(test)));
        Assert.Equal(0, await ScalarAsync(Database(test), "SELECT count(*) FROM audition_ownership"));
        var snapshot = Assert.Single(Directory.GetFiles(Path.Combine(test.Workspace.Root, "schema-backups"), "*.db"));
        Assert.Equal(3, await SqliteSchema.ValidateDatabaseAsync(snapshot)); Assert.Equal(original, await PayloadAsync(snapshot));
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(snapshot + ".json"));
        Assert.Equal(await Workspace.HashFileAsync(snapshot), receipt.RootElement.GetProperty("sha256").GetString());
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task VersionThreeMigrationFailureOrCancellationPreservesOriginalTablesAndPayload(bool cancel)
    {
        using var test = new TestWorkspace(); await new SqliteJobStore(test.Workspace).SaveAsync(MakeJob());
        await ExecuteAsync(Database(test), "DROP TABLE audition_ownership; DELETE FROM schema_history WHERE version>3; PRAGMA user_version=3");
        var payload = await PayloadAsync(Database(test)); using var cancellation = new CancellationTokenSource();
        var store = new SqliteJobStore(test.Workspace, cancel ? new CancelMigration(cancellation) : new FailingMigration());
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(cancellation.Token));
        else await Assert.ThrowsAsync<IOException>(() => store.LoadAsync());
        Assert.Equal(3, await SqliteSchema.ValidateDatabaseAsync(Database(test))); Assert.Equal(payload, await PayloadAsync(Database(test)));
        Assert.Equal(0, await ScalarAsync(Database(test), "SELECT count(*) FROM sqlite_schema WHERE name='audition_ownership'"));
        Assert.Equal(3, await ScalarAsync(Database(test), "SELECT count(*) FROM schema_history"));
    }

    [Fact] public async Task VersionTwoMigrationPreservesPayloadAndVerifiedOriginalSnapshot()
    {
        using var test = new TestWorkspace(); var job = MakeJob(); var store = new SqliteJobStore(test.Workspace);
        await store.SaveAsync(job);
        await ExecuteAsync(Database(test), "DROP TABLE audition_ownership; DELETE FROM schema_history WHERE version>2; PRAGMA user_version=2");
        var original = await PayloadAsync(Database(test));
        Assert.Equal(2, await SqliteSchema.ValidateDatabaseAsync(Database(test)));
        Assert.Equal(JsonSerializer.Serialize(job), JsonSerializer.Serialize(Assert.Single(await store.LoadAsync())));
        Assert.Equal(original, await PayloadAsync(Database(test)));
        Assert.Equal(SqliteSchema.CurrentVersion, await SqliteSchema.ValidateDatabaseAsync(Database(test)));
        Assert.Equal(SqliteSchema.CurrentVersion, await ScalarAsync(Database(test), "SELECT count(*) FROM schema_history"));
        var snapshot = Assert.Single(Directory.GetFiles(Path.Combine(test.Workspace.Root, "schema-backups"), "*.db"));
        Assert.Equal(2, await SqliteSchema.ValidateDatabaseAsync(snapshot)); Assert.Equal(original, await PayloadAsync(snapshot));
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(snapshot + ".json"));
        Assert.Equal(2, receipt.RootElement.GetProperty("fromVersion").GetInt32());
        Assert.Equal(SqliteSchema.CurrentVersion, receipt.RootElement.GetProperty("toVersion").GetInt32());
        Assert.Equal(await Workspace.HashFileAsync(snapshot), receipt.RootElement.GetProperty("sha256").GetString());
    }
    [Fact] public async Task VersionOneMigrationPreservesExactPayloadAudioIdentityAndVerifiedOriginalSnapshot()
    {
        using var test = new TestWorkspace(); var job = MakeJob(); var directory = test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        var audio = Path.Combine(directory, "complete.mp3"); await File.WriteAllTextAsync(audio, "Completed synthetic audio");
        await using (var held = ExportStagingFile.OpenIfPresent(directory, "complete.mp3")!)
            job.PrivateArtifacts.Add(new("complete.mp3", await held.HashAsync(default), held.Identity));
        var store = new SqliteJobStore(test.Workspace); await store.SaveAsync(job);
        await ExecuteAsync(Database(test), "DROP TABLE audition_ownership; DELETE FROM schema_history WHERE version>1; PRAGMA user_version=1");
        var originalPayload = await PayloadAsync(Database(test)); var audioHash = await Workspace.HashFileAsync(audio);
        var restored = Assert.Single(await store.LoadAsync());
        Assert.Equal(originalPayload, await PayloadAsync(Database(test)));
        Assert.Equal(JsonSerializer.Serialize(job), JsonSerializer.Serialize(restored));
        Assert.Equal(audioHash, await Workspace.HashFileAsync(audio));
        Assert.Equal(SqliteSchema.CurrentVersion, await SqliteSchema.ValidateDatabaseAsync(Database(test)));
        Assert.Equal(SqliteSchema.CurrentVersion, await ScalarAsync(Database(test), "SELECT count(*) FROM schema_history"));
        var snapshot = Assert.Single(Directory.GetFiles(Path.Combine(test.Workspace.Root, "schema-backups"), "*.db"));
        Assert.Equal(1, await SqliteSchema.ValidateDatabaseAsync(snapshot)); Assert.Equal(originalPayload, await PayloadAsync(snapshot));
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(snapshot + ".json"));
        Assert.Equal(1, receipt.RootElement.GetProperty("fromVersion").GetInt32()); Assert.Equal(SqliteSchema.CurrentVersion, receipt.RootElement.GetProperty("toVersion").GetInt32());
        Assert.Equal(await Workspace.HashFileAsync(snapshot), receipt.RootElement.GetProperty("sha256").GetString());
        await store.LoadAsync(); Assert.Single(Directory.GetFiles(Path.Combine(test.Workspace.Root, "schema-backups"), "*.db"));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task VersionOneMigrationFailureOrCancellationLeavesVersionHistoryAndPayloadIntact(bool cancel)
    {
        using var test = new TestWorkspace(); await new SqliteJobStore(test.Workspace).SaveAsync(MakeJob());
        await ExecuteAsync(Database(test), "DROP TABLE audition_ownership; DELETE FROM schema_history WHERE version>1; PRAGMA user_version=1");
        var payload = await PayloadAsync(Database(test)); using var cancellation = new CancellationTokenSource();
        var store = new SqliteJobStore(test.Workspace, cancel ? new CancelMigration(cancellation) : new FailingMigration());
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(cancellation.Token));
        else await Assert.ThrowsAsync<IOException>(() => store.LoadAsync());
        Assert.Equal(1, await SqliteSchema.ValidateDatabaseAsync(Database(test))); Assert.Equal(1, await ScalarAsync(Database(test), "SELECT count(*) FROM schema_history"));
        Assert.Equal(payload, await PayloadAsync(Database(test)));
        Assert.Equal(1, await SqliteSchema.ValidateDatabaseAsync(Assert.Single(Directory.GetFiles(Path.Combine(test.Workspace.Root, "schema-backups"), "*.db"))));
    }
    [Fact] public async Task ReadOnlyVersionOneValidationDoesNotUpgradeOrCreateSnapshot()
    {
        using var test = new TestWorkspace(); await new SqliteJobStore(test.Workspace).SaveAsync(MakeJob());
        await ExecuteAsync(Database(test), "DROP TABLE audition_ownership; DELETE FROM schema_history WHERE version>1; PRAGMA user_version=1");
        var hash = await Workspace.HashFileAsync(Database(test));
        Assert.Equal(1, await SqliteSchema.ValidateDatabaseAsync(Database(test))); Assert.Equal(hash, await Workspace.HashFileAsync(Database(test)));
        Assert.False(Directory.Exists(Path.Combine(test.Workspace.Root, "schema-backups")));
    }
    [Fact] public async Task MissingMigrationHistoryBlocksReadsAndPreservesDatabase()
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace); await store.SaveAsync(MakeJob());
        await ExecuteAsync(Database(test), "DELETE FROM schema_history"); var before = await Workspace.HashFileAsync(Database(test));
        await Assert.ThrowsAsync<IOException>(() => store.LoadAsync()); Assert.Equal(before, await Workspace.HashFileAsync(Database(test)));
    }
    [Fact] public async Task ActualSqliteTransactionFailureIsActionableAndDoesNotEchoPrivateDetails()
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace); var original = MakeJob(); await store.SaveAsync(original);
        await ExecuteAsync(Database(test), "CREATE TRIGGER injected_failure BEFORE INSERT ON jobs BEGIN SELECT RAISE(ABORT,'Sensitive SQL failure detail'); END");
        var error = await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(MakeJob())); Assert.DoesNotContain("Sensitive SQL failure detail", error.Message);
        Assert.Contains("disk space", error.Message); Assert.Equal(original.Id, Assert.Single(await store.LoadAsync()).Id); Assert.Single(await store.ReadDiagnosticEventsAsync());
    }
    [Fact] public async Task NewQueueRecordsIdentityVersionAndHistoryWithoutAMigrationBackup()
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace); await store.SaveAsync(MakeJob());
        Assert.Equal(SqliteSchema.CurrentVersion, await SqliteSchema.ValidateDatabaseAsync(Database(test)));
        Assert.False(Directory.Exists(Path.Combine(test.Workspace.Root, "schema-backups")));
        Assert.Equal(SqliteSchema.ApplicationId, await ScalarAsync(Database(test), "PRAGMA application_id"));
        Assert.Equal(SqliteSchema.CurrentVersion, await ScalarAsync(Database(test), "SELECT count(*) FROM schema_history"));
    }
    [Fact] public async Task LegacyMigrationMakesVerifiedConsistentSnapshotAndPreservesFrozenRecords()
    {
        using var test = new TestWorkspace(); var job = MakeJob(); await LegacyAsync(test, job);
        var restored = Assert.Single(await new SqliteJobStore(test.Workspace).LoadAsync());
        Assert.Equal(job.Source, restored.Source); Assert.Equal(job.Settings, restored.Settings); Assert.Equal(job.CreatedUtc, restored.CreatedUtc);
        var backup = Assert.Single(Directory.GetFiles(Path.Combine(test.Workspace.Root, "schema-backups"), "*.db"));
        Assert.Equal(0, await SqliteSchema.ValidateDatabaseAsync(backup)); Assert.Equal(1, await ScalarAsync(backup, "SELECT count(*) FROM jobs"));
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(backup + ".json")); Assert.Equal(await Workspace.HashFileAsync(backup), receipt.RootElement.GetProperty("sha256").GetString());
        Assert.Equal(SqliteSchema.CurrentVersion, await SqliteSchema.ValidateDatabaseAsync(Database(test)));
        await new SqliteJobStore(test.Workspace).LoadAsync(); Assert.Single(Directory.GetFiles(Path.Combine(test.Workspace.Root, "schema-backups"), "*.db"));
    }
    [Fact] public async Task MigrationFailureRollsBackEntireSchemaAndRetainsOriginalAndBackup()
    {
        using var test = new TestWorkspace(); var job = MakeJob(); await LegacyAsync(test, job);
        await Assert.ThrowsAsync<IOException>(() => new SqliteJobStore(test.Workspace, new FailingMigration()).LoadAsync());
        Assert.Equal(0, await ScalarAsync(Database(test), "PRAGMA user_version")); Assert.Equal(0, await ScalarAsync(Database(test), "PRAGMA application_id"));
        Assert.Equal(0, await ScalarAsync(Database(test), "SELECT count(*) FROM sqlite_schema WHERE name='schema_history'"));
        Assert.Equal(1, await ScalarAsync(Database(test), "SELECT count(*) FROM jobs"));
        var backup = Assert.Single(Directory.GetFiles(Path.Combine(test.Workspace.Root, "schema-backups"), "*.db")); Assert.Equal(0, await SqliteSchema.ValidateDatabaseAsync(backup));
        Assert.Equal(job.Source, Assert.Single(await new SqliteJobStore(test.Workspace).LoadAsync()).Source);
    }
    [Fact] public async Task MigrationCancellationRollsBackVersionAndHistory()
    {
        using var test = new TestWorkspace(); await LegacyAsync(test, MakeJob()); using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SqliteJobStore(test.Workspace, new CancelMigration(cancel)).LoadAsync(cancel.Token));
        Assert.Equal(0, await ScalarAsync(Database(test), "PRAGMA user_version")); Assert.Equal(0, await ScalarAsync(Database(test), "SELECT count(*) FROM sqlite_schema WHERE name='schema_history'"));
    }
    [Theory] [InlineData("future")] [InlineData("foreign-id")] [InlineData("foreign-table")] [InlineData("column")] [InlineData("constraint")]
    public async Task IncompatibleQueueIsRejectedWithoutModifyingOriginalBytes(string defect)
    {
        using var test = new TestWorkspace(); await LegacyAsync(test, MakeJob());
        await ExecuteAsync(Database(test), defect switch
        {
            "future" => $"PRAGMA application_id={SqliteSchema.ApplicationId}; PRAGMA user_version=99",
            "foreign-id" => "PRAGMA application_id=12345",
            "foreign-table" => "CREATE TABLE unrelated(value TEXT)",
            "column" => "ALTER TABLE jobs ADD COLUMN unexpected TEXT",
            _ => "ALTER TABLE jobs RENAME TO original; CREATE TABLE jobs(id TEXT,created TEXT NOT NULL,payload TEXT NOT NULL); INSERT INTO jobs SELECT * FROM original; DROP TABLE original"
        });
        var before = await Workspace.HashFileAsync(Database(test)); await Assert.ThrowsAsync<IOException>(() => new SqliteJobStore(test.Workspace).LoadAsync());
        Assert.Equal(before, await Workspace.HashFileAsync(Database(test))); Assert.False(Directory.Exists(Path.Combine(test.Workspace.Root, "schema-backups")));
    }
    [Theory] [InlineData("bytes")] [InlineData("json")] [InlineData("identity")] [InlineData("stage")] [InlineData("null-settings")]
    public async Task CorruptOrIncompatibleRecordsArePreservedAndBlockReading(string defect)
    {
        using var test = new TestWorkspace(); var job = MakeJob(); await new SqliteJobStore(test.Workspace).SaveAsync(job);
        if (defect == "bytes") await File.WriteAllBytesAsync(Database(test), [1, 2, 3, 4]);
        else await ExecuteAsync(Database(test), defect switch
        {
            "json" => "UPDATE jobs SET payload='malformed json'",
            "identity" => "UPDATE jobs SET payload=json_set(payload,'$.Id','foreign')",
            "stage" => "UPDATE jobs SET payload=json_set(payload,'$.Stage',999)",
            _ => "UPDATE jobs SET payload=json_set(payload,'$.Settings',NULL)"
        });
        var before = await Workspace.HashFileAsync(Database(test));
        await Assert.ThrowsAsync<IOException>(() => new SqliteJobStore(test.Workspace).LoadAsync());
        await Assert.ThrowsAsync<IOException>(() => SqliteSchema.ValidateDatabaseAsync(Database(test)));
        Assert.Equal(before, await Workspace.HashFileAsync(Database(test)));
    }
    [Fact] public async Task OnlineBackupIncludesCommittedWalThatNaiveDatabaseCopyMisses()
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace); await store.SaveAsync(MakeJob());
        await using var reader = new SqliteConnection(SqliteSchema.ConnectionString(Database(test))); await reader.OpenAsync();
        await using var hold = reader.CreateCommand(); hold.CommandText = "PRAGMA wal_autocheckpoint=0; BEGIN; SELECT count(*) FROM jobs"; await hold.ExecuteScalarAsync();
        await store.SaveAsync(MakeJob()); Assert.True(File.Exists(Database(test) + "-wal"));
        var naive = Path.Combine(test.Workspace.Root, "naive.db"); File.Copy(Database(test), naive);
        var consistent = Path.Combine(test.Workspace.Root, "consistent.db"); await store.BackupDatabaseAsync(consistent);
        Assert.Equal(1, await ScalarAsync(naive, "SELECT count(*) FROM jobs")); Assert.Equal(2, await ScalarAsync(consistent, "SELECT count(*) FROM jobs"));
        Assert.Equal(SqliteSchema.CurrentVersion, await SqliteSchema.ValidateDatabaseAsync(consistent)); Assert.False(File.Exists(consistent + "-wal")); Assert.False(File.Exists(consistent + "-shm"));
        hold.CommandText = "ROLLBACK"; await hold.ExecuteNonQueryAsync();
    }
    [Fact] public async Task BackupCollisionAndCancellationPreserveTheExistingSnapshot()
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace); await store.SaveAsync(MakeJob());
        var path = Path.Combine(test.Workspace.Root, "snapshot.db"); await store.BackupDatabaseAsync(path); var before = await Workspace.HashFileAsync(path);
        await Assert.ThrowsAsync<IOException>(() => store.BackupDatabaseAsync(path)); Assert.Equal(before, await Workspace.HashFileAsync(path));
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.BackupDatabaseAsync(path + "-cancelled", cancel.Token));
        Assert.False(File.Exists(path + "-cancelled")); Assert.Empty(Directory.GetFiles(test.Workspace.Root, "*.partial-*"));
    }
    [Fact] public async Task OnlineBackupCanBeOpenedAndExtendedThroughProductionStoreAfterRestore()
    {
        using var original = new TestWorkspace(); using var restored = new TestWorkspace(); var job = MakeJob();
        var store = new SqliteJobStore(original.Workspace); await store.SaveAsync(job); await store.BackupDatabaseAsync(Database(restored));
        var reopened = new SqliteJobStore(restored.Workspace); Assert.Equal(job.Source, Assert.Single(await reopened.LoadAsync()).Source);
        await reopened.SaveAsync(MakeJob()); Assert.Equal(2, (await reopened.LoadAsync()).Count); Assert.Equal(SqliteSchema.CurrentVersion, await SqliteSchema.ValidateDatabaseAsync(Database(restored)));
    }
    [Fact] public async Task WorkspaceLeaseIsExclusiveAndCanBeReleasedAcrossAwaitBoundaries()
    {
        using var test = new TestWorkspace(); var first = WorkspaceLease.Acquire(test.Workspace);
        Assert.Throws<IOException>(() => WorkspaceLease.Acquire(test.Workspace)); await Task.Yield(); first.Dispose();
        using var second = WorkspaceLease.Acquire(test.Workspace); second.EnsureHeld(); first.Dispose(); Assert.Throws<ObjectDisposedException>(() => first.EnsureHeld());
    }
    private static Job MakeJob() => new() { Source = "Frozen original source. 😀", Prepared = TextPreparation.Prepare("Frozen original source. 😀"), Settings = new("piper", "en_US-lessac-medium", 1.1, false, "API=A P I", "pinned-fixture") };
    private static string Database(TestWorkspace test) => Path.Combine(test.Workspace.Root, "queue.db");
    private static async Task<string> PayloadAsync(string path)
    {
        await using var connection = new SqliteConnection(SqliteSchema.ConnectionString(path, SqliteOpenMode.ReadOnly)); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT payload FROM jobs";
        return (string)(await command.ExecuteScalarAsync())!;
    }
    private static async Task LegacyAsync(TestWorkspace test, Job job)
    {
        await ExecuteAsync(Database(test), "CREATE TABLE jobs(id TEXT PRIMARY KEY,created TEXT NOT NULL,payload TEXT NOT NULL); CREATE TABLE events(sequence INTEGER PRIMARY KEY AUTOINCREMENT,job_id TEXT NOT NULL,stage TEXT NOT NULL,timestamp TEXT NOT NULL)");
        await using var connection = new SqliteConnection(SqliteSchema.ConnectionString(Database(test))); await connection.OpenAsync();
        await using var insert = connection.CreateCommand(); insert.CommandText = "INSERT INTO jobs VALUES($id,$created,$payload)";
        insert.Parameters.AddWithValue("$id", job.Id); insert.Parameters.AddWithValue("$created", job.CreatedUtc.ToString("O")); insert.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(job)); await insert.ExecuteNonQueryAsync();
    }
    private static async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection(SqliteSchema.ConnectionString(path)); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
    }
    private static async Task<long> ScalarAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection(SqliteSchema.ConnectionString(path, SqliteOpenMode.ReadOnly)); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
    private sealed class FailingMigration : ISchemaMigrationObserver
    {
        public Task BeforeCommitAsync(int fromVersion, int toVersion, CancellationToken ct) => throw new IOException("Injected migration failure");
    }
    private sealed class CancelMigration(CancellationTokenSource cancel) : ISchemaMigrationObserver
    {
        public Task BeforeCommitAsync(int fromVersion, int toVersion, CancellationToken ct) { cancel.Cancel(); return Task.CompletedTask; }
    }
}
