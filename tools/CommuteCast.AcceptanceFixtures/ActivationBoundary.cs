using CommuteCast.Infrastructure;
using System.Reflection;
using System.Text.Json;

internal static class ActivationBoundary
{
    internal static async Task RunAsync(string[] args, WorkspaceLease lease, string marker)
    {
        var migrationBoundary = args[3] == "MigrationBeforeCommit";
        if (!Enum.TryParse<InstallationCheckpoint>(args[3], out var point) && !migrationBoundary || !migrationBoundary && (args[3] != point.ToString() ||
            point is not (InstallationCheckpoint.Prepared or InstallationCheckpoint.StateMigrated or InstallationCheckpoint.BeforeActivation or InstallationCheckpoint.Activated)))
            throw new IOException("Choose an actual activation checkpoint.");
        var parent = Path.GetDirectoryName(lease.Workspace.Root)!;
        var source = Path.GetFullPath(args[2]);
        if (source != Path.Combine(parent, "source-b")) throw new IOException("Use this isolated fixture's own revision B.");
        Workspace.RejectReparsePoints(source);
        if (File.Exists(marker) || Directory.Exists(marker)) throw new IOException("Use a fresh activation boundary marker.");
        var package = await ReleasePackage.ValidateAsync(source);
        async Task HoldAsync(int schemaVersion, int? migrationFrom = null, int? migrationTo = null)
        {
            await Workspace.AtomicWriteAsync(marker, JsonSerializer.Serialize(new
            {
                processId = Environment.ProcessId, point = args[3], package.PackageId,
                applicationBuild = typeof(Installation).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                schemaVersion, migrationFrom, migrationTo
            }));
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        var installation = new Installation(Path.Combine(parent, "program"), migrationBoundary ? new MigrationObserver(HoldAsync) : null);
        await installation.ActivateAsync(lease, source, new Observer(async checkpoint =>
        {
            if (migrationBoundary || checkpoint != point) return;
            await HoldAsync(await SqliteSchema.ValidateDatabaseAsync(Path.Combine(lease.Workspace.Root, "queue.db")));
        }));
        throw new IOException("Activation did not reach the requested boundary.");
    }
    private sealed class MigrationObserver(Func<int, int?, int?, Task> barrier) : ISchemaMigrationObserver
    {
        public Task BeforeCommitAsync(int fromVersion, int toVersion, CancellationToken ct)
        {
            if (fromVersion != 3 || toVersion != 4) throw new IOException("Use this fixture's schema 3-to-4 migration.");
            return barrier(fromVersion, fromVersion, toVersion);
        }
    }
    private sealed class Observer(Func<InstallationCheckpoint, Task> barrier) : IInstallationObserver
    {
        public Task ReachedAsync(InstallationCheckpoint checkpoint, string? item, CancellationToken ct) => barrier(checkpoint);
    }
}
