using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CommuteCast.Web;

/// <summary>Per-user Windows Credential Manager; neither configuration nor journals contain secrets.</summary>
public static class WindowsWebSecrets
{
    public static string Target(CommuteCast.Infrastructure.M365Destination d) => $"CommuteCast/Web/M365/{d.TenantId}/{d.ClientId}";
    public static string? Get(string target)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!CredRead(target, 1, 0, out var pointer))
        {
            if (Marshal.GetLastWin32Error() == 1168) return null;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows credential storage could not be read.");
        }
        try { var value = Marshal.PtrToStructure<Credential>(pointer); return Marshal.PtrToStringUni(value.Blob, checked((int)value.BlobSize / 2)); }
        finally { CredFree(pointer); }
    }
    public static void Set(string target, string value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The web host currently requires Windows credential storage.");
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2500 || value.Any(char.IsControl)) throw new ArgumentException("Enter a valid application secret.");
        var pointer = Marshal.StringToHGlobalUni(value);
        try
        {
            var credential = new Credential { Type = 1, TargetName = target, BlobSize = checked((uint)value.Length * 2), Blob = pointer, Persist = 2, UserName = "CommuteCast" };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows credential storage could not save the secret.");
        }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(pointer); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? Alias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr pointer);
}
