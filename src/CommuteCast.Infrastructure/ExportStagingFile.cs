using CommuteCast.Core;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CommuteCast.Infrastructure;

/// <summary>Exclusive Windows staging handle held through identity/content verification and rename or removal.</summary>
public sealed class ExportStagingFile : IAsyncDisposable
{
    private readonly string root;
    private string name;
    public FileStream Stream { get; }
    public ExportStagingIdentity Identity { get; }

    private ExportStagingFile(string root, string name, FileStream stream, ExportStagingIdentity identity)
    { this.root = root; this.name = name; Stream = stream; Identity = identity; }
    internal bool IsAt(string directory, string relativeName) =>
        string.Equals(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) && name.Equals(relativeName, StringComparison.OrdinalIgnoreCase);

    public static ExportStagingFile Create(string root, string name) => Open(root, name, true)!;
    public static ExportStagingFile? OpenIfPresent(string root, string name) => Open(root, name, false);

    private static ExportStagingFile? Open(string root, string name, bool create)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Export staging requires Windows.");
        var path = OwnedFileRemoval.Resolve(root, name);
        if (path.StartsWith("\\\\", StringComparison.Ordinal)) throw new IOException("Export staging requires a local folder.");
        var handle = CreateFile("\\\\?\\" + path, 0xC0010000, 0, IntPtr.Zero, create ? 1u : 3u, 0x80200080, IntPtr.Zero); // READ|WRITE|DELETE; exclusive; write-through; open reparse point itself.
        try
        {
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                if (!create && error is 2 or 3) return null;
                throw new IOException("Export staging is occupied, locked or inaccessible. Existing files were preserved; close the application using it or choose another output folder.", new Win32Exception(error));
            }
            if (!GetBasicInfo(handle, 0, out var basic, (uint)Marshal.SizeOf<BasicInfo>()) || (basic.Attributes & (0x400 | 0x10)) != 0)
                throw new IOException("Export staging is a directory or reparse point. It was preserved.");
            if (!GetIdInfo(handle, 18, out var id, (uint)Marshal.SizeOf<IdInfo>()))
                throw new IOException("Windows could not identify the export staging file. Inspection is required.", new Win32Exception(Marshal.GetLastWin32Error()));
            var identity = new ExportStagingIdentity(1, id.Volume, id.Low.ToString("X16") + id.High.ToString("X16"), basic.CreationTime);
            var stream = new FileStream(handle, FileAccess.ReadWrite, 81920, false);
            return new(root, name, stream, identity);
        }
        catch
        {
            if (create && !handle.IsInvalid)
            { var disposition = new Disposition { DeleteFile = 1 }; SetDisposition(handle, 4, ref disposition, 1); }
            handle.Dispose(); throw;
        }
        finally { if (handle.IsInvalid) handle.Dispose(); }
    }

    public bool Matches(ExportStagingIdentity expected) => expected.FormatVersion == 1 && expected.FileId?.Length == 32 &&
        expected.CreationFileTime > 0 && expected == Identity;

    public async Task<string> HashAsync(CancellationToken ct)
    { Stream.Position = 0; return Convert.ToHexString(await SHA256.HashDataAsync(Stream, ct)); }

    public async Task<bool> IsSourcePrefixAsync(Stream source, CancellationToken ct)
    {
        if (Stream.Length > source.Length) return false;
        Stream.Position = source.Position = 0;
        var actual = new byte[81920]; var expected = new byte[81920]; long remaining = Stream.Length;
        while (remaining > 0)
        {
            var count = (int)Math.Min(remaining, actual.Length);
            await Stream.ReadExactlyAsync(actual.AsMemory(0, count), ct); await source.ReadExactlyAsync(expected.AsMemory(0, count), ct);
            if (!actual.AsSpan(0, count).SequenceEqual(expected.AsSpan(0, count))) return false;
            remaining -= count;
        }
        return true;
    }

    public void Rename(string name)
    {
        var target = OwnedFileRemoval.Resolve(root, name);
        if (!string.Equals(Path.GetDirectoryName(target), Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Export publication must stay in its reviewed output folder.");
        var filename = Encoding.Unicode.GetBytes("\\\\?\\" + target);
        var offset = (int)Marshal.OffsetOf<RenameInfo>(nameof(RenameInfo.FileName));
        var size = checked(offset + filename.Length + 2); var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            Marshal.WriteInt32(buffer, 0, 0); // ReplaceIfExists=false; never overwrite.
            Marshal.WriteIntPtr(buffer, (int)Marshal.OffsetOf<RenameInfo>(nameof(RenameInfo.Root)), IntPtr.Zero);
            Marshal.WriteInt32(buffer, (int)Marshal.OffsetOf<RenameInfo>(nameof(RenameInfo.Length)), filename.Length);
            Marshal.Copy(filename, 0, buffer + offset, filename.Length);
            if (!SetInfo(Stream.SafeFileHandle, 3, buffer, (uint)size))
                throw new IOException("Windows could not publish the verified export without overwrite. The completed private MP3 is retained; repair locks or the destination and retry.", new Win32Exception(Marshal.GetLastWin32Error()));
            this.name = name;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Delete()
    {
        var disposition = new Disposition { DeleteFile = 1 };
        if (!SetDisposition(Stream.SafeFileHandle, 4, ref disposition, 1))
            throw new IOException("Windows could not remove the verified staging file. It is retained for retry.", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    public ValueTask DisposeAsync() => Stream.DisposeAsync();

    [StructLayout(LayoutKind.Sequential)] private struct BasicInfo { public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct IdInfo { public ulong Volume, Low, High; }
    [StructLayout(LayoutKind.Sequential)] private struct RenameInfo { public uint Flags; public IntPtr Root; public uint Length; public char FileName; }
    [StructLayout(LayoutKind.Sequential)] private struct Disposition { public byte DeleteFile; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetBasicInfo(SafeFileHandle handle, int kind, out BasicInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIdInfo(SafeFileHandle handle, int kind, out IdInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInfo(SafeFileHandle handle, int kind, IntPtr info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDisposition(SafeFileHandle handle, int kind, ref Disposition info, uint size);
}
