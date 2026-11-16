using CommuteCast.Core;
using Microsoft.Win32;

namespace CommuteCast.Infrastructure;

public sealed record FolderLocation(string Label, string Path);

public sealed record OneDriveFolders(string Root)
{
    public const string RootName = "CommuteCast";
    public const string InboxName = "Inbox";
    public const string WorkersName = "Workers";
    public const string StatusName = "Status";
    public const string StagingName = "Staging";
    public const string ListeningName = "Ready to Listen";

    public string Listening => Path.Combine(Root, ListeningName);

    public IReadOnlyList<FolderLocation> Locations() =>
    [
        new("Shared queue root", Root),
        new("Job requests", Path.Combine(Root, InboxName)),
        new("Worker check-ins", Path.Combine(Root, WorkersName)),
        new("Job status", Path.Combine(Root, StatusName)),
        new("Delivery staging", Path.Combine(Root, StagingName)),
        new("Remote worker MP3s", Listening)
    ];

    public static IReadOnlyList<FolderLocation> Describe(string? sharedRoot, string exports, string workspace, string logs)
    {
        List<FolderLocation> folders = string.IsNullOrWhiteSpace(sharedRoot)
            ? [new("Shared queue root", "Not configured")]
            : [.. new OneDriveFolders(sharedRoot).Locations()];
        folders.Add(new("Local generation MP3 exports", string.IsNullOrWhiteSpace(exports) ? "Not configured" : exports));
        folders.Add(new("Private workspace", workspace));
        folders.Add(new("Local job files", Path.Combine(workspace, "jobs")));
        folders.Add(new("Operational logs", logs));
        return folders;
    }

    public static string? PersonalSyncRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            using var account = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts\Personal");
            var registered = account?.GetValue("UserFolder") as string;
            if (Directory.Exists(registered)) return registered;
        }
        var consumer = Environment.GetEnvironmentVariable("OneDriveConsumer");
        return Directory.Exists(consumer) ? consumer : null;
    }

    public static OneDriveFolders? PrepareDefault(Workspace workspace, string? personalSyncRoot)
    {
        if (string.IsNullOrWhiteSpace(personalSyncRoot) || !Path.IsPathFullyQualified(personalSyncRoot) ||
            personalSyncRoot.StartsWith(@"\\", StringComparison.Ordinal) || !Directory.Exists(personalSyncRoot))
            return null;
        var folders = new OneDriveFolders(Path.Combine(Path.GetFullPath(personalSyncRoot), RootName));
        if (Workspace.IsWithin(workspace.Root, folders.Root) || Workspace.IsWithin(folders.Root, workspace.Root))
            throw new IOException("The shared OneDrive folder must be separate from the private workspace.");
        RemoteQueueFolder.Guard(folders.Listening);
        Directory.CreateDirectory(folders.Listening);
        return folders;
    }

    public static bool ApplyDesktopDefaults(AppSettings settings, Workspace workspace, string? personalSyncRoot)
    {
        if (settings.RemoteQueue is not null && !string.IsNullOrWhiteSpace(settings.Destination)) return false;
        var folders = settings.RemoteQueue is { } configured
            ? new OneDriveFolders(new RemoteQueueFolder(configured.Folder, workspace).Root)
            : PrepareDefault(workspace, personalSyncRoot);
        if (folders is null) return false;

        settings.RemoteQueue ??= new(folders.Root, false, Environment.MachineName, "", []);
        if (string.IsNullOrWhiteSpace(settings.Destination))
        {
            RemoteQueueFolder.Guard(folders.Listening);
            Directory.CreateDirectory(folders.Listening);
            workspace.GuardLocalDestination(folders.Listening);
            settings.Destination = folders.Listening;
        }
        return true;
    }
}
