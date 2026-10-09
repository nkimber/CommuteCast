using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Runtime.InteropServices;

namespace CommuteCast.Tests;

public class CloudFileSyncTests
{
    [Theory]
    [InlineData(0u, CloudSyncState.Unknown)]
    [InlineData(uint.MaxValue, CloudSyncState.Unknown)]
    [InlineData(8u, CloudSyncState.Unknown)]
    [InlineData(1u, CloudSyncState.NotInSync)]
    [InlineData(0x31u, CloudSyncState.NotInSync)]
    [InlineData(9u, CloudSyncState.InSync)]
    [InlineData(0x39u, CloudSyncState.InSync)]
    public void OnlyExplicitCloudInSyncMetadataConfirmsSync(uint flags, CloudSyncState expected)
        => Assert.Equal(expected, CloudFileSync.FromPlaceholderState(flags));

    [Theory]
    [InlineData("../other.mp3")]
    [InlineData("sub\\other.mp3")]
    [InlineData("*.mp3")]
    [InlineData("")]
    public void InvalidExportNamesAreNotQueried(string name)
    {
        using var test = new TestWorkspace();
        Assert.Equal(CloudSyncState.Unknown, CloudFileSync.Read(new Job { ExportCommitted = true, Destination = test.Destination, ExportName = name }));
    }

    [Fact]
    public void UncommittedAndDeletingExportsHaveNoCloudStatus()
    {
        var job = new Job { ExportName = "missing.mp3", Destination = Path.GetTempPath() };
        Assert.Equal(CloudSyncState.Unknown, CloudFileSync.Read(job));
        job.ExportCommitted = true; job.DeletionRequested = true;
        Assert.Equal(CloudSyncState.Unknown, CloudFileSync.Read(job));
    }

    [Theory]
    [InlineData("relative")]
    [InlineData(@"\\server\share")]
    public void NonLocalOrRelativeDestinationsHaveNoCloudStatus(string destination)
        => Assert.Equal(CloudSyncState.Unknown, CloudFileSync.Read(new Job { ExportCommitted = true, Destination = destination, ExportName = "audio.mp3" }));

    [Fact]
    public void MissingOrdinaryAndDirectoryExportsRemainDistinctAndRestoreThreadMode()
    {
        using var test = new TestWorkspace();
        var job = new Job { ExportCommitted = true, Destination = test.Destination, ExportName = "audio.mp3" };
        var previous = Native.RtlSetThreadPlaceholderCompatibilityMode(1);
        Assert.True(previous >= 0);
        try
        {
            Assert.Equal(CloudSyncState.Missing, CloudFileSync.Read(job));
            Assert.Equal((sbyte)1, Native.RtlQueryThreadPlaceholderCompatibilityMode());
            File.WriteAllText(Path.Combine(test.Destination, job.ExportName), "Ordinary local file");
            Assert.Equal(CloudSyncState.Unknown, CloudFileSync.Read(job));
            Assert.Equal((sbyte)1, Native.RtlQueryThreadPlaceholderCompatibilityMode());
            job.ExportName = "folder.mp3"; Directory.CreateDirectory(Path.Combine(test.Destination, job.ExportName));
            Assert.Equal(CloudSyncState.Unknown, CloudFileSync.Read(job));
            Assert.Equal((sbyte)1, Native.RtlQueryThreadPlaceholderCompatibilityMode());
        }
        finally { Native.RtlSetThreadPlaceholderCompatibilityMode(previous); }
    }

    [Theory]
    [InlineData(false, CloudSyncState.NotInSync)]
    [InlineData(true, CloudSyncState.InSync)]
    public void RealCloudPlaceholderIsReadableWhileDisguisedWithoutHydrating(bool synced, CloudSyncState expected)
    {
        // Isolated, disconnected test provider: reading contents would fail. No
        // OneDrive account, network or user's cloud files are involved.
        using var test = new TestWorkspace();
        var registration = new Native.Registration { Size = (uint)Marshal.SizeOf<Native.Registration>(), Name = "CommuteCast regression fixture", Version = "1" };
        var policies = new Native.Policies { Size = (uint)Marshal.SizeOf<Native.Policies>(), Hydration = 2, Population = 3 }; // FULL hydration, ALWAYS_FULL population.
        Marshal.ThrowExceptionForHR(Native.CfRegisterSyncRoot(test.Destination, ref registration, ref policies, 0));
        try
        {
            var identity = Marshal.AllocHGlobal(1);
            try
            {
                Marshal.WriteByte(identity, 1);
                var file = new Native.PlaceholderInfo
                {
                    Name = "synthetic 音声.mp3", Identity = identity, IdentityLength = 1, Flags = synced ? 2u : 0u,
                    Metadata = new() { Basic = new() { Attributes = 0x80 }, Size = 1024 * 1024 }
                };
                Marshal.ThrowExceptionForHR(Native.CfCreatePlaceholders(test.Destination, ref file, 1, 1, out var processed));
                Assert.Equal(1u, processed); Marshal.ThrowExceptionForHR(file.Result);
                var job = new Job { ExportCommitted = true, Destination = test.Destination, ExportName = file.Name };
                var previous = Native.RtlSetThreadPlaceholderCompatibilityMode(1);
                Assert.True(previous >= 0);
                try
                {
                    Native.RtlSetThreadPlaceholderCompatibilityMode(2);
                    var handle = Native.FindFirstFile(Path.Combine(job.Destination, job.ExportName), out var data);
                    Assert.True(handle != new IntPtr(-1), $"FindFirstFile failed: {Marshal.GetLastWin32Error()}");
                    uint rawState;
                    try { rawState = Native.CfGetPlaceholderStateFromAttributeTag(data.Attributes, data.Tag); }
                    finally { Native.FindClose(handle); }
                    Assert.True((rawState & 1) != 0, $"attributes=0x{data.Attributes:X}, tag=0x{data.Tag:X}, state=0x{rawState:X}");
                    Native.RtlSetThreadPlaceholderCompatibilityMode(1);
                    Assert.Equal(expected, CloudFileSync.Read(job));
                    Assert.Equal((sbyte)1, Native.RtlQueryThreadPlaceholderCompatibilityMode());
                    Native.RtlSetThreadPlaceholderCompatibilityMode(2);
                    var attributes = File.GetAttributes(Path.Combine(job.Destination, job.ExportName));
                    Assert.True((attributes & FileAttributes.ReparsePoint) != 0);
                    Assert.True(((uint)attributes & 0x400000) != 0); // RECALL_ON_DATA_ACCESS: still online-only.
                }
                finally { Native.RtlSetThreadPlaceholderCompatibilityMode(previous); }
            }
            finally { Marshal.FreeHGlobal(identity); }
        }
        finally { Marshal.ThrowExceptionForHR(Native.CfUnregisterSyncRoot(test.Destination)); }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct Registration
        {
            public uint Size;
            [MarshalAs(UnmanagedType.LPWStr)] public string Name;
            [MarshalAs(UnmanagedType.LPWStr)] public string Version;
            public IntPtr RootIdentity; public uint RootIdentityLength;
            public IntPtr FileIdentity; public uint FileIdentityLength;
            public Guid ProviderId;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct Policies
        {
            public uint Size;
            // Each policy packs a ushort primary policy and ushort modifiers.
            public uint Hydration, Population, InSync, HardLink, PlaceholderManagement;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct BasicInfo { public long Created, Accessed, Written, Changed; public uint Attributes; }
        [StructLayout(LayoutKind.Sequential)]
        public struct Metadata { public BasicInfo Basic; public long Size; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PlaceholderInfo
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string Name;
            public Metadata Metadata; public IntPtr Identity; public uint IdentityLength, Flags;
            public int Result; public long CreateUsn;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct FindData
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            public uint High, Low, Tag, Reserved;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string Alternate;
        }
        [DllImport("kernel32.dll", EntryPoint = "FindFirstFileW", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr FindFirstFile(string path, out FindData data);
        [DllImport("kernel32.dll")] public static extern bool FindClose(IntPtr handle);
        [DllImport("cldapi.dll")] public static extern uint CfGetPlaceholderStateFromAttributeTag(uint attributes, uint tag);
        [DllImport("ntdll.dll", ExactSpelling = true)] public static extern sbyte RtlSetThreadPlaceholderCompatibilityMode(sbyte mode);
        [DllImport("ntdll.dll", ExactSpelling = true)] public static extern sbyte RtlQueryThreadPlaceholderCompatibilityMode();
        [DllImport("cldapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] public static extern int CfRegisterSyncRoot(string path, ref Registration registration, ref Policies policies, uint flags);
        [DllImport("cldapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] public static extern int CfUnregisterSyncRoot(string path);
        [DllImport("cldapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] public static extern int CfCreatePlaceholders(string path, ref PlaceholderInfo file, uint count, uint flags, out uint processed);
    }
}
