using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace CommuteCast.Infrastructure;

public interface IFileRemovalObserver { Task ValidatedAsync(string path, CancellationToken ct); }

/// <summary>Windows deletion through the same handle that validated the recorded bytes. Never sweeps a directory.</summary>
public static class OwnedFileRemoval
{
    public static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Contains(':') ||
            relative.Split('/').Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                System.Text.RegularExpressions.Regex.IsMatch(p.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)))
            throw new IOException("A managed removal path is unsafe. Files were preserved.");
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!Workspace.IsWithin(root, path)) throw new IOException("A managed removal path escapes its recorded root. Files were preserved.");
        SqliteSchema.RejectLink(path); return path;
    }
    public static async Task<bool> DeleteAsync(string root, ReleaseFile receipt, IFileRemovalObserver? observer = null, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Managed installation removal requires Windows.");
        ct.ThrowIfCancellationRequested(); var path = Resolve(root, receipt.RelativePath);
        if (path.StartsWith("\\\\", StringComparison.Ordinal)) throw new IOException("Managed installation removal requires a local file.");
        using var handle = CreateFile("\\\\?\\" + path, 0x80010000, 1, IntPtr.Zero, 3, 0x00200080, IntPtr.Zero); // READ|DELETE; share READ; open the reparse point itself, including long paths.
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error(); if (error is 2 or 3) return false;
            throw new IOException("A managed file is locked or inaccessible. Removal is incomplete; retry after closing the application using it.", new Win32Exception(error));
        }
        if (!GetFileInformationByHandleEx(handle, 9, out var attributes, 8) || (attributes.Attributes & (0x400 | 0x10)) != 0)
            throw new IOException("A managed removal item is a directory or reparse point. Files were preserved.");
        await using var input = new FileStream(handle, FileAccess.Read, 81920, false);
        if (input.Length != receipt.Bytes || Convert.ToHexString(await SHA256.HashDataAsync(input, ct)) != receipt.Sha256)
            throw new IOException("A recorded file changed. It was preserved rather than removed.");
        if (observer is not null) await observer.ValidatedAsync(path, ct);
        ct.ThrowIfCancellationRequested();
        var disposition = new Disposition { DeleteFile = 1 };
        if (!SetFileInformationByHandle(handle, 4, ref disposition, 1))
            throw new IOException("Windows could not remove a verified file. Removal is incomplete; retry after repairing permissions or locks.", new Win32Exception(Marshal.GetLastWin32Error()));
        return true;
    }
    public static void RemoveEmptyDirectories(string root)
    {
        SqliteSchema.RejectLink(root); if (!Directory.Exists(root)) return;
        var pending = new Stack<string>(); var directories = new List<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop(); Workspace.RejectFileReparsePoint(current); directories.Add(current);
            if (directories.Count > 200000) throw new IOException("There are too many managed directories to inspect safely.");
            foreach (var child in Directory.EnumerateDirectories(current)) { Workspace.RejectFileReparsePoint(child); pending.Push(child); }
        }
        foreach (var directory in directories.AsEnumerable().Reverse())
        {
            Workspace.RejectFileReparsePoint(directory);
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory, false);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { public uint Attributes; public uint ReparseTag; }
    [StructLayout(LayoutKind.Sequential)] private struct Disposition { public byte DeleteFile; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out AttributeTag info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref Disposition info, uint size);
}
