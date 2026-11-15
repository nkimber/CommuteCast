using System.Text.Json;
using System.Text.Json.Serialization;
using CommuteCast.Core;

namespace CommuteCast.Infrastructure;

public sealed class RemoteQueueFolder
{
    private const int MaximumRequestBytes = 2 * 1024 * 1024;
    private const int MaximumStatusBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public string Root { get; }
    public string OutputFolder => Path.Combine(Root, "Ready to Listen");

    public RemoteQueueFolder(string root, Workspace workspace)
    {
        if (!Path.IsPathFullyQualified(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Choose an existing local OneDrive folder for remote narration.");
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(Root) || Workspace.IsWithin(workspace.Root, Root) || Workspace.IsWithin(Root, workspace.Root))
            throw new ArgumentException("The shared queue folder must exist and be separate from CommuteCast's private workspace.");
        Guard(Root);
    }

    public async Task SubmitAsync(RemoteRequest request, CancellationToken ct = default)
    {
        request.Validate();
        ct.ThrowIfCancellationRequested();
        var path = RecordPath("Inbox", request.JobId);
        var json = JsonSerializer.Serialize(request, Json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumRequestBytes)
            throw new ArgumentException("The remote request is too large for the shared queue.");
        try { await Workspace.AtomicWriteAsync(path, json, false, Guard); }
        catch (IOException) when (File.Exists(path))
        {
            var existing = await ReadRequestAsync(request.JobId, ct);
            if (existing is null || Fingerprint(existing) != Fingerprint(request))
                throw new IOException("This remote job ID already belongs to a different request.");
        }
    }

    public static string Fingerprint(RemoteRequest request) => Job.Hash(JsonSerializer.Serialize(request, Json));

    internal bool MatchesRoot(string path) => Root.Equals(Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> RequestIds() => ListIds("Inbox");

    public async Task<RemoteRequest?> ReadRequestAsync(string id, CancellationToken ct = default)
    {
        var request = await ReadAsync<RemoteRequest>("Inbox", id, MaximumRequestBytes, ct);
        if (request is null) return null;
        request.Validate();
        if (request.JobId != id) throw new IOException("The request identity does not match its filename.");
        return request;
    }

    public Task WriteStatusAsync(RemoteJobStatus status)
    {
        ValidateStatus(status, status.JobId);
        return WriteAsync("Status", status.JobId, status);
    }

    public async Task<RemoteJobStatus?> ReadStatusAsync(string id, CancellationToken ct = default)
    {
        var status = await ReadAsync<RemoteJobStatus>("Status", id, MaximumStatusBytes, ct);
        if (status is not null) ValidateStatus(status, id);
        return status;
    }

    public Task WriteWorkerAsync(RemoteWorkerStatus worker)
    {
        ValidateWorker(worker, worker.WorkerId);
        return WriteAsync("Workers", worker.WorkerId, worker);
    }

    public async Task<IReadOnlyList<RemoteWorkerStatus>> ReadWorkersAsync(CancellationToken ct = default)
    {
        var workers = new List<RemoteWorkerStatus>();
        foreach (var id in ListIds("Workers"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var worker = await ReadAsync<RemoteWorkerStatus>("Workers", id, MaximumStatusBytes, ct);
                if (worker is null) continue;
                ValidateWorker(worker, id);
                workers.Add(worker);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return workers.OrderBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(w => w.WorkerId).ToArray();
    }

    public string OutputPath(string filename)
    {
        if (!IsOutputName(filename)) throw new ArgumentException("The shared output must be an MP3 filename.");
        var path = Path.Combine(OutputFolder, filename);
        Guard(path);
        return path;
    }

    public async Task<string> PublishAsync(Job job, Workspace workspace, CancellationToken ct = default)
    {
        if (job.Remote is null || !MatchesRoot(job.Remote.QueueRoot) ||
            job.Stage != JobStage.Generated || job.FinalHash.Length == 0 || !RemoteIdentity.IsValid(job.Id))
            throw new InvalidOperationException("Publish only a validated narration assigned to this folder.");
        var name = ExportPublisher.Filename(job);
        var destination = OutputPath(name);
        if (File.Exists(destination))
        {
            if (await Workspace.HashFileAsync(destination, ct) != job.FinalHash)
                throw new IOException("A different file already occupies this narration's output name.");
            return name;
        }

        var source = workspace.FinalPath(job);
        Workspace.RejectReparsePoints(Path.GetDirectoryName(source)!);
        Workspace.RejectFileReparsePoint(source);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(input, ct)) != job.FinalHash)
            throw new IOException("The locally validated narration has changed. Use Retry on the worker.");
        input.Position = 0;

        Directory.CreateDirectory(OutputFolder);
        var temporary = Path.Combine(Root, "Staging", $".{job.Id}-{Guid.NewGuid():N}.partial");
        Guard(temporary);
        Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
        var created = false;
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                created = true;
                await input.CopyToAsync(output, ct);
                output.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            Guard(destination);
            File.Move(temporary, destination);
            return name;
        }
        finally
        {
            if (created && File.Exists(temporary)) { Guard(temporary); File.Delete(temporary); }
        }
    }

    private static bool IsOutputName(string filename) => filename.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) &&
        filename.Length <= 200 && !filename.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c));

    private static void ValidateStatus(RemoteJobStatus status, string id)
    {
        if (status.Version != 1 || status.JobId != id || !RemoteIdentity.IsValid(id) || !RemoteIdentity.IsValid(status.WorkerId) ||
            !Enum.IsDefined(status.State) || status.UpdatedUtc == default || status.RequestedVoice is null ||
            status.ChunksDone < 0 || status.ChunksTotal < status.ChunksDone ||
            (status.State == RemoteJobState.Ready && status.OutputFile is null) ||
            (status.OutputFile is not null && !IsOutputName(status.OutputFile)))
            throw new IOException("The shared job status is invalid.");
    }

    private static void ValidateWorker(RemoteWorkerStatus worker, string id)
    {
        if (worker.Version != 1 || worker.WorkerId != id || !RemoteIdentity.IsValid(id) || string.IsNullOrWhiteSpace(worker.Name) ||
            worker.Name.Length > 120 || worker.UpdatedUtc == default || worker.Presets is null || worker.Presets.Length > 100 ||
            worker.Presets.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 160) ||
            !Enum.IsDefined(worker.State) || worker.State == RemoteWorkerState.Offline)
            throw new IOException("The shared worker status is invalid.");
    }

    private string RecordPath(string folder, string id)
    {
        if (!RemoteIdentity.IsValid(id)) throw new ArgumentException("Invalid shared queue identity.");
        var path = Path.Combine(Root, folder, id + ".json");
        Guard(path);
        return path;
    }

    private IReadOnlyList<string> ListIds(string folder)
    {
        var path = Path.Combine(Root, folder);
        Guard(path);
        return Directory.Exists(path) ? Directory.EnumerateFiles(path, "*.json")
            .Where(file => Path.GetExtension(file) == ".json")
            .Select(file => Path.GetFileNameWithoutExtension(file)).Where(RemoteIdentity.IsValid).ToArray() : [];
    }

    private Task WriteAsync<T>(string folder, string id, T value)
    {
        var json = JsonSerializer.Serialize(value, Json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumStatusBytes) throw new IOException("Shared queue status is too large.");
        return Workspace.AtomicWriteAsync(RecordPath(folder, id), json, true, Guard);
    }

    private async Task<T?> ReadAsync<T>(string folder, string id, int maxBytes, CancellationToken ct) where T : class
    {
        var path = RecordPath(folder, id);
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
            if (file.Length is <= 0 || file.Length > maxBytes) throw new IOException("The shared queue file has an invalid size.");
            var bytes = new byte[(int)file.Length];
            await file.ReadExactlyAsync(bytes, ct);
            return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new JsonException("The shared queue file is empty.");
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    private static void Guard(string path)
    {
        for (var item = path; item is not null; item = Path.GetDirectoryName(item))
        {
            try
            {
                if (File.GetAttributes(item).HasFlag(FileAttributes.ReparsePoint) &&
                    CloudFileSync.ReadPath(item, allowDirectory: true) is not (CloudSyncState.InSync or CloudSyncState.NotInSync))
                    throw new IOException("Only ordinary local paths and cloud sync placeholders are supported in the shared queue.");
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        }
    }
}
