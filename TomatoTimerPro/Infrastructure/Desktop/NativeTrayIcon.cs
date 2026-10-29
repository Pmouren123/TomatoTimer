using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace TomatoTimerPro.Infrastructure.Desktop;

/// <summary>Shell notification icon with an explicit custom balloon icon (NIIF_USER).</summary>
internal sealed class NativeTrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8001;
    private readonly HwndSource _window;
    private readonly IntPtr _icon;
    private readonly uint _taskbarCreated;
    private string _text = "TomatoTimer · 待开始";
    private bool _registered;
    private bool _disposed;
    public event Action? OpenRequested;
    public event Action? ContextMenuRequested;

    public NativeTrayIcon(IntPtr icon)
    {
        _icon = icon;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        // Hidden top-level window receives Explorer's TaskbarCreated broadcast.
        _window = new HwndSource(new HwndSourceParameters("TomatoTimer.Tray")
        { Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000) });
        _window.AddHook(OnMessage);
        RegisterIcon();
    }

    private void RegisterIcon()
    {
        if (_disposed || _registered) return;
        var data = CreatePayload(_window.Handle, _icon);
        data.Flags = 0x1 | 0x2 | 0x4; // NIF_MESSAGE | NIF_ICON | NIF_TIP
        data.Tip = _text;
        _registered = ShellNotifyIcon(0, ref data); // NIM_ADD
    }

    public void UpdateText(string text)
    {
        text = Limit(text, 63);
        if (_disposed || _registered && _text == text) return;
        _text = text;
        RegisterIcon();
        var data = CreatePayload(_window.Handle, _icon);
        data.Flags = 0x4;
        data.Tip = text;
        Modify(ref data);
    }

    public void Notify(string title, string message)
    {
        if (_disposed) return;
        RegisterIcon();
        var data = CreateBalloonPayload(_window.Handle, _icon, title, message);
        Modify(ref data);
    }

    private void Modify(ref NotifyIconData data)
    {
        if (ShellNotifyIcon(1, ref data)) return; // NIM_MODIFY
        _registered = false;
        RegisterIcon();
        ShellNotifyIcon(1, ref data);
    }

    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed) return IntPtr.Zero;
        if (_taskbarCreated != 0 && (uint)message == _taskbarCreated)
        {
            _registered = false;
            RegisterIcon();
        }
        else if ((uint)message == CallbackMessage)
        {
            handled = true;
            switch (lParam.ToInt32())
            {
                case 0x0203: // WM_LBUTTONDBLCLK
                case 0x0405: // NIN_BALLOONUSERCLICK
                    OpenRequested?.Invoke();
                    break;
                case 0x0205: // WM_RBUTTONUP
                    ContextMenuRequested?.Invoke();
                    break;
            }
        }
        return IntPtr.Zero;
    }

    internal static NotifyIconData CreatePayload(IntPtr window, IntPtr icon) => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = window, Id = 1,
        CallbackMessage = CallbackMessage, Icon = icon, Tip = "", Info = "", InfoTitle = ""
    };

    internal static NotifyIconData CreateBalloonPayload(IntPtr window, IntPtr icon, string title, string message)
    {
        var data = CreatePayload(window, icon);
        data.Flags = 0x10; // NIF_INFO
        data.Info = Limit(message, 255);
        data.InfoTitle = Limit(title, 63);
        data.Timeout = 5000;
        data.InfoFlags = 0x4 | 0x20; // NIIF_USER | NIIF_LARGE_ICON, not the system information glyph
        data.BalloonIcon = icon;
        return data;
    }

    private static string Limit(string text, int length)
    {
        if (text.Length <= length) return text;
        if (char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var data = CreatePayload(_window.Handle, _icon);
        ShellNotifyIcon(2, ref data); // NIM_DELETE
        _window.RemoveHook(OnMessage);
        _window.Dispose();
        OpenRequested = null; ContextMenuRequested = null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid ItemGuid;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
}
