using System.Collections.ObjectModel;
using TomatoTimerPro.Contracts.Audio;
using TomatoTimerPro.Contracts.Interaction;
using TomatoTimerPro.Infrastructure.Binding;
using TomatoTimerPro.Infrastructure.Commands;

namespace TomatoTimerPro.Controllers.Pages.Music;

/// <summary>Reusable music interactions; contains no references to its host page.</summary>
public sealed class MusicPageController : ObservableObject, IDisposable
{
    private readonly IMusicService _music;
    private readonly IUserInteraction _interaction;
    private MusicTrack? _selectedTrack;
    private string? _operationStatus;
    private bool _disposed;

    public MusicPageController(IMusicService music, IUserInteraction interaction)
    {
        _music = music;
        _interaction = interaction;
        ImportCommand = new AsyncRelayCommand(ImportAsync, OnError);
        PlayPauseCommand = new RelayCommand(PlayPause);
        StopCommand = new RelayCommand(_music.Stop);
        PreviousCommand = new RelayCommand(_music.Previous);
        NextCommand = new RelayCommand(_music.Next);
        RemoveCommand = new AsyncRelayCommand(RemoveAsync, OnError, () => SelectedTrack is not null);
        RelinkCommand = new AsyncRelayCommand(RelinkAsync, OnError, () => SelectedTrack is not null);
        PreviewAlarmCommand = new AsyncRelayCommand(PreviewAlarmAsync, OnError);
        StopAlarmCommand = new RelayCommand(_music.StopAlarm);
        _music.Changed += OnChanged;
        _music.LibraryChanged += OnLibraryChanged;
        SynchronizeTracks();
    }

    public ObservableCollection<MusicTrack> Tracks { get; } = [];
    public MusicTrack? SelectedTrack
    {
        get => _selectedTrack;
        set
        {
            if (!SetProperty(ref _selectedTrack, value)) return;
            RemoveCommand.NotifyCanExecuteChanged();
            RelinkCommand.NotifyCanExecuteChanged();
        }
    }
    public bool IsPlaying => _music.IsPlaying;
    public bool IsAlarmPlaying => _music.IsAlarmPlaying;
    public string PlaybackStatus => _music.IsAlarmPlaying ? "提醒中" : _music.IsPlaying ? "正在播放" : "已暂停 / 待播放";
    public string CurrentTrackTitle => _music.CurrentTrack?.Title ?? "还没有正在播放的音乐";
    public string PositionText => FormatTime(_music.PositionSeconds);
    public string DurationText => FormatTime(_music.DurationSeconds);
    public string StatusMessage => _operationStatus ?? _music.StatusMessage;
    public double PositionSeconds { get => _music.PositionSeconds; set => _music.Seek(value); }
    public double DurationSeconds => _music.DurationSeconds;
    public double Volume { get => _music.MusicVolume; set { _music.MusicVolume = value; OnPropertyChanged(); } }
    public double AlarmVolume { get => _music.AlarmVolume; set { _music.AlarmVolume = value; OnPropertyChanged(); } }
    public bool ResumeMusic { get => _music.ResumeMusic; set { _music.ResumeMusic = value; OnPropertyChanged(); } }
    public AsyncRelayCommand ImportCommand { get; }
    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand PreviousCommand { get; }
    public RelayCommand NextCommand { get; }
    public AsyncRelayCommand RemoveCommand { get; }
    public AsyncRelayCommand RelinkCommand { get; }
    public AsyncRelayCommand PreviewAlarmCommand { get; }
    public RelayCommand StopAlarmCommand { get; }

    public async Task InitializeAsync()
    {
        await _music.InitializeAsync();
        if (!_disposed) SynchronizeTracks();
    }

    private async Task ImportAsync()
    {
        var paths = await _interaction.PickAudioFilesAsync();
        if (paths.Length == 0 || _disposed) return;
        _operationStatus = null;
        await _music.ImportAsync(paths);
    }

    private void PlayPause()
    {
        _operationStatus = null;
        if (_music.IsPlaying && (SelectedTrack is null || SelectedTrack.Id == _music.CurrentTrack?.Id)) _music.Pause();
        else _music.Play(SelectedTrack?.Id);
    }

    private async Task RemoveAsync()
    {
        var track = SelectedTrack;
        if (track is null) return;
        _operationStatus = null;
        await _music.RemoveAsync(track.Id);
    }

    private async Task RelinkAsync()
    {
        var track = SelectedTrack;
        if (track is null) return;
        var files = await _interaction.PickAudioFilesAsync();
        if (files.Length == 0 || _disposed) return;
        _operationStatus = null;
        await _music.RelinkAsync(track.Id, files[0]);
    }

    private Task PreviewAlarmAsync()
    {
        _operationStatus = null;
        return _music.PlayAlarmAsync(SelectedTrack?.FilePath);
    }

    private void OnLibraryChanged(object? sender, EventArgs e) => SynchronizeTracks();

    private void SynchronizeTracks()
    {
        if (_disposed) return;
        var selectedId = SelectedTrack?.Id;
        Tracks.Clear();
        foreach (var track in _music.Tracks) Tracks.Add(track);
        SelectedTrack = Tracks.FirstOrDefault(t => t.Id == selectedId) ?? Tracks.FirstOrDefault();
        OnChanged(this, EventArgs.Empty);
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        foreach (var property in new[] { nameof(IsPlaying), nameof(IsAlarmPlaying), nameof(PlaybackStatus), nameof(CurrentTrackTitle),
                     nameof(DurationSeconds), nameof(PositionSeconds), nameof(PositionText), nameof(DurationText), nameof(StatusMessage),
                     nameof(Volume), nameof(AlarmVolume), nameof(ResumeMusic) }) OnPropertyChanged(property);
    }

    private void OnError(Exception exception)
    {
        _operationStatus = $"操作未完成：{exception.Message}";
        OnPropertyChanged(nameof(StatusMessage));
        _interaction.ShowMessage("音乐操作未完成", exception.Message);
    }

    private static string FormatTime(double seconds)
    {
        var duration = TimeSpan.FromSeconds(double.IsFinite(seconds) ? Math.Max(0, seconds) : 0);
        return duration.TotalHours >= 1 ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}" : $"{(int)duration.TotalMinutes:00}:{duration.Seconds:00}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _music.Changed -= OnChanged;
        _music.LibraryChanged -= OnLibraryChanged;
    }
}
