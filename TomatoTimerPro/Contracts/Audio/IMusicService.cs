namespace TomatoTimerPro.Contracts.Audio;

public interface IMusicService : IDisposable
{
    IReadOnlyList<MusicTrack> Tracks { get; }
    MusicTrack? CurrentTrack { get; }
    bool IsPlaying { get; }
    bool IsAlarmPlaying { get; }
    double PositionSeconds { get; }
    double DurationSeconds { get; }
    double MusicVolume { get; set; }
    double AlarmVolume { get; set; }
    bool ResumeMusic { get; set; }
    string StatusMessage { get; }
    event EventHandler? Changed;
    event EventHandler? LibraryChanged;
    Task InitializeAsync();
    Task ImportAsync(IEnumerable<string> paths);
    Task RemoveAsync(Guid id);
    Task RelinkAsync(Guid id, string path);
    void Play(Guid? trackId = null);
    void Pause();
    void Stop();
    void Previous();
    void Next();
    void Seek(double seconds);
    Task PlayAlarmAsync(string? path, bool silent = false);
    void StopAlarm();
}
