using Microsoft.Data.Sqlite;
using CommuteCast.Core;
using System.Text.Json;

namespace CommuteCast.Infrastructure;

public interface ISchemaMigrationObserver
{
    Task BeforeCommitAsync(int fromVersion, int toVersion, CancellationToken ct);
}
public static class SqliteSchema
{
    // Version 6 fences local voice recipes and speaker pronunciation overrides from older runtimes.
    public const int CurrentVersion = 6;
    public const int ApplicationId = 0x434D4354;
    public const string AppVersion = "0.1.0";
    public static string ConnectionString(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate) =>
        new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 5 }.ToString();

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }
    private static async Task<HashSet<string>> TablesAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        await using var reader = await command.ExecuteReaderAsync(ct); var tables = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct)) tables.Add(reader.GetString(0)); return tables;
    }
    private static async Task ValidateColumnsAsync(SqliteConnection connection, string table, string[] expected, CancellationToken ct)
    {
        // Table comes exclusively from fixed application constants.
        await using var command = connection.CreateCommand(); command.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await command.ExecuteReaderAsync(ct); var columns = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            var column = reader.GetString(1); columns.Add(column);
            var primary = column is "id" or "sequence" or "version";
            var integer = column is "sequence" or "version";
            if (!reader.GetString(2).Equals(integer ? "INTEGER" : "TEXT", StringComparison.OrdinalIgnoreCase) || reader.GetInt32(3) != (primary ? 0 : 1) || reader.GetInt32(5) != (primary ? 1 : 0))
                throw new IOException("The queue column constraints are incompatible. Original files were preserved.");
        }
        if (!columns.SequenceEqual(expected)) throw new IOException("The queue schema is incompatible. Preserve local files and use a compatible application/backup.");
    }
    internal static async Task<int> ValidateSchemaAsync(SqliteConnection connection, bool allowEmpty, CancellationToken ct)
    {
        var applicationId = await ScalarAsync(connection, "PRAGMA application_id", ct);
        var version = await ScalarAsync(connection, "PRAGMA user_version", ct);
        if (version > CurrentVersion) throw new IOException($"Queue schema {version} needs a newer CommuteCast version. No migration or reset was performed.");
        if (version < 0 || applicationId != 0 && applicationId != ApplicationId || version != 0 && applicationId != ApplicationId || version == 0 && applicationId != 0)
            throw new IOException("The database identity is incompatible. Local files were preserved; choose a verified CommuteCast backup.");
        var tables = await TablesAsync(connection, ct);
        if (tables.Count == 0 && version == 0 && applicationId == 0 && allowEmpty) return 0;
        var expected = version == 0 ? new[] { "jobs", "events" } : version < 4 ? new[] { "jobs", "events", "schema_history" } : version == 4 ? new[] { "jobs", "events", "schema_history", "audition_ownership" } : new[] { "jobs", "events", "schema_history", "audition_ownership", "hosted_auditions" };
        if (!tables.SetEquals(expected)) throw new IOException("The database is not a recognized CommuteCast queue. No reset was performed.");
        await ValidateColumnsAsync(connection, "jobs", ["id", "created", "payload"], ct);
        await ValidateColumnsAsync(connection, "events", ["sequence", "job_id", "stage", "timestamp"], ct);
        if (version >= 4) await ValidateColumnsAsync(connection, "audition_ownership", ["id", "created", "payload"], ct);
        if (version >= 5) await ValidateColumnsAsync(connection, "hosted_auditions", ["id", "created", "payload"], ct);
        if (version != 0)
        {
            await ValidateColumnsAsync(connection, "schema_history", ["version", "app_version", "applied_utc"], ct);
            if (await ScalarAsync(connection, "SELECT count(*) FROM schema_history", ct) != version ||
                await ScalarAsync(connection, "SELECT coalesce(min(version),0) FROM schema_history", ct) != 1 ||
                await ScalarAsync(connection, "SELECT coalesce(max(version),0) FROM schema_history", ct) != version)
                throw new IOException("The schema migration history is incomplete. Original files were preserved.");
        }
        return (int)version;
    }
    internal static async Task CheckIntegrityAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var check = connection.CreateCommand(); check.CommandText = "PRAGMA integrity_check";
        await using var reader = await check.ExecuteReaderAsync(ct); var rows = 0;
        while (await reader.ReadAsync(ct)) { rows++; if (reader.GetString(0) != "ok") throw new IOException("The queue failed SQLite integrity validation. Original files were preserved."); }
        if (rows != 1) throw new IOException("The queue integrity result is incomplete. Original files were preserved.");
    }
    public static async Task<int> ValidateDatabaseAsync(string path, CancellationToken ct = default)
    {
        RejectLink(path);
        try
        {
            await using var connection = new SqliteConnection(ConnectionString(path, SqliteOpenMode.ReadOnly)); await connection.OpenAsync(ct);
            return await ValidateDatabaseConnectionAsync(connection, ct);
        }
        catch (Exception error) when (error is SqliteException or JsonException or NotSupportedException)
        { throw new IOException("The queue is corrupt or unreadable. Preserve local files and restore a verified compatible backup; no reset was performed.", error); }
    }
    internal static async Task<int> ValidateDatabaseConnectionAsync(SqliteConnection connection, CancellationToken ct)
    {
        var version = await ValidateSchemaAsync(connection, false, ct);
        await CheckIntegrityAsync(connection, ct);
        await using (var records = connection.CreateCommand())
        {
            records.CommandText = "SELECT id,created,payload FROM jobs";
            await using var items = await records.ExecuteReaderAsync(ct);
            while (await items.ReadAsync(ct)) ValidateRecord(items.GetString(0), items.GetString(1), items.GetString(2));
        }
        if (version >= 4) await AuditionOwnershipStore.ValidateRowsAsync(connection, ct);
        if (version >= 5) await HostedAuditionHistory.ValidateRowsAsync(connection, ct);
        return version;
    }
    internal static Job ValidateRecord(string id, string created, string payload)
    {
        try
        {
            var job = JsonSerializer.Deserialize<Job>(payload);
            if (job is null || job.Id != id || id.Length != 32 || !id.All(Uri.IsHexDigit) || !DateTimeOffset.TryParse(created, out var timestamp) || timestamp != job.CreatedUtc ||
                !Enum.IsDefined(job.Stage) || !Enum.IsDefined(job.FailureCategory) || job.Settings is null || job.Prepared is null || job.Prepared.Spans is null ||
                job.Source is null || job.Prepared.Script is null || job.Chunks is null || job.Receipts is null || job.PrivateArtifacts is null || job.Title is null || job.Destination is null)
                throw new IOException("A queue record is incompatible. Original records were preserved; restore a verified backup.");
            if (job.Episode is not null) { job.Episode.ValidateStorage(); if (job.Chunks.Count > 0) PodcastScript.ValidateManifest(job); }
            if (job.SpeechAttempts is { } attempts) foreach (var attempt in attempts) HostedAuditionHistory.Validate(attempt);
            return job;
        }
        catch (JsonException error) { throw new IOException("A queue record is unreadable. Original records were preserved; restore a verified backup.", error); }
        catch (ArgumentException error) { throw new IOException("A saved podcast contract is incompatible. Original records were preserved.", error); }
    }
    internal static void RejectLink(string path)
    {
        Workspace.RejectReparsePoints(Path.GetDirectoryName(Path.GetFullPath(path))!);
        Workspace.RejectFileReparsePoint(path);
    }

    internal static async Task EnsureAsync(SqliteConnection connection, Workspace workspace, ISchemaMigrationObserver? observer, CancellationToken ct)
    {
        var version = await ValidateSchemaAsync(connection, true, ct);
        if (version == CurrentVersion) return;
        var tables = await TablesAsync(connection, ct);
        if (tables.Count > 0)
        {
            var backups = Path.Combine(workspace.Root, "schema-backups"); Workspace.RejectReparsePoints(backups); Directory.CreateDirectory(backups);
            var path = Path.Combine(backups, $"pre-schema-{CurrentVersion}-{Guid.NewGuid():N}.db");
            await SnapshotAsync(connection, path, ct);
            await Workspace.AtomicWriteAsync(path + ".json", JsonSerializer.Serialize(new { fromVersion = version, toVersion = CurrentVersion, sha256 = await Workspace.HashFileAsync(path, ct), createdUtc = DateTimeOffset.UtcNow }));
        }
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY, created TEXT NOT NULL, payload TEXT NOT NULL); CREATE TABLE IF NOT EXISTS events(sequence INTEGER PRIMARY KEY AUTOINCREMENT, job_id TEXT NOT NULL, stage TEXT NOT NULL, timestamp TEXT NOT NULL); CREATE TABLE IF NOT EXISTS schema_history(version INTEGER PRIMARY KEY,app_version TEXT NOT NULL,applied_utc TEXT NOT NULL); CREATE INDEX IF NOT EXISTS events_by_job ON events(job_id,sequence);";
        await command.ExecuteNonQueryAsync(ct);
        command.CommandText = "CREATE TABLE IF NOT EXISTS audition_ownership(id TEXT PRIMARY KEY,created TEXT NOT NULL,payload TEXT NOT NULL)";
        await command.ExecuteNonQueryAsync(ct);
        command.CommandText = "CREATE TABLE IF NOT EXISTS hosted_auditions(id TEXT PRIMARY KEY,created TEXT NOT NULL,payload TEXT NOT NULL)";
        await command.ExecuteNonQueryAsync(ct);
        for (var next = version + 1; next <= CurrentVersion; next++)
        {
            command.CommandText = "INSERT INTO schema_history VALUES($version,$app,$now)";
            command.Parameters.Clear(); command.Parameters.AddWithValue("$version", next);
            command.Parameters.AddWithValue("$app", AppVersion); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(ct);
        }
        command.Parameters.Clear(); command.CommandText = $"PRAGMA application_id={ApplicationId}; PRAGMA user_version={CurrentVersion};";
        await command.ExecuteNonQueryAsync(ct);
        if (observer is not null) await observer.BeforeCommitAsync(version, CurrentVersion, ct);
        ct.ThrowIfCancellationRequested(); transaction.Commit();
    }
    internal static async Task SnapshotAsync(SqliteConnection source, string destination, CancellationToken ct)
    {
        RejectLink(destination);
        var partial = destination + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            // CreateNew prevents even a staging-name collision from overwriting a file.
            using (var reserved = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None)) reserved.Flush(true);
            await using (var target = new SqliteConnection(ConnectionString(partial, SqliteOpenMode.ReadWrite)))
            {
                await target.OpenAsync(ct); await using var settings = target.CreateCommand(); settings.CommandText = "PRAGMA synchronous=FULL"; await settings.ExecuteNonQueryAsync(ct);
                ct.ThrowIfCancellationRequested(); source.BackupDatabase(target); ct.ThrowIfCancellationRequested();
                settings.CommandText = "PRAGMA journal_mode=DELETE"; await settings.ExecuteNonQueryAsync(ct);
            }
            using (var flushed = new FileStream(partial, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) flushed.Flush(true);
            await ValidateDatabaseAsync(partial, ct); ct.ThrowIfCancellationRequested(); File.Move(partial, destination, false);
        }
        finally
        {
            foreach (var path in new[] { partial, partial + "-wal", partial + "-shm", partial + "-journal" }) if (File.Exists(path)) File.Delete(path);
        }
    }
}
