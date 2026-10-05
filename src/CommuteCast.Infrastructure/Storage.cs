using CommuteCast.Core;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Security.Cryptography;

namespace CommuteCast.Infrastructure;

public sealed class Workspace
{
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
    public Task SaveSettingsAsync(AppSettings settings) => AtomicWriteAsync(Path.Combine(Root, "settings.json"), JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    public static async Task AtomicWriteAsync(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            await file.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text));
            file.Flush(true);
        }
        File.Move(temporary, path, true);
    }
}

public sealed class SqliteJobStore : IJobStore
{
    private readonly string connectionString;
    private readonly SemaphoreSlim gate = new(1);
    public SqliteJobStore(Workspace workspace) => connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(workspace.Root, "queue.db"), Pooling = false }.ToString();
    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY, created TEXT NOT NULL, payload TEXT NOT NULL); CREATE TABLE IF NOT EXISTS events(sequence INTEGER PRIMARY KEY AUTOINCREMENT, job_id TEXT NOT NULL, stage TEXT NOT NULL, timestamp TEXT NOT NULL);";
        await command.ExecuteNonQueryAsync(ct);
        return connection;
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
        finally { gate.Release(); }
    }
    public async Task<IReadOnlyList<Job>> LoadAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM jobs ORDER BY created";
            await using var reader = await command.ExecuteReaderAsync(ct);
            var jobs = new List<Job>();
            while (await reader.ReadAsync(ct)) jobs.Add(JsonSerializer.Deserialize<Job>(reader.GetString(0)) ?? throw new IOException("Queue record is unreadable. Private data is preserved."));
            return jobs;
        }
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
        finally { gate.Release(); }
    }
}
