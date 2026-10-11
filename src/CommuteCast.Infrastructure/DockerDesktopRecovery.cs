using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("CommuteCast.Tests")]

namespace CommuteCast.Infrastructure;

internal static class DockerDesktopRecovery
{
    // Called only after the explicit restart has stopped the verified Desktop
    // processes. Error 1920 identifies the inaccessible Windows AF_UNIX endpoint;
    // ordinary files, accessible sockets and permission errors are preserved.
    internal static string[] QuarantineInaccessibleRuntime(string localApplicationData, Func<string, bool>? inaccessible = null, Func<string, FileAttributes>? inspect = null)
    {
        var candidates = new List<string>();
        foreach (var (relative, endpoint, names) in new[] {
            (Path.Combine("Docker", "run"), "dockerInference", new[] { "dockerInference", "userAnalyticsOtlpHttp.sock" }),
            ("docker-secrets-engine", "engine.sock", new[] { "engine.sock" }) })
        {
            var runtime = Path.GetFullPath(Path.Combine(localApplicationData, relative));
            if (CanQuarantine(runtime, endpoint, names, inaccessible ?? InaccessibleSocket, inspect ?? File.GetAttributes)) candidates.Add(runtime);
        }
        // Inspect both locations before moving either, so unknown data in the
        // second folder does not leave the first partly recovered.
        var preserved = new List<string>();
        foreach (var runtime in candidates)
        {
            var quarantine = runtime + ".commutecast-recovery-" + Guid.NewGuid().ToString("N");
            if (!string.Equals(Path.GetDirectoryName(runtime), Path.GetDirectoryName(quarantine), StringComparison.OrdinalIgnoreCase))
                throw new IOException("The Docker runtime quarantine escaped its expected directory.");
            Workspace.RejectReparsePoints(runtime);
            Directory.Move(runtime, quarantine);
            preserved.Add(quarantine);
        }
        return preserved.ToArray();
    }

    private static bool CanQuarantine(string runtime, string endpoint, string[] names, Func<string, bool> inaccessible, Func<string, FileAttributes> inspect)
    {
        if (!Directory.Exists(runtime)) return false;
        Workspace.RejectReparsePoints(runtime);
        var socket = Path.Combine(runtime, endpoint);
        FileAttributes attributes;
        try { attributes = inspect(socket); }
        catch (FileNotFoundException) { return false; }
        if (!attributes.HasFlag(FileAttributes.ReparsePoint) || attributes.HasFlag(FileAttributes.Directory) || !inaccessible(socket)) return false;
        foreach (var path in Directory.EnumerateFileSystemEntries(runtime))
        {
            var entry = new FileInfo(path);
            if (entry.Attributes.HasFlag(FileAttributes.Directory) || entry.Length != 0 ||
                !names.Any(n => entry.Name == n || entry.Name.StartsWith(n + ".stale", StringComparison.Ordinal)))
                throw new IOException("Docker's runtime folder contains unexpected data. It was preserved; ask your administrator to inspect the inaccessible socket.");
        }
        return true;
    }

    private static bool InaccessibleSocket(string socket)
    {
        try { using var handle = File.OpenHandle(socket); return false; }
        catch (IOException error) when ((error.HResult & 0xffff) == 1920) { return true; }
    }
}
