using System.Runtime.InteropServices;
using System.Windows.Interop;
using CommuteCast.Core;

namespace CommuteCast.Desktop;

public interface IUserNotifications : IDisposable
{
    void Show(JobNotification notification, Action clicked);
}

/// <summary>Transient Windows notification-area banners, without authentication or additional app registration.</summary>
internal sealed class WindowsNotifications : IUserNotifications
{
    private const uint CallbackMessage = 0x8001;
    private HwndSource? source;
    private Action? click;
    private bool registered;
    private NotifyIconData data;
    public void Show(JobNotification notification, Action clicked)
    {
        if (source is null)
        {
            source = new(new HwndSourceParameters("CommuteCast notifications") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
            source.AddHook(Hook);
            data = new() { Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = source.Handle, Id = 1, Flags = 1 | 2 | 4,
                Callback = CallbackMessage, Icon = LoadIconW(IntPtr.Zero, new IntPtr(32516)), Tip = "CommuteCast", Info = "", InfoTitle = "" };
        }
        if (!registered)
        {
            data.Flags = 1 | 2 | 4;
            if (!Shell_NotifyIconW(0, ref data)) throw new InvalidOperationException("Windows notification registration is unavailable.");
            registered = true; data.Version = 4;
            if (!Shell_NotifyIconW(4, ref data)) { Dispose(); throw new InvalidOperationException("Windows notification version is unavailable."); }
        }
        click = clicked; data.Flags = 0x10; data.Info = notification.Message; data.InfoTitle = notification.Heading;
        data.InfoFlags = (notification.Kind == JobNotificationKind.Exported ? 1u : 2u) | 0x80; // respect Windows quiet time
        if (!Shell_NotifyIconW(1, ref data)) { registered = false; throw new InvalidOperationException("Windows notification delivery is unavailable."); }
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == CallbackMessage && ((long)lParam & 0xFFFF) is 0x405 or 0x400 or 0x401)
        { handled = true; click?.Invoke(); }
        return IntPtr.Zero;
    }
    public void Dispose()
    {
        if (registered) Shell_NotifyIconW(2, ref data);
        registered = false; click = null; source?.RemoveHook(Hook); source?.Dispose(); source = null;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size; public IntPtr Window; public uint Id; public uint Flags; public uint Callback; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State; public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern IntPtr LoadIconW(IntPtr instance, IntPtr resource);
}
