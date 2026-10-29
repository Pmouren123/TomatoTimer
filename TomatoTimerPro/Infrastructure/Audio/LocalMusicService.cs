using System.Diagnostics;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Threading;
using TomatoTimerPro.Contracts.Audio;
using TomatoTimerPro.Contracts.Storage;
using TomatoTimerPro.Contracts.Threading;

namespace TomatoTimerPro.Infrastructure.Audio;

/// <summary>
/// Local playback adapter. Created and called on the UI thread; decoding is deferred until playback.
/// The library stores references only. Background audio and alarms have separate players and volumes.
/// </summary>
public sealed class LocalMusicService : IMusicService, IAppDataLocation
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".wma", ".m4a", ".aac", ".aif", ".aiff" };
    private string _directory;
    private string _indexPath;
    private readonly IUiDispatcher _dispatcher;
    private readonly List<MusicTrack> _tracks = [];
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private Task? _initialization;
    private MediaPlayer? _music;
    private MediaPlayer? _alarm;
    private DispatcherTimer? _pulse;
    private MusicTrack? _current;
    private bool _isPlaying;
    private bool _isAlarmPlaying;
    private bool _resumeMusic = true;
    private double _musicVolume = .5;
    private double _alarmVolume = .8;
    private double _duration;
    private string _status = "";
    private long _userRevision;
    private long _resumeRevision;
    private long _alarmStarted;
    private long _alarmGeneration;
    private bool _resumeAfterAlarm;
    private bool _alarmIsFallback;
    private bool _disposed;

    public LocalMusicService(string appDataDirectory, IUiDispatcher dispatcher)
    {
        _directory = appDataDirectory;
        _indexPath = Path.Combine(appDataDirectory, "music-library.json");
        _dispatcher = dispatcher;
    }

    public IReadOnlyList<MusicTrack> Tracks => _tracks.AsReadOnly();
    public MusicTrack? CurrentTrack => _current;
    public bool IsPlaying => _isPlaying;
    public bool IsAlarmPlaying => _isAlarmPlaying;
    public double PositionSeconds => _music is null ? 0 : Math.Max(0, _music.Position.TotalSeconds);
    public double DurationSeconds => _duration;
    public string StatusMessage => _status;
    public double MusicVolume
    {
        get => _musicVolume;
        set { _musicVolume = NormalizeVolume(value); if (_music is not null) _music.Volume = _musicVolume; Notify(); }
    }
    public double AlarmVolume
    {
        get => _alarmVolume;
        set { _alarmVolume = NormalizeVolume(value); if (_alarm is not null) _alarm.Volume = _alarmVolume; Notify(); }
    }
    public bool ResumeMusic
    {
        get => _resumeMusic;
        set { _resumeMusic = value; Notify(); }
    }

    public event EventHandler? Changed;
    public event EventHandler? LibraryChanged;

    public Task InitializeAsync() => _initialization ??= ReadIndexAsync();

    public async Task ChangeDataDirectoryAsync(string directory)
    {
        ThrowIfDisposed();
        directory = Path.GetFullPath(directory);
        await InitializeAsync();
        await _indexGate.WaitAsync();
        try
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(_directory, directory)) return;
            Stop();
            _tracks.Clear();
            _current = null;
            _directory = directory;
            _indexPath = Path.Combine(directory, "music-library.json");
            _initialization = ReadIndexAsync();
            await _initialization;
        }
        finally { _indexGate.Release(); }
    }

    private async Task ReadIndexAsync()
    {
        if (!File.Exists(_indexPath)) return;
        try
        {
            var text = await File.ReadAllTextAsync(_indexPath);
            var loaded = JsonSerializer.Deserialize<MusicTrack[]>(text) ?? [];
            if (_disposed) return;
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var track in loaded)
            {
                if (string.IsNullOrWhiteSpace(track.FilePath)) continue;
                try
                {
                    var path = Path.GetFullPath(track.FilePath);
                    if (unique.Add(path)) _tracks.Add(track with { Id = track.Id == Guid.Empty ? Guid.NewGuid() : track.Id, FilePath = path, Issue = null });
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
            }
            _status = "";
            NotifyLibrary();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _status = $"音乐索引读取失败，原文件未改动：{ex.Message}";
            Notify();
        }
    }

    public async Task ImportAsync(IEnumerable<string> paths)
    {
        ThrowIfDisposed();
        await InitializeAsync();
        ThrowIfDisposed();
        await _indexGate.WaitAsync();
        var original = _tracks.ToArray();
        try
        {
            var added = 0;
            foreach (var raw in paths)
            {
                string path;
                try { path = Path.GetFullPath(raw); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
                if (_tracks.Any(t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase))) continue;
                var issue = !Extensions.Contains(Path.GetExtension(path)) ? "不支持此音频格式" : !File.Exists(path) ? "文件不存在，请重新定位" : null;
                _tracks.Add(new MusicTrack(Guid.NewGuid(), path, Path.GetFileNameWithoutExtension(path), issue));
                added++;
            }
            await SaveIndexAsync();
            _status = added == 0 ? "没有新增曲目" : $"已导入 {added} 首";
            NotifyLibrary();
        }
        catch
        {
            _tracks.Clear();
            _tracks.AddRange(original);
            NotifyLibrary();
            throw;
        }
        finally { _indexGate.Release(); }
    }

    public async Task RemoveAsync(Guid id)
    {
        ThrowIfDisposed();
        await InitializeAsync();
        ThrowIfDisposed();
        await _indexGate.WaitAsync();
        var original = _tracks.ToArray();
        try
        {
            if (_current?.Id == id) { Stop(); _current = null; }
            _tracks.RemoveAll(t => t.Id == id);
            await SaveIndexAsync();
            _status = "已移除";
            NotifyLibrary();
        }
        catch
        {
            _tracks.Clear();
            _tracks.AddRange(original);
            NotifyLibrary();
            throw;
        }
        finally { _indexGate.Release(); }
    }

    public async Task RelinkAsync(Guid id, string path)
    {
        ThrowIfDisposed();
        await InitializeAsync();
        ThrowIfDisposed();
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("所选音频不存在。", path);
        if (!Extensions.Contains(Path.GetExtension(path))) throw new NotSupportedException("请选择 MP3、WAV 或系统支持的音频格式。");
        await _indexGate.WaitAsync();
        var original = _tracks.ToArray();
        try
        {
            var index = _tracks.FindIndex(t => t.Id == id);
            if (index < 0) return;
            if (_tracks.Any(t => t.Id != id && string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("这个文件已在音乐列表中，请直接选择已有曲目。");
            if (_current?.Id == id) Stop();
            var updated = _tracks[index] with { FilePath = path, Title = Path.GetFileNameWithoutExtension(path), Issue = null };
            _tracks[index] = updated;
            if (_current?.Id == id) _current = updated;
            await SaveIndexAsync();
            _status = "已更新位置";
            NotifyLibrary();
        }
        catch
        {
            _tracks.Clear();
            _tracks.AddRange(original);
            _current = original.FirstOrDefault(t => t.Id == _current?.Id);
            NotifyLibrary();
            throw;
        }
        finally { _indexGate.Release(); }
    }

    private async Task SaveIndexAsync()
    {
        Directory.CreateDirectory(_directory);
        var temporary = _indexPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(_tracks, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _indexPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Play(Guid? trackId = null)
    {
        ThrowIfDisposed();
        _userRevision++;
        StopAlarmCore(false);
        var track = trackId is { } id ? _tracks.FirstOrDefault(t => t.Id == id) : _current ?? _tracks.FirstOrDefault();
        if (track is null) { _status = "请先导入并选择一首音乐。"; Notify(); return; }
        if (!File.Exists(track.FilePath)) { CloseMusic(); _current = track; MarkUnavailable(track, "文件不存在，请重新定位"); UpdatePulse(); return; }
        if (!Extensions.Contains(Path.GetExtension(track.FilePath))) { CloseMusic(); _current = track; MarkUnavailable(track, "不支持此音频格式"); UpdatePulse(); return; }
        if (_current?.Id == track.Id && _music is not null)
        {
            _music.Play();
            _isPlaying = true;
            _status = "";
            EnsurePulse();
            Notify();
            return;
        }
        CloseMusic();
        _current = track;
        _duration = 0;
        var player = new MediaPlayer { Volume = _musicVolume };
        _music = player;
        player.MediaOpened += OnMusicOpened;
        player.MediaEnded += OnMusicEnded;
        player.MediaFailed += OnMusicFailed;
        try
        {
            player.Open(new Uri(track.FilePath, UriKind.Absolute));
            player.Play();
            _isPlaying = true;
            _status = "加载中";
            EnsurePulse();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            CloseMusic();
            MarkUnavailable(track, $"无法播放：{ex.Message}");
            UpdatePulse();
        }
        Notify();
    }

    public void Pause()
    {
        _userRevision++;
        _music?.Pause();
        _isPlaying = false;
        _status = "";
        UpdatePulse();
        Notify();
    }

    public void Stop()
    {
        _userRevision++;
        CloseMusic();
        _isPlaying = false;
        _duration = 0;
        _status = "";
        UpdatePulse();
        Notify();
    }

    public void Previous() => MoveTrack(-1);
    public void Next() => MoveTrack(1);

    private void MoveTrack(int offset)
    {
        if (_tracks.Count == 0) { _status = "请先导入音乐。"; Notify(); return; }
        var index = _current is null ? (offset > 0 ? -1 : 0) : _tracks.FindIndex(t => t.Id == _current.Id);
        var next = (index + offset + _tracks.Count) % _tracks.Count;
        Play(_tracks[next].Id);
    }

    public void Seek(double seconds)
    {
        if (_music is null || !double.IsFinite(seconds)) return;
        // A binding refresh must not seek backwards or invalidate pending alarm resume.
        if (Math.Abs(seconds - PositionSeconds) < .6) return;
        _userRevision++;
        _music.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, Math.Max(0, _duration)));
        Notify();
    }

    private void OnMusicOpened(object? sender, EventArgs e)
    {
        if (_music is null || !ReferenceEquals(sender, _music)) return;
        _duration = _music.NaturalDuration.HasTimeSpan ? _music.NaturalDuration.TimeSpan.TotalSeconds : 0;
        if (_current is not null)
        {
            ClearIssue(_current);
            _status = "";
        }
        Notify();
    }

    private void OnMusicEnded(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _music)) return;
        _isPlaying = false;
        _music?.Stop();
        _status = "播放结束";
        UpdatePulse();
        Notify();
    }

    private void OnMusicFailed(object? sender, System.Windows.Media.ExceptionEventArgs e)
    {
        if (!ReferenceEquals(sender, _music)) return;
        var track = _current;
        CloseMusic();
        if (track is not null) MarkUnavailable(track, "系统无法解码此音频，请重新定位或选择其他文件");
        UpdatePulse();
    }

    private void MarkUnavailable(MusicTrack track, string issue)
    {
        var index = _tracks.FindIndex(t => t.Id == track.Id);
        if (index >= 0) _tracks[index] = track with { Issue = issue };
        if (_current?.Id == track.Id) _current = track with { Issue = issue };
        _isPlaying = false;
        _status = issue;
        NotifyLibrary();
    }

    private void ClearIssue(MusicTrack track)
    {
        if (track.Issue is null) return;
        var index = _tracks.FindIndex(t => t.Id == track.Id);
        if (index >= 0) _tracks[index] = track with { Issue = null };
        _current = track with { Issue = null };
        NotifyLibrary();
    }

    public async Task PlayAlarmAsync(string? path, bool silent = false)
    {
        ThrowIfDisposed();
        var carryResume = _isAlarmPlaying && _resumeAfterAlarm && _resumeRevision == _userRevision;
        StopAlarmCore(false);
        var generation = ++_alarmGeneration;
        if (silent || string.Equals(path, "__silent__", StringComparison.Ordinal))
        {
            _status = "已静音";
            Notify();
            return;
        }
        _resumeAfterAlarm = _isPlaying || carryResume;
        _resumeRevision = _userRevision;
        if (_isPlaying) { _music?.Pause(); _isPlaying = false; }
        _isAlarmPlaying = true;
        _alarmStarted = Environment.TickCount64;
        _alarmIsFallback = string.IsNullOrWhiteSpace(path) || !File.Exists(path);
        _status = _alarmIsFallback && !string.IsNullOrWhiteSpace(path)
            ? "所选铃声不可用，已回退默认提示音。" : "铃声播放中";
        EnsurePulse();
        Notify();
        try
        {
            var selected = _alarmIsFallback ? await EnsureDefaultAlarmAsync() : path!;
            if (_disposed || generation != _alarmGeneration) return;
            OpenAlarm(selected);
            EnsurePulse();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            if (_disposed || generation != _alarmGeneration) return;
            _status = $"提示音无法播放，专注已完成：{ex.Message}";
            if (_alarmVolume > 0) System.Media.SystemSounds.Exclamation.Play();
            StopAlarmCore(true);
        }
    }

    private void OpenAlarm(string path)
    {
        CloseAlarm();
        var player = new MediaPlayer { Volume = _alarmVolume };
        _alarm = player;
        player.MediaEnded += OnAlarmEnded;
        player.MediaFailed += OnAlarmFailed;
        player.Open(new Uri(path, UriKind.Absolute));
        player.Play();
    }

    private void OnAlarmEnded(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _alarm)) StopAlarm();
    }

    private async void OnAlarmFailed(object? sender, System.Windows.Media.ExceptionEventArgs e)
    {
        if (!ReferenceEquals(sender, _alarm)) return;
        var generation = _alarmGeneration;
        CloseAlarm();
        if (_alarmIsFallback)
        {
            _status = "系统无法播放提示音，专注已完成。";
            if (_alarmVolume > 0) System.Media.SystemSounds.Exclamation.Play();
            StopAlarmCore(true);
            return;
        }
        _alarmIsFallback = true;
        _status = "所选铃声无法解码，已回退默认提示音。";
        Notify();
        try
        {
            var fallback = await EnsureDefaultAlarmAsync();
            if (!_disposed && generation == _alarmGeneration) OpenAlarm(fallback);
        }
        catch (Exception ex)
        {
            if (_disposed || generation != _alarmGeneration) return;
            _status = $"默认提示音不可用：{ex.Message}";
            if (_alarmVolume > 0) System.Media.SystemSounds.Exclamation.Play();
            StopAlarmCore(true);
        }
    }

    public void StopAlarm() => StopAlarmCore(true);

    private void StopAlarmCore(bool allowResume)
    {
        _alarmGeneration++;
        var wasAlarm = _isAlarmPlaying;
        CloseAlarm();
        _isAlarmPlaying = false;
        if (allowResume && wasAlarm && _resumeAfterAlarm && _resumeMusic && _resumeRevision == _userRevision && _music is not null)
        {
            _music.Play();
            _isPlaying = true;
            _status = "已恢复音乐";
        }
        _resumeAfterAlarm = false;
        UpdatePulse();
        if (wasAlarm) Notify();
    }

    private async Task<string> EnsureDefaultAlarmAsync()
    {
        var path = Path.Combine(_directory, "default-alarm.wav");
        if (File.Exists(path)) return path;
        Directory.CreateDirectory(_directory);
        // A short two-tone PCM chime can be played with the same independently controlled alarm volume.
        const int sampleRate = 22050;
        const int samples = sampleRate;
        using var memory = new MemoryStream();
        using (var writer = new BinaryWriter(memory, System.Text.Encoding.UTF8, true))
        {
            writer.Write("RIFF"u8.ToArray()); writer.Write(36 + samples * 2); writer.Write("WAVE"u8.ToArray());
            writer.Write("fmt "u8.ToArray()); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
            writer.Write(sampleRate); writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16);
            writer.Write("data"u8.ToArray()); writer.Write(samples * 2);
            for (var sample = 0; sample < samples; sample++)
            {
                var time = (double)sample / sampleRate;
                var local = time < .5 ? time : time - .5;
                var frequency = time < .5 ? 660 : 880;
                var envelope = Math.Min(1, local * 40) * Math.Exp(-local * 7);
                writer.Write((short)(Math.Sin(2 * Math.PI * frequency * time) * envelope * short.MaxValue * .35));
            }
        }
        await File.WriteAllBytesAsync(path, memory.ToArray());
        return path;
    }

    private void EnsurePulse()
    {
        if (_disposed) return;
        if (_pulse is null)
        {
            _pulse = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
            _pulse.Tick += OnPulse;
        }
        _pulse.Start();
    }

    private void OnPulse(object? sender, EventArgs e)
    {
        if (_isAlarmPlaying && Environment.TickCount64 - _alarmStarted >= 30_000) StopAlarm();
        Notify();
    }

    private void UpdatePulse()
    {
        if (_isPlaying || _isAlarmPlaying) EnsurePulse(); else _pulse?.Stop();
    }

    private void CloseMusic()
    {
        if (_music is null) return;
        _music.MediaOpened -= OnMusicOpened;
        _music.MediaEnded -= OnMusicEnded;
        _music.MediaFailed -= OnMusicFailed;
        _music.Close();
        _music = null;
        _isPlaying = false;
        _duration = 0;
    }

    private void CloseAlarm()
    {
        if (_alarm is null) return;
        _alarm.MediaEnded -= OnAlarmEnded;
        _alarm.MediaFailed -= OnAlarmFailed;
        _alarm.Close();
        _alarm = null;
    }

    private void Notify() => Publish(Changed);
    private void NotifyLibrary() { Publish(LibraryChanged); Notify(); }
    private void Publish(EventHandler? handlers)
    {
        if (_disposed || handlers is null) return;
        _dispatcher.Invoke(() =>
        {
            foreach (EventHandler handler in handlers.GetInvocationList())
            {
                try { handler(this, EventArgs.Empty); }
                catch (Exception ex) { Debug.WriteLine($"Audio observer failed: {ex}"); }
            }
        });
    }

    private static double NormalizeVolume(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _alarmGeneration++;
        _disposed = true;
        if (_pulse is not null) { _pulse.Stop(); _pulse.Tick -= OnPulse; }
        CloseAlarm();
        CloseMusic();
        _isAlarmPlaying = false;
        Changed = null;
        LibraryChanged = null;
    }
}
