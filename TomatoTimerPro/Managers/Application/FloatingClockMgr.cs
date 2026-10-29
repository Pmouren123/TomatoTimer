using System.ComponentModel;
using TomatoTimerPro.Contracts.Audio;
using TomatoTimerPro.Contracts.Threading;
using TomatoTimerPro.Controllers.Components.FloatingClock;
using TomatoTimerPro.Controllers.Pages.Timer;
using TomatoTimerPro.RuntimeStore.Abstractions;
using TomatoTimerPro.RuntimeStore.States.Settings;
using TomatoTimerPro.Views.Components.FloatingClock;

namespace TomatoTimerPro.Managers.Application;

/// <summary>Owns the optional clock window and its lifetime independently from main-window visibility.</summary>
public sealed class FloatingClockMgr : IDisposable
{
    private readonly IRuntimeStore _store;
    private readonly TimerPageController _timer;
    private readonly IUiDispatcher _dispatcher;
    private readonly Action _openMain;
    private readonly Action _preferencesChanged;
    private readonly IMusicService? _music;
    private readonly IDisposable _subscription;
    private FloatingClockWindow? _window;
    private FloatingClockController? _controller;
    private bool _disposed;

    public FloatingClockMgr(IRuntimeStore store, TimerPageController timer, IUiDispatcher dispatcher,
        Action openMain, Action preferencesChanged, IMusicService? music = null)
    {
        _store = store;
        _timer = timer;
        _dispatcher = dispatcher;
        _openMain = openMain;
        _preferencesChanged = preferencesChanged;
        _music = music;
        _subscription = store.Subscribe<AppPreferences>(preferences =>
            dispatcher.Invoke(() => Apply(preferences.FloatingClockEnabled)));
        Apply(store.Get<AppPreferences>().FloatingClockEnabled);
    }

    private void Apply(bool enabled)
    {
        if (_disposed) return;
        if (!enabled) { _window?.Hide(); return; }
        if (_window is null)
        {
            _controller = new FloatingClockController(_timer, _openMain, Disable, _music);
            _window = new FloatingClockWindow { DataContext = _controller };
            _window.Closing += OnClosing;
            _window.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            var workArea = System.Windows.SystemParameters.WorkArea;
            _window.Left = Math.Max(workArea.Left, workArea.Right - _window.Width - 24);
            _window.Top = Math.Max(workArea.Top, workArea.Top + 32);
        }
        if (!_window.IsVisible) _window.Show();
    }

    public void ShowForFocus()
    {
        if (_disposed) return;
        _store.Update<AppPreferences>(preferences => preferences with { FloatingClockEnabled = true });
        _preferencesChanged();
        _dispatcher.Invoke(() => Apply(true));
    }

    public void HideAfterFocus() => Disable();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_disposed) return;
        e.Cancel = true;
        Disable();
    }

    private void Disable()
    {
        if (_disposed) return;
        _store.Update<AppPreferences>(preferences => preferences with { FloatingClockEnabled = false });
        _preferencesChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _subscription.Dispose();
        _dispatcher.Invoke(() =>
        {
            if (_window is not null)
            {
                _window.Closing -= OnClosing;
                _window.Close();
            }
            if ((object?)_controller is IDisposable disposable) disposable.Dispose();
            _window = null;
            _controller = null;
        });
    }
}
