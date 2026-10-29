using System.ComponentModel;
using TomatoTimerPro.Contracts.Audio;
using TomatoTimerPro.Controllers.Pages.Timer;
using TomatoTimerPro.Infrastructure.Binding;
using TomatoTimerPro.Infrastructure.Commands;

namespace TomatoTimerPro.Controllers.Components.FloatingClock;

public sealed class FloatingClockController : ObservableObject, IDisposable
{
    private readonly TimerPageController _timer;
    private readonly IMusicService? _music;
    private bool _isAlarmPlaying;
    private bool _disposed;
    public string RemainingText => _timer.RemainingText;
    public string StatusText => _timer.StatusText;
    public string CurrentTaskTitle => _timer.CurrentTaskTitle;
    public AsyncRelayCommand PauseResumeCommand => _timer.PauseResumeCommand;
    public RelayCommand OpenMainCommand { get; }
    public RelayCommand HideCommand { get; }
    public bool IsAlarmPlaying => _isAlarmPlaying;
    public RelayCommand StopAlarmCommand { get; }
    public FloatingClockController(TimerPageController timer, Action openMain, Action hide, IMusicService? music = null)
    {
        _timer = timer;
        _music = music;
        _isAlarmPlaying = music?.IsAlarmPlaying ?? false;
        OpenMainCommand = new RelayCommand(openMain); HideCommand = new RelayCommand(hide);
        StopAlarmCommand = new RelayCommand(() => _music?.StopAlarm(), () => IsAlarmPlaying);
        _timer.PropertyChanged += OnTimerChanged;
        if (_music is not null) _music.Changed += OnMusicChanged;
    }
    private void OnTimerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RemainingText) or nameof(StatusText) or nameof(CurrentTaskTitle)) OnPropertyChanged(e.PropertyName);
    }
    private void OnMusicChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        var playing = _music?.IsAlarmPlaying ?? false;
        if (_isAlarmPlaying == playing) return;
        _isAlarmPlaying = playing;
        OnPropertyChanged(nameof(IsAlarmPlaying));
        StopAlarmCommand.NotifyCanExecuteChanged();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.PropertyChanged -= OnTimerChanged;
        if (_music is not null) _music.Changed -= OnMusicChanged;
    }
}
