using System.ComponentModel;
using System.Windows;
using TomatoTimerPro.Bootstrap;
using TomatoTimerPro.Contracts.Interaction;
using TomatoTimerPro.Infrastructure.Desktop;
using TomatoTimerPro.Infrastructure.Threading;
using TomatoTimerPro.RuntimeStore.States.Settings;
using TomatoTimerPro.RuntimeStore.States.Timer;
using TomatoTimerPro.Tools.Time;
using TomatoTimerPro.Views.Shell;

namespace TomatoTimerPro.Managers.Application;

public sealed class ApplicationMgr : IAsyncDisposable
{
    private readonly System.Windows.Application _app;
    private readonly MainWindow _window;
    private readonly ApplicationComposition _composition;
    private readonly IUserInteraction _interaction;
    private TrayIconService? _tray;
    private FloatingClockMgr? _floatingClock;
    private WindowBackgroundService? _background;
    private IDisposable? _appearanceSubscription;
    private bool _exiting;
    private bool _allowClose;
    private bool _disposed;
    private bool _wasFocusActive;
    public ApplicationMgr(System.Windows.Application app, MainWindow window, ApplicationComposition composition, IUserInteraction interaction)
    { _app = app; _window = window; _composition = composition; _interaction = interaction; }

    public async Task StartAsync()
    {
        _window.DataContext = _composition.Shell;
        _app.MainWindow = _window;
        _window.Closing += OnWindowClosing;
        _tray = new TrayIconService();
        _tray.OpenRequested += ShowWindow;
        _tray.PauseRequested += PauseRequested;
        _tray.StopAlarmRequested += StopAlarmRequested;
        _tray.ExitRequested += ExitRequested;
        _composition.Countdown.Changed += RefreshTray;
        _composition.Countdown.Changed += OnFocusStateChanged;
        _composition.Countdown.Completed += OnCompleted;
        _composition.Music.Changed += OnMusicChanged;
        _composition.Timer.FocusStarted += OnFocusStarted;
        _background = new WindowBackgroundService(_window.BackgroundSurface);
        _appearanceSubscription = _composition.Store.Subscribe<AppPreferences>(preferences =>
            _window.Dispatcher.Invoke(() => ApplyBackground(preferences)));
        ApplyBackground(_composition.Store.Get<AppPreferences>());
        _window.Show();
        _floatingClock = new FloatingClockMgr(_composition.Store, _composition.Timer,
            new WpfUiDispatcher(_window.Dispatcher), ShowWindow, _composition.QueueSave, _composition.Music);
        await _composition.InitializeAsync();
        ApplyBackground(_composition.Store.Get<AppPreferences>());
        RefreshTray();
    }

    private void ApplyBackground(AppPreferences preferences)
    {
        if (_disposed || _background is null) return;
        try { _background.Apply(preferences.BackgroundColor, preferences.BackgroundImagePath, preferences.BackgroundOpacity); }
        catch (Exception error)
        {
            _composition.Shell.StatusMessage = "背景暂时无法加载，已使用默认背景：" + error.Message;
            _background.Apply("#FAF5E9", null, .18);
        }
    }

    public void ShowWindow()
    {
        if (_disposed || _allowClose) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        var preferences = _composition.Store.Get<AppPreferences>();
        if (preferences.CloseToTray)
        {
            _window.Hide();
            if (!preferences.HasSeenTrayHint)
            {
                _tray?.Notify("已隐藏到系统托盘", "计时和音乐继续运行。双击托盘图标恢复窗口，右键菜单可退出软件。");
                _composition.Store.Update<AppPreferences>(p => p with { HasSeenTrayHint = true });
                _composition.QueueSave();
            }
        }
        else ExitRequested();
    }

    private async void PauseRequested()
    {
        try { await _composition.Countdown.PauseResumeAsync(); }
        catch (Exception error) { _interaction.ShowMessage("计时操作失败", error.Message); }
    }
    private void StopAlarmRequested() => _composition.Music.StopAlarm();
    private void OnMusicChanged(object? sender, EventArgs e) => RefreshTray();
    private void OnFocusStarted()
    {
        _floatingClock?.ShowForFocus();
        _window.Hide();
    }
    private void OnFocusStateChanged()
    {
        var active = _composition.Countdown.IsActive;
        var finished = _wasFocusActive && !active;
        _wasFocusActive = active;
        if (!finished || _exiting || _disposed) return;
        _floatingClock?.HideAfterFocus();
        ShowWindow();
    }
    private void RefreshTray()
    {
        var countdown = _composition.Countdown;
        var state = countdown.Current?.Status switch
        {
            SessionStatus.Running => "专注中 · " + DurationFormatter.Format(countdown.Remaining),
            SessionStatus.Paused => "已暂停 · " + DurationFormatter.Format(countdown.Remaining),
            SessionStatus.Completed => "专注已完成",
            SessionStatus.EndedEarly => "已提前结束",
            SessionStatus.Cancelled => "已取消",
            SessionStatus.Interrupted => "上次专注已中断",
            _ => "待开始"
        };
        _tray?.Update(state, countdown.IsActive, countdown.IsPaused, _composition.Music.IsAlarmPlaying);
    }

    private async void OnCompleted(TimerSession session)
    {
        try
        {
            _tray?.Notify("专注完成", $"“{session.Title}”已完成。");
            await _composition.Music.PlayAlarmAsync(session.AlarmPath, session.AlarmPath == "__silent__");
            RefreshTray();
        }
        catch (Exception error) { _composition.Shell.StatusMessage = "专注已完成，铃声播放失败：" + error.Message; }
    }

    private async void ExitRequested() => await RequestExitAsync();
    public async Task<bool> RequestExitAsync()
    {
        if (_exiting || _disposed) return false;
        _exiting = true;
        try
        {
            if (!await _composition.Pages.CanCloseAsync()) return false;
            if (_composition.Records is { IsDirty: true } records && !await records.CanLeaveAsync()) return false;
            if (_composition.Countdown.IsActive && !await _interaction.ConfirmAsync("退出软件", "当前番茄钟尚未结束。退出将把本次专注记为已中断，是否继续？")) return false;
            await _composition.Countdown.InterruptAsync();
            if (_composition.Countdown.LastStorageError is { } storageError && storageError.Contains("也写入失败", StringComparison.Ordinal))
            {
                try
                {
                    // Retry explicitly: InterruptAsync is idempotent and an already interrupted session won't save again.
                    if (_composition.Countdown.Current is { } pending)
                        await _composition.Storage.SaveSessionAsync(pending);
                    await _composition.Storage.SaveActiveSessionAsync(null);
                }
                catch (Exception retryError)
                {
                    ShowWindow();
                    _interaction.ShowMessage("计时记录尚未保存", storageError + "\n请恢复磁盘可写状态后重试退出，以免丢失记录。\n" + retryError.Message);
                    return false;
                }
            }
            await _composition.FlushAsync();
            _allowClose = true;
            // Once persistence succeeded, cleanup failure cannot restore a partially disposed application.
            try { await DisposeAsync(); }
            catch (Exception cleanupError) { System.Diagnostics.Debug.WriteLine(cleanupError); }
            _window.Close();
            _app.Shutdown();
            return true;
        }
        catch (Exception error)
        {
            _allowClose = false;
            ShowWindow();
            _interaction.ShowMessage("退出未完成", "保存或清理失败，软件保持打开：" + error.Message);
            return false;
        }
        finally { _exiting = false; }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _window.Closing -= OnWindowClosing;
        _composition.Countdown.Changed -= RefreshTray;
        _composition.Countdown.Changed -= OnFocusStateChanged;
        _composition.Countdown.Completed -= OnCompleted;
        _composition.Music.Changed -= OnMusicChanged;
        _composition.Timer.FocusStarted -= OnFocusStarted;
        _tray?.Dispose();
        _floatingClock?.Dispose();
        _appearanceSubscription?.Dispose();
        await _composition.DisposeAsync();
    }
}
