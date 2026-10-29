using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TomatoTimerPro.Infrastructure.Desktop;

public sealed class TrayIconService : IDisposable
{
    private readonly NativeTrayIcon _icon;
    private readonly MenuItem _pause;
    private readonly MenuItem _stopAlarm;
    private readonly ContextMenu _menu;
    private readonly Icon _ownedIcon;
    private bool _disposed;
    public event Action? OpenRequested;
    public event Action? PauseRequested;
    public event Action? StopAlarmRequested;
    public event Action? ExitRequested;

    public TrayIconService()
    {
        _menu = new ContextMenu { Placement = PlacementMode.MousePoint, StaysOpen = false };
        var open = new MenuItem { Header = "打开主界面" };
        open.Click += (_, _) => OpenRequested?.Invoke();
        _menu.Items.Add(open);
        _pause = new MenuItem { Header = "暂停 / 继续", IsEnabled = false };
        _pause.Click += (_, _) => PauseRequested?.Invoke();
        _stopAlarm = new MenuItem { Header = "停止铃声", IsEnabled = false };
        _stopAlarm.Click += (_, _) => StopAlarmRequested?.Invoke();
        _menu.Items.Add(_pause);
        _menu.Items.Add(_stopAlarm);
        _menu.Items.Add(new Separator());
        var exit = new MenuItem { Header = "退出" };
        exit.Click += (_, _) => ExitRequested?.Invoke();
        _menu.Items.Add(exit);
        _ownedIcon = CreateIcon();
        _icon = new NativeTrayIcon(_ownedIcon.Handle);
        _icon.ContextMenuRequested += OnTrayContextMenu;
        _icon.OpenRequested += OnOpen;
    }

    private static Icon CreateIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/TomatoTimer;component/Resources/Images/AppIcon.ico", UriKind.Absolute))
            ?? throw new FileNotFoundException("应用图标资源缺失。");
        using var stream = resource.Stream;
        using var icon = new Icon(stream, new System.Drawing.Size(32, 32));
        return (Icon)icon.Clone();
    }

    private void OnOpen() => OpenRequested?.Invoke();
    private void OnTrayContextMenu()
    {
        if (_disposed) return;
        _menu.IsOpen = true;
        _menu.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_disposed || !_menu.IsOpen) return;
            // Foreground only the popup so outside clicks dismiss it without opening or activating the main window.
            if (PresentationSource.FromVisual(_menu) is HwndSource popup) SetForegroundWindow(popup.Handle);
            _menu.Focus();
            Keyboard.Focus(_menu);
        }));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr handle);
    public void Update(string status, bool active, bool paused, bool alarm)
    {
        if (_disposed) return;
        var text = "TomatoTimer · " + status;
        text = text.Length > 63 ? text[..63] : text;
        _icon.UpdateText(text);
        _pause.IsEnabled = active;
        _pause.Header = paused ? "继续专注" : "暂停专注";
        _stopAlarm.IsEnabled = alarm;
    }
    public void Notify(string title, string message)
    {
        if (!_disposed) _icon.Notify(title, message);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.ContextMenuRequested -= OnTrayContextMenu;
        _icon.OpenRequested -= OnOpen;
        _icon.Dispose();
        _menu.IsOpen = false;
        _menu.Items.Clear();
        _ownedIcon.Dispose();
    }
}
