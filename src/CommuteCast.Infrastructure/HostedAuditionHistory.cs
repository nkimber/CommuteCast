using System.Text.Json;
using CommuteCast.Core;
using Microsoft.Data.Sqlite;

namespace CommuteCast.Infrastructure;

/// <summary>Source-free hosted audition requests retained after preview-file cleanup.</summary>
public sealed class HostedAuditionHistory(Workspace workspace)
{
    public static void Validate(SpeechAttempt attempt)
    {
        if (attempt.Id.Length != 32 || !attempt.Id.All(Uri.IsHexDigit) || !SpeechProviders.IsKnown(attempt.Provider) || !SpeechProviders.IsHosted(attempt.Provider) ||
            !SpeechProviders.Get(attempt.Provider).Capabilities.Models.Contains(attempt.Model) || attempt.TextHash.Length != 64 || !attempt.TextHash.All(Uri.IsHexDigit) ||
            attempt.State is not ("started" or "received" or "uncertain" or "rejected") || attempt.RequestId?.Length > 200 || attempt.Usage?.Length > 4096 || attempt.BilledCharacters < 0)
            throw new IOException("A hosted request record is incompatible. Original records were preserved.");
    }
    public async Task SaveAsync(IReadOnlyList<SpeechAttempt> attempts)
    {
        await using var connection = await new SqliteJobStore(workspace).OpenAsync(default);
        using var transaction = connection.BeginTransaction();
        foreach (var attempt in attempts)
        {
            Validate(attempt); await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO hosted_auditions VALUES($id,$created,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload";
            command.Parameters.AddWithValue("$id", attempt.Id); command.Parameters.AddWithValue("$created", attempt.StartedUtc.ToString("O")); command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(attempt)); await command.ExecuteNonQueryAsync();
        }
        transaction.Commit();
    }
    internal static async Task ValidateRowsAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT id,created,payload FROM hosted_auditions";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            try
            {
                var attempt = JsonSerializer.Deserialize<SpeechAttempt>(reader.GetString(2)) ?? throw new JsonException(); Validate(attempt);
                if (attempt.Id != reader.GetString(0) || attempt.StartedUtc.ToString("O") != reader.GetString(1)) throw new IOException("Hosted audition identity differs from its record.");
            }
            catch (JsonException error) { throw new IOException("Hosted audition history is unreadable. Original files were preserved.", error); }
        }
    }
    public async Task<IReadOnlyList<SpeechAttempt>> RecentAsync(CancellationToken ct = default)
    {
        await using var connection = await new SqliteJobStore(workspace).OpenAsync(ct); await ValidateRowsAsync(connection, ct);
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT payload FROM hosted_auditions ORDER BY created DESC LIMIT 100";
        await using var reader = await command.ExecuteReaderAsync(ct); var result = new List<SpeechAttempt>();
        while (await reader.ReadAsync(ct)) { var attempt = JsonSerializer.Deserialize<SpeechAttempt>(reader.GetString(0))!; result.Add(attempt.State == "started" ? attempt with { State = "uncertain" } : attempt); }
        return result;
    }
}
