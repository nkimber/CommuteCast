using CommuteCast.Core;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CommuteCast.Infrastructure;

// Only file ownership is persisted. Prepared text, source, voices and jobs are absent.
public sealed class AuditionOwnershipStore(Workspace workspace)
{
    private readonly SqliteJobStore database = new(workspace);
    private static IOException StorageError(SqliteException error) => new("The preview ownership checkpoint or read failed. Check free disk space and permissions, then retry. Durable records were preserved.", error);
    public const int MaximumRecords = 256;
    internal static async Task RequireSettledAsync(Workspace workspace, CancellationToken ct)
    {
        var path = Path.Combine(workspace.Root, "queue.db");
        if (!File.Exists(path)) return;
        SqliteSchema.RejectLink(path);
        try
        {
            await using var connection = new SqliteConnection(SqliteSchema.ConnectionString(path, SqliteOpenMode.ReadOnly));
            await connection.OpenAsync(ct);
            var version = await SqliteSchema.ValidateSchemaAsync(connection, false, ct);
            if (version >= 4 && (await ValidateRowsAsync(connection, ct)).Count != 0)
                throw new IOException("Voice previews must be stopped or reconciled in the original workspace before removing private data. Preview files and ownership records were preserved.");
        }
        catch (SqliteException error) { throw StorageError(error); }
    }
    public static void Validate(AuditionWriteJournal journal)
    {
        if (journal is null || !Regex.IsMatch(journal.Id ?? "", "^[a-f0-9]{32}$", RegexOptions.CultureInvariant) || journal.CreatedUtc <= DateTimeOffset.MinValue ||
            journal.Artifacts is null || journal.Artifacts.Count > 4) throw new IOException("The preview ownership record is invalid. Files were preserved.");
        PrivateJobFiles.Inventory(new Job { PrivateArtifacts = journal.Artifacts });
        var output = "audition-" + journal.Id + ".wav";
        foreach (var receipt in journal.Artifacts)
            if (receipt.CreationIdentity is null && receipt.PromotionIdentity is null ||
                receipt.RelativePath != output && !Regex.IsMatch(receipt.RelativePath, "^" + Regex.Escape(output) + @"\.attempt-[a-f0-9]{32}\.partial$", RegexOptions.CultureInvariant))
                throw new IOException("The preview receipt has no original identity or an incompatible path. Files were preserved.");
    }
    private static AuditionWriteJournal Read(string id, string created, string payload)
    {
        try
        {
            if (payload.Length > 32768) throw new IOException("The preview ownership record exceeds its bound.");
            var journal = JsonSerializer.Deserialize<AuditionWriteJournal>(payload) ?? throw new JsonException(); Validate(journal);
            if (journal.Id != id || !DateTimeOffset.TryParse(created, out var timestamp) || timestamp != journal.CreatedUtc) throw new IOException("The preview ownership identity changed. Files were preserved.");
            return journal;
        }
        catch (JsonException error) { throw new IOException("The preview ownership record is unreadable. Files were preserved.", error); }
    }
    internal static async Task<IReadOnlyList<AuditionWriteJournal>> ValidateRowsAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT id,created,payload FROM audition_ownership ORDER BY created,id";
        await using var reader = await command.ExecuteReaderAsync(ct); var result = new List<AuditionWriteJournal>();
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2)) throw new IOException("A preview ownership record is incomplete. Files were preserved.");
            if (result.Count >= MaximumRecords) throw new IOException("Too many preview ownership records require inspection. Files were preserved.");
            result.Add(Read(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        return result;
    }
    public async Task<IReadOnlyList<AuditionWriteJournal>> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var path = Path.Combine(workspace.Root, "queue.db");
            if (!File.Exists(path) && !Directory.Exists(path)) return [];
            await using var connection = await database.OpenAsync(ct); return await ValidateRowsAsync(connection, ct);
        }
        catch (SqliteException error) { throw StorageError(error); }
    }
    public async Task SaveAsync(AuditionWriteJournal journal, CancellationToken ct = default)
    {
        Validate(journal); var payload = JsonSerializer.Serialize(journal);
        try
        {
            await using var connection = await database.OpenAsync(ct); using var transaction = connection.BeginTransaction();
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT count(*) FROM audition_ownership WHERE id<>$id"; command.Parameters.AddWithValue("$id", journal.Id);
            if (Convert.ToInt64(await command.ExecuteScalarAsync(ct)) >= MaximumRecords) throw new IOException("Preview ownership storage is full. Clean interrupted previews before auditioning again.");
            command.CommandText = "INSERT INTO audition_ownership VALUES($id,$created,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload WHERE audition_ownership.created=excluded.created";
            command.Parameters.AddWithValue("$created", journal.CreatedUtc.ToString("O")); command.Parameters.AddWithValue("$payload", payload);
            if (await command.ExecuteNonQueryAsync(ct) != 1) throw new IOException("The preview ownership record changed. Files were preserved.");
            ct.ThrowIfCancellationRequested(); transaction.Commit();
        }
        catch (SqliteException error) { throw StorageError(error); }
    }
    public async Task RemoveAsync(AuditionWriteJournal journal, CancellationToken ct = default)
    {
        Validate(journal); var payload = JsonSerializer.Serialize(journal);
        try
        {
            await using var connection = await database.OpenAsync(ct); await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM audition_ownership WHERE id=$id AND payload=$payload";
            command.Parameters.AddWithValue("$id", journal.Id); command.Parameters.AddWithValue("$payload", payload);
            if (await command.ExecuteNonQueryAsync(ct) != 1) throw new IOException("The preview ownership record changed during cleanup. It was preserved for inspection.");
        }
        catch (SqliteException error) { throw StorageError(error); }
    }
}
