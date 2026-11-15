using System.Runtime.InteropServices;
using CommuteCast.Core;

namespace CommuteCast.Infrastructure;

public enum CloudSyncState { Unknown, InSync, NotInSync, Missing }

public static class CloudFileSync
{
    private const sbyte ExposePlaceholders = 2;
    private const uint Placeholder = 1, InSync = 8;

    // Query directory metadata only: do not open/hydrate the audio or change provider state.
    public static CloudSyncState Read(Job job)
    {
        if (!job.ExportCommitted || job.DeletionRequested) return CloudSyncState.Unknown;
        try
        {
            if (string.IsNullOrEmpty(job.ExportName) || Path.GetFileName(job.ExportName) != job.ExportName || job.ExportName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return CloudSyncState.Unknown;
            var path = Path.GetFullPath(Path.Combine(job.Destination, job.ExportName));
            if (!Path.IsPathFullyQualified(job.Destination) || path.StartsWith(@"\\", StringComparison.Ordinal)) return CloudSyncState.Unknown;
            return ReadPath(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        { return CloudSyncState.Unknown; }
    }

    internal static CloudSyncState ReadPath(string path, bool allowDirectory = false)
    {
        if (!OperatingSystem.IsWindows()) return CloudSyncState.Unknown;
        // Compatibility mode can hide the cloud tag. Restore the thread mode before returning to the pool.
        var previousMode = RtlSetThreadPlaceholderCompatibilityMode(ExposePlaceholders);
        if (previousMode < 0) return CloudSyncState.Unknown;
        try
        {
            var handle = FindFirstFile(path, out var data);
            if (handle == new IntPtr(-1))
                return Marshal.GetLastWin32Error() is 2 or 3 ? CloudSyncState.Missing : CloudSyncState.Unknown;
            try
            {
                if (!allowDirectory && (data.Attributes & (uint)FileAttributes.Directory) != 0) return CloudSyncState.Unknown;
                return FromPlaceholderState(CfGetPlaceholderStateFromAttributeTag(data.Attributes, data.ReparseTag));
            }
            finally { FindClose(handle); }
        }
        finally { RtlSetThreadPlaceholderCompatibilityMode(previousMode); }
    }

    public static CloudSyncState FromPlaceholderState(uint state) => state == uint.MaxValue || (state & Placeholder) == 0
        ? CloudSyncState.Unknown : (state & InSync) != 0 ? CloudSyncState.InSync : CloudSyncState.NotInSync;

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
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern sbyte RtlSetThreadPlaceholderCompatibilityMode(sbyte mode);
}
