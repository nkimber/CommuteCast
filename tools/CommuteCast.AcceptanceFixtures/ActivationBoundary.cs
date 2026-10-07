using CommuteCast.Infrastructure;
using System.Reflection;
using System.Text.Json;

internal static class ActivationBoundary
{
    internal static async Task RunAsync(string[] args, WorkspaceLease lease, string marker)
    {
        if (!Enum.TryParse<InstallationCheckpoint>(args[3], out var point) || args[3] != point.ToString() ||
            point is not (InstallationCheckpoint.Prepared or InstallationCheckpoint.StateMigrated or InstallationCheckpoint.BeforeActivation or InstallationCheckpoint.Activated))
            throw new IOException("Choose an actual activation checkpoint.");
        var parent = Path.GetDirectoryName(lease.Workspace.Root)!;
        var source = Path.GetFullPath(args[2]);
        if (source != Path.Combine(parent, "source-b")) throw new IOException("Use this isolated fixture's own revision B.");
        Workspace.RejectReparsePoints(source);
        if (File.Exists(marker) || Directory.Exists(marker)) throw new IOException("Use a fresh activation boundary marker.");
        var package = await ReleasePackage.ValidateAsync(source);
        var installation = new Installation(Path.Combine(parent, "program"));
        await installation.ActivateAsync(lease, source, new Observer(async checkpoint =>
        {
            if (checkpoint != point) return;
            await Workspace.AtomicWriteAsync(marker, JsonSerializer.Serialize(new
            {
                processId = Environment.ProcessId, point = point.ToString(), package.PackageId,
                applicationBuild = typeof(Installation).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                schemaVersion = await SqliteSchema.ValidateDatabaseAsync(Path.Combine(lease.Workspace.Root, "queue.db"))
            }));
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }));
        throw new IOException("Activation did not reach the requested boundary.");
    }
    private sealed class Observer(Func<InstallationCheckpoint, Task> barrier) : IInstallationObserver
    {
        public Task ReachedAsync(InstallationCheckpoint checkpoint, string? item, CancellationToken ct) => barrier(checkpoint);
    }
}
