using System.ComponentModel;
using System.Runtime.InteropServices;
using CommuteCast.Core;

namespace CommuteCast.Infrastructure;

public interface ISpeechSecrets
{
    string? Get(string provider);
    void Set(string provider, string key);
    void Remove(string provider);
}

/// <summary>Per-user Windows credentials; API keys never enter settings, draft or backup files.</summary>
public sealed class WindowsSpeechSecrets(string targetPrefix = "CommuteCast/TTS/") : ISpeechSecrets
{
    private string Target(string provider)
    {
        if (!SpeechProviders.IsHosted(provider)) throw new ArgumentException("This provider does not use an API key.");
        return targetPrefix + provider;
    }
    public string? Get(string provider)
    {
        if (!CredRead(Target(provider), 1, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error(); if (error == 1168) return null; throw new Win32Exception(error, "Windows credential storage could not be read.");
        }
        try { var credential = Marshal.PtrToStructure<Credential>(pointer); return Marshal.PtrToStringUni(credential.Blob, checked((int)credential.BlobSize / 2)); }
        finally { CredFree(pointer); }
    }
    public void Set(string provider, string key)
    {
        key = key.Trim(); if (key.Length is < 8 or > 2000 || key.Any(char.IsControl)) throw new ArgumentException("Enter a valid provider API key.");
        var pointer = Marshal.StringToHGlobalUni(key);
        try
        {
            var credential = new Credential { Type = 1, TargetName = Target(provider), BlobSize = checked((uint)key.Length * 2), Blob = pointer, Persist = 2, UserName = "CommuteCast" };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows credential storage could not save the key.");
        }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(pointer); }
    }
    public void Remove(string provider)
    {
        if (!CredDelete(Target(provider), 1, 0) && Marshal.GetLastWin32Error() != 1168) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows credential storage could not remove the key.");
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
        public string? TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr pointer);
}
