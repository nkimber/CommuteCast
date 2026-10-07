using CommuteCast.Infrastructure;
using System.Reflection;
using System.Text.Json;

internal static class RollbackBoundary
{
    internal static async Task RunAsync(WorkspaceLease lease, bool afterCommit)
    {
        var parent = Path.GetDirectoryName(lease.Workspace.Root)!;
        var marker = Path.Combine(parent, "rollback-boundary.json");
        if (File.Exists(marker) || Directory.Exists(marker)) throw new IOException("Use a fresh rollback fixture marker.");
        var installation = new Installation(Path.Combine(parent, "program"));
        await installation.RollbackAsync(lease, new Observer(async checkpoint =>
        {
            if (checkpoint != (afterCommit ? InstallationCheckpoint.Activated : InstallationCheckpoint.StateRestored)) return;
            var jobs = await new SqliteJobStore(lease.Workspace).LoadAsync();
            await Workspace.AtomicWriteAsync(marker, JsonSerializer.Serialize(new
            {
                processId = Environment.ProcessId, point = checkpoint.ToString(), restoredJobs = jobs.Count,
                applicationBuild = typeof(Installation).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            }));
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }));
        throw new IOException("Rollback did not reach the selected checkpoint.");
    }
    private sealed class Observer(Func<InstallationCheckpoint, Task> barrier) : IInstallationObserver
    {
        public Task ReachedAsync(InstallationCheckpoint checkpoint, string? item, CancellationToken ct) => barrier(checkpoint);
    }
}
