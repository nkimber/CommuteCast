using CommuteCast.Core;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Security.Cryptography;

namespace CommuteCast.Infrastructure;

public sealed class Workspace
{
    private readonly SemaphoreSlim settingsGate = new(1);
    // Bounded per-path serialization across Workspace instances, without retaining private paths.
    private static readonly SemaphoreSlim[] atomicWriteGates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();
    public string Root { get; }
    public Workspace(string? root = null)
    {
        Root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CommuteCast"));
        Directory.CreateDirectory(Root);
    }
    public string JobDirectory(string id)
    {
        if (id.Length != 32 || !id.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid job identity.");
        return Path.Combine(Root, "jobs", id);
    }
    public string FinalPath(Job job) => Path.Combine(JobDirectory(job.Id), "complete.mp3");
    public string ChunkPath(Job job, int index) => Path.Combine(JobDirectory(job.Id), $"chunk-{index:D5}.wav");
    public void GuardLocalDestination(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Choose an output folder before queueing. Use Choose folder to select your existing local OneDrive folder; your draft is retained.");
        var path = Path.GetFullPath(directory);
        if (!Directory.Exists(path)) throw new IOException("The output folder is missing. Choose an existing local OneDrive folder.");
        if (path.StartsWith("\\\\", StringComparison.Ordinal)) throw new IOException("Choose a local synced folder, not a network share.");
        if (IsWithin(Root, path) || IsWithin(path, Root)) throw new IOException("Output must be separate from CommuteCast's private data folder.");
        RejectReparsePoints(path);
    }
    public static bool IsWithin(string parent, string child)
    {
        parent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        child = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar);
        return child.Equals(parent, StringComparison.OrdinalIgnoreCase) || child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    public static void RejectReparsePoints(string path)
    {
        for (var info = new DirectoryInfo(Path.GetFullPath(path)); info is not null; info = info.Parent)
            if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Directory junctions and symbolic links are not supported for managed storage.");
    }
    public static void RejectFileReparsePoint(string path)
    {
        try { if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Symbolic links are not supported for managed files."); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
    public static async Task<string> HashFileAsync(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }
    public async Task<AppSettings> LoadSettingsAsync()
    {
        var path = Path.Combine(Root, "settings.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(path)) ?? new() : new();
    }
    public async Task SaveSettingsAsync(AppSettings settings)
    {
        var snapshot = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        await settingsGate.WaitAsync();
        try { await AtomicWriteAsync(Path.Combine(Root, "settings.json"), snapshot); }
        finally { settingsGate.Release(); }
    }
    public static async Task AtomicWriteAsync(string path, string text)
    {
        path = Path.GetFullPath(path); SqliteSchema.RejectLink(path);
        var gate = atomicWriteGates[(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(path) % (uint)atomicWriteGates.Length];
        await gate.WaitAsync();
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        var ownsTemporary = false;
        try
        {
            SqliteSchema.RejectLink(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                ownsTemporary = true;
                await file.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text));
                file.Flush(true);
            }
            SqliteSchema.RejectLink(path); File.Move(temporary, path, true);
        }
        finally
        {
            try { if (ownsTemporary && File.Exists(temporary)) File.Delete(temporary); }
            finally { gate.Release(); }
        }
    }
}

public sealed class SqliteJobStore : IJobStore
{
    private readonly Workspace workspace;
    private readonly ISchemaMigrationObserver? migrationObserver;
    private readonly string connectionString;
    private readonly SemaphoreSlim gate = new(1);
    private static IOException StorageError(SqliteException error) => new("The queue checkpoint or read failed. Check free disk space and permissions, then retry. Existing durable records were preserved.", error);
    public SqliteJobStore(Workspace workspace, ISchemaMigrationObserver? migrationObserver = null)
    {
        this.workspace = workspace; this.migrationObserver = migrationObserver;
        connectionString = SqliteSchema.ConnectionString(Path.Combine(workspace.Root, "queue.db"));
    }
    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            SqliteSchema.RejectLink(Path.Combine(workspace.Root, "queue.db"));
            await connection.OpenAsync(ct);
            await SqliteSchema.EnsureAsync(connection, workspace, migrationObserver, ct);
            await using var command = connection.CreateCommand(); command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL";
            await command.ExecuteNonQueryAsync(ct); return connection;
        }
        catch (SqliteException error)
        {
            await connection.DisposeAsync();
            throw new IOException("The queue could not be opened or migrated. Original files were preserved. Check storage and restore a verified compatible backup if needed.", error);
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    public async Task SaveAsync(Job job, CancellationToken ct = default)
    {
        // Serialize before awaiting: UI and worker never persist a partly mutated snapshot.
        var payload = JsonSerializer.Serialize(job);
        var stage = job.Stage.ToString();
        await gate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            using var transaction = connection.BeginTransaction();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO jobs VALUES($id,$created,$payload) ON CONFLICT(id) DO UPDATE SET payload=$payload; INSERT INTO events(job_id,stage,timestamp) VALUES($id,$stage,$now);";
            command.Parameters.AddWithValue("$id", job.Id);
            command.Parameters.AddWithValue("$created", job.CreatedUtc.ToString("O"));
            command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue("$stage", stage);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(ct);
            transaction.Commit();
        }
        catch (SqliteException error) { throw StorageError(error); }
        finally { gate.Release(); }
    }
    public async Task<IReadOnlyList<Job>> LoadAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await SqliteSchema.CheckIntegrityAsync(connection, ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,created,payload FROM jobs ORDER BY created";
            await using var reader = await command.ExecuteReaderAsync(ct);
            var jobs = new List<Job>();
            while (await reader.ReadAsync(ct)) jobs.Add(SqliteSchema.ValidateRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            return jobs;
        }
        catch (SqliteException error) { throw StorageError(error); }
        finally { gate.Release(); }
    }
    public async Task BackupDatabaseAsync(string destination, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await SqliteSchema.SnapshotAsync(connection, destination, ct);
        }
        catch (SqliteException error) { throw StorageError(error); }
        finally { gate.Release(); }
    }
    public async Task SaveQueueOrderAsync(IReadOnlyDictionary<string, long> positions, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            using var transaction = connection.BeginTransaction();
            foreach (var (id, position) in positions)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                // Update only the order field; do not overwrite concurrent stage or receipt saves.
                command.CommandText = "UPDATE jobs SET payload=json_set(payload,'$.QueuePosition',$position) WHERE id=$id AND json_extract(payload,'$.Stage')=$queued;";
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$position", position);
                command.Parameters.AddWithValue("$queued", (int)JobStage.Queued);
                if (await command.ExecuteNonQueryAsync(ct) != 1) throw new IOException("Queue order changed while saving. Refresh the library and try again.");
            }
            transaction.Commit();
        }
        catch (SqliteException error) { throw StorageError(error); }
        finally { gate.Release(); }
    }
    public async Task RemoveAsync(string id, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM events WHERE job_id=$id; DELETE FROM jobs WHERE id=$id;";
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException error) { throw StorageError(error); }
        finally { gate.Release(); }
    }
    public async Task<IReadOnlyList<DiagnosticEvent>> ReadDiagnosticEventsAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT job_id,stage,timestamp FROM (SELECT sequence,job_id,stage,timestamp FROM events ORDER BY sequence DESC LIMIT 2000) ORDER BY sequence";
            await using var reader = await command.ExecuteReaderAsync(ct);
            var events = new List<DiagnosticEvent>();
            var previous = new Dictionary<string, DateTimeOffset>();
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetString(0);
                if (id.Length != 32 || !id.All(Uri.IsHexDigit) || !Enum.TryParse<JobStage>(reader.GetString(1), out var stage) || !Enum.IsDefined(stage) || !DateTimeOffset.TryParse(reader.GetString(2), out var timestamp)) continue;
                var elapsed = previous.TryGetValue(id, out var prior) ? Math.Max(0, (timestamp - prior).TotalMilliseconds) : (double?)null;
                events.Add(new(id, stage, timestamp, elapsed)); previous[id] = timestamp;
            }
            return events;
        }
        catch (SqliteException error) { throw StorageError(error); }
        finally { gate.Release(); }
    }
}
