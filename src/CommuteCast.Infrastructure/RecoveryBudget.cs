using System.Text.Json;

namespace CommuteCast.Infrastructure;

// A successful readiness probe clears the episode. Failure or process loss keeps the
// spent allowance, shared by subsequent jobs and relaunches until a deliberate retry.
public sealed class RecoveryBudget(Workspace workspace)
{
    private string PathFor(string engine) => Path.Combine(workspace.Root, engine is "kokoro" or "piper" ? $"recovery-{engine}.json" : throw new ArgumentException("Unsupported engine."));
    public async Task BeginAsync(string engine, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var path = PathFor(engine);
        if (File.Exists(path)) throw new IOException("Automatic speech recovery is exhausted for this outage. Repair Docker Desktop, then use Check speech readiness or Retry / resume. Saved audio is retained.");
        await Workspace.AtomicWriteAsync(path, JsonSerializer.Serialize(new { engine, startedUtc = DateTimeOffset.UtcNow, allowanceSeconds = 120, desktopLaunches = 1, ownedStarts = 1 }));
    }
    public Task ResetAsync(string engine)
    {
        var path = PathFor(engine); if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}
