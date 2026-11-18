using Microsoft.Win32;

namespace CommuteCast.Infrastructure;

public sealed record FolderLocation(string Label, string Path);

public sealed record OneDriveFolders(string Root)
{
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

    // Compare saved paths with the old automatic choice without creating a folder or changing settings.
    public static OneDriveFolders? DiscoverPreviousDesktopDefault()
    {
        string? personalRoot = null;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var account = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts\Personal");
                personalRoot = account?.GetValue("UserFolder") as string;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        if (string.IsNullOrWhiteSpace(personalRoot)) personalRoot = Environment.GetEnvironmentVariable("OneDriveConsumer");
        return string.IsNullOrWhiteSpace(personalRoot) || !Path.IsPathFullyQualified(personalRoot)
            ? null : new(Path.Combine(Path.GetFullPath(personalRoot), "CommuteCast"));
    }

    public static bool NeedsConfirmation(string? selected, string? confirmed, string? previousDefault) =>
        SameLocalPath(selected, previousDefault) && !SameLocalPath(selected, confirmed);

    private static bool SameLocalPath(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second) ||
            !Path.IsPathFullyQualified(first) || !Path.IsPathFullyQualified(second)) return false;
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }
}
