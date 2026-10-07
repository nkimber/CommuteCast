using CommuteCast.Infrastructure;
using System.Reflection;
using System.Text.Json;

internal static class UninstallBoundary
{
    internal static async Task RunAsync(WorkspaceLease lease)
    {
        var parent = Path.GetDirectoryName(lease.Workspace.Root)!;
        var marker = Path.Combine(parent, "uninstall-boundary.json");
        if (File.Exists(marker) || Directory.Exists(marker)) throw new IOException("Use a fresh uninstall fixture marker.");
        var installation = new Installation(Path.Combine(parent, "program"));
        await installation.UninstallAsync(lease, false, new Observer(async (checkpoint, item) =>
        {
            if (checkpoint != InstallationCheckpoint.ItemRemoved || item is null || !item.StartsWith("Package/", StringComparison.Ordinal)) return;
            var parts = item.Split('/', 3);
            var removed = Path.GetFullPath(Path.Combine(installation.Root, "releases", parts[1], parts[2]));
            if (!Workspace.IsWithin(installation.Root, removed) || File.Exists(removed) || Directory.Exists(removed)) throw new IOException("Expected an actually removed owned package file.");
            await Workspace.AtomicWriteAsync(marker, JsonSerializer.Serialize(new
            {
                processId = Environment.ProcessId, point = checkpoint.ToString(), item, removed,
                applicationBuild = typeof(Installation).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            }));
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }));
        throw new IOException("Uninstall did not reach an owned package file removal.");
    }
    private sealed class Observer(Func<InstallationCheckpoint, string?, Task> barrier) : IInstallationObserver
    {
        public Task ReachedAsync(InstallationCheckpoint checkpoint, string? item, CancellationToken ct) => barrier(checkpoint, item);
    }
}
