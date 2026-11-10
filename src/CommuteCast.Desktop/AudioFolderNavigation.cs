using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Desktop;

public static class AudioFolderNavigation
{
    public static Task<bool> ShowAsync(Job job, Workspace workspace) => Task.Run(() => Show(job, workspace));

    private static bool Show(Job job, Workspace workspace)
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
        if (file is not null && File.Exists(file))
        {
            SelectFile(Path.GetFullPath(file));
            return true;
        }

        using var process = Process.Start(new ProcessStartInfo(Path.GetFullPath(folder)) { UseShellExecute = true });
        return false;
    }

    private static void SelectFile(string path)
    {
        const uint coinitMultithreaded = 0;
        Marshal.ThrowExceptionForHR(CoInitializeEx(0, coinitMultithreaded));
        nint item = 0;
        try
        {
            var result = SHParseDisplayName(path, 0, out item, 0, 0);
            // A single absolute item ID lets Explorer locate the folder and reuse its view.
            if (result >= 0) result = SHOpenFolderAndSelectItems(item, 0, 0, 0);
            if (result < 0) throw new IOException("Windows could not show the recorded MP3 in File Explorer.", Marshal.GetExceptionForHR(result));
        }
        finally
        {
            Marshal.FreeCoTaskMem(item);
            CoUninitialize();
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(nint reserved, uint flags);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(string path, nint bindingContext, out nint item, uint attributesIn, nint attributesOut);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(nint folderOrItem, uint itemCount, nint children, uint flags);
}
