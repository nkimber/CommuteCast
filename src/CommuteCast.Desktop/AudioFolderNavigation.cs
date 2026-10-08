using System.Diagnostics;
using System.IO;
using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Desktop;

public static class AudioFolderNavigation
{
    public static ProcessStartInfo ForJob(Job job, Workspace workspace)
    {
        var generated = !job.ExportCommitted && job.FinalHash.Length > 0;
        var folder = generated ? workspace.JobDirectory(job.Id) : job.Destination;
        if (!Directory.Exists(folder)) throw new IOException("The recorded audio folder is missing or moved.");
        string? file = null;
        if (job.ExportCommitted && job.ExportName.Length > 0)
        {
            if (Path.GetFileName(job.ExportName) != job.ExportName || !job.ExportName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                throw new IOException("The recorded MP3 filename is invalid. Open the saved narration details for inspection.");
            file = Path.Combine(folder, job.ExportName);
        }
        else if (generated) file = workspace.FinalPath(job);
        return file is not null && File.Exists(file)
            ? new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false, Arguments = "/select,\"" + Path.GetFullPath(file) + "\"" }
            : new ProcessStartInfo(Path.GetFullPath(folder)) { UseShellExecute = true };
    }
}
