using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CommuteCast.Tests;

public sealed class RemoteQueueFolderTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("CommuteCast-remote-").FullName;
    private static string Id() => Guid.NewGuid().ToString("N");
    private RemoteQueueFolder Open() => new(Directory.CreateDirectory(Path.Combine(root, "shared")).FullName,
        new Workspace(Path.Combine(root, "private")));

    [Fact]
    public async Task ConcurrentSubmissionsWithTheSameIdPreserveTheFirstRequest()
    {
        var folder = Open();
        var request = new RemoteRequest(Id(), Id(), DateTimeOffset.UtcNow, "Morning", "Listen to this.",
            new("kokoro", "af_heart", 1, false, "", new()));

        await Task.WhenAll(folder.SubmitAsync(request), Open().SubmitAsync(request));
        await Assert.ThrowsAsync<IOException>(() => folder.SubmitAsync(request with { Text = "Different text" }));

        File.WriteAllText(Path.Combine(folder.Root, "Inbox", Id() + ".json.tmp"), "incomplete");
        Assert.Equal(request.JobId, Assert.Single(Open().RequestIds()));
        Assert.Equal(request, await Open().ReadRequestAsync(request.JobId));
    }

    [Fact]
    public async Task LegacyRequestsKeepTheirFingerprintAndCannotCarryHostedConsent()
    {
        var folder = Open();
        var request = new RemoteRequest(Id(), Id(), DateTimeOffset.UtcNow, "Morning", "Listen to this.",
            new("kokoro", "af_heart", 1, false, "", new()));
        var json = JsonSerializer.Serialize(new { request.JobId, request.TargetWorkerId, request.CreatedUtc, request.Title,
            request.Text, request.Options, request.Version, request.Preset }, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
        });
        await File.WriteAllTextAsync(Path.Combine(Directory.CreateDirectory(Path.Combine(folder.Root, "Inbox")).FullName, request.JobId + ".json"), json);

        var saved = Assert.IsType<RemoteRequest>(await folder.ReadRequestAsync(request.JobId));
        Assert.False(saved.AllowHostedFallback);
        Assert.Equal(Job.Hash(json), RemoteQueueFolder.Fingerprint(saved));
        await Assert.ThrowsAsync<ArgumentException>(() => folder.SubmitAsync(saved with { AllowHostedFallback = true }));
        await Assert.ThrowsAsync<IOException>(() => folder.SubmitAsync(saved with { Version = RemoteRequest.CurrentVersion }));
    }

    [Fact]
    public async Task IndependentCheckInsPreserveExplicitStatesAndDetectStaleWorkers()
    {
        var now = DateTimeOffset.UtcNow;
        var fresh = new RemoteWorkerStatus(Id(), "Basement", now, RemoteWorkerState.Busy, ["Warm narrator"]);
        var stale = new RemoteWorkerStatus(Id(), "Office", now.AddMinutes(-6), RemoteWorkerState.Ready, []);
        var disabled = new RemoteWorkerStatus(Id(), "Spare", now.AddDays(-5), RemoteWorkerState.Disabled, []);
        await Task.WhenAll(Open().WriteWorkerAsync(fresh), Open().WriteWorkerAsync(stale), Open().WriteWorkerAsync(disabled));
        File.WriteAllText(Path.Combine(Open().Root, "Workers", Id() + ".json"), "{broken");

        var roster = (await Open().ReadWorkersAsync()).ToDictionary(w => w.WorkerId);
        Assert.Equal(3, roster.Count);
        Assert.Equal(RemoteWorkerState.Busy, roster[fresh.WorkerId].StateAt(now));
        Assert.Equal("Warm narrator", Assert.Single(roster[fresh.WorkerId].Presets));
        Assert.Equal(RemoteWorkerState.Offline, roster[stale.WorkerId].StateAt(now));
        Assert.Equal(RemoteWorkerState.Disabled, roster[disabled.WorkerId].StateAt(now));
        await Open().WriteWorkerAsync(fresh with { State = RemoteWorkerState.Stopped });
        Assert.Equal(RemoteWorkerState.Stopped, (await Open().ReadWorkersAsync()).Single(w => w.WorkerId == fresh.WorkerId).StateAt(now.AddDays(1)));
    }

    [Fact]
    public async Task StatusCanAdvanceToReadyWithTheActualVoiceAndAFileInsideTheDeliveryFolder()
    {
        var folder = Open();
        var status = new RemoteJobStatus(Id(), Id(), DateTimeOffset.UtcNow, RemoteJobState.Generating,
            new("kokoro", "af_heart", 1), new("piper", "en_US-lessac-medium", 1, Preset: "Even reading"),
            ChunksDone: 2, ChunksTotal: 3, FallbackReason: "The requested voice is missing.");
        await folder.WriteStatusAsync(status);
        Assert.Equal(status, await Open().ReadStatusAsync(status.JobId));

        var ready = status with { State = RemoteJobState.Ready, ChunksDone = 3, OutputFile = status.JobId + ".mp3" };
        await Assert.ThrowsAsync<IOException>(() => folder.WriteStatusAsync(ready with { OutputFile = "../outside.mp3" }));
        await folder.WriteStatusAsync(ready);
        Assert.Equal(ready, await Open().ReadStatusAsync(status.JobId));
        Assert.Equal(Path.Combine(folder.Root, "Ready to Listen", ready.OutputFile!), folder.OutputPath(ready.OutputFile!));
    }

    public void Dispose() => Directory.Delete(root, true);
}
