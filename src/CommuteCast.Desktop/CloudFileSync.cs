using System.IO;
using System.Runtime.InteropServices;
using CommuteCast.Core;

namespace CommuteCast.Desktop;

public enum CloudSyncState { Unknown, InSync, NotInSync, Missing }

public static class CloudFileSync
{
    // Query directory metadata only: do not open/hydrate the audio or change provider state.
    public static CloudSyncState Read(Job job)
    {
        if (!job.ExportCommitted || job.DeletionRequested) return CloudSyncState.Unknown;
        try
        {
            if (string.IsNullOrEmpty(job.ExportName) || Path.GetFileName(job.ExportName) != job.ExportName || job.ExportName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return CloudSyncState.Unknown;
            var path = Path.GetFullPath(Path.Combine(job.Destination, job.ExportName));
            if (!Path.IsPathFullyQualified(job.Destination) || path.StartsWith(@"\\", StringComparison.Ordinal)) return CloudSyncState.Unknown;
            var handle = FindFirstFile(path, out var data);
            if (handle == new IntPtr(-1))
                return Marshal.GetLastWin32Error() is 2 or 3 ? CloudSyncState.Missing : CloudSyncState.Unknown;
            try { return FromPlaceholderState(CfGetPlaceholderStateFromAttributeTag(data.Attributes, data.ReparseTag)); }
            finally { FindClose(handle); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        { return CloudSyncState.Unknown; }
    }

    public static CloudSyncState FromPlaceholderState(uint state) => state == uint.MaxValue || (state & 1) == 0
        ? CloudSyncState.Unknown : (state & 8) != 0 ? CloudSyncState.InSync : CloudSyncState.NotInSync;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FindData
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint SizeHigh, SizeLow, ReparseTag, Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateName;
    }
    [DllImport("kernel32.dll", EntryPoint = "FindFirstFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFile(string path, out FindData data);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr handle);
    [DllImport("cldapi.dll")]
    private static extern uint CfGetPlaceholderStateFromAttributeTag(uint attributes, uint reparseTag);
}
