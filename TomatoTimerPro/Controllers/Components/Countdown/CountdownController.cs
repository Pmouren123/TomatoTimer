using System.Text.Json;
using TomatoTimerPro.Contracts.Storage;
using TomatoTimerPro.Contracts.Threading;
using TomatoTimerPro.Contracts.Timing;
using TomatoTimerPro.RuntimeStore.Abstractions;
using TomatoTimerPro.RuntimeStore.States.Timer;

namespace TomatoTimerPro.Controllers.Components.Countdown;

/// <summary>Reusable application-owned countdown. It has no dependency on any WPF view.</summary>
public sealed class CountdownController : IAsyncDisposable
{
    private readonly IRuntimeStore _store;
    private readonly IAppStorage _storage;
    private readonly IUiDispatcher _dispatcher;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private Task? _heartbeat;
    private long _runStarted;
    private long _lastCheckpoint;
    private double _runBaseFocused;
    private bool _disposed;

    public event Action? Changed;
    public event Action<TimerSession>? Completed;
    public event Action<string>? StorageError;
    public string? LastStorageError { get; private set; }
    public TimerSession? Current => _store.Get<TimerState>().Current;
    public bool IsRunning => Current?.Status == SessionStatus.Running;
    public bool IsPaused => Current?.Status == SessionStatus.Paused;
    public bool IsActive => IsRunning || IsPaused;
    public TimeSpan Remaining => TimeSpan.FromSeconds(Math.Max(0,
        (Current?.PlannedSeconds ?? 0) - (Current?.FocusedSeconds ?? 0)));

    public CountdownController(IRuntimeStore store, IAppStorage storage, IUiDispatcher dispatcher, IClock? clock = null)
    {
        _store = store;
        _storage = storage;
        _dispatcher = dispatcher;
        _clock = clock ?? new SystemClock();
        try { _ = store.Get<TimerState>(); }
        catch (KeyNotFoundException) { store.Set(new TimerState()); }
    }

    public async Task StartAsync(string workspace, string title, TimeSpan duration, string? alarmPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        if (duration.TotalMinutes is < .01 or > 1440 || !double.IsFinite(duration.TotalSeconds))
            throw new ArgumentOutOfRangeException(nameof(duration), "专注时长应在 0.01 至 1440 分钟之间。");
        await _operations.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsActive) throw new InvalidOperationException("请先完成或取消当前番茄钟。");
            var session = new TimerSession
            {
                WorkspacePath = Path.GetFullPath(workspace),
                Title = string.IsNullOrWhiteSpace(title) ? "专注时光" : title.Trim(),
                StartedAt = _clock.LocalNow,
                PlannedSeconds = duration.TotalSeconds,
                Status = SessionStatus.Running,
                AlarmPath = alarmPath
            };
            await _storage.PrepareDayAsync(workspace, session.Day);
            await _storage.SaveActiveSessionAsync(session);
            try { await _storage.SaveSessionAsync(session); }
            catch
            {
                await _storage.SaveActiveSessionAsync(session with { Status = SessionStatus.Interrupted, EndedAt = _clock.LocalNow });
                throw;
            }
            _runStarted = _lastCheckpoint = _clock.Milliseconds;
            _runBaseFocused = 0;
            LastStorageError = null;
            await PublishAsync(session);
            EnsureHeartbeat();
        }
        finally { _operations.Release(); }
    }

    public async Task PauseResumeAsync()
    {
        await _operations.WaitAsync();
        try
        {
            if (!IsActive) return;
            var session = Current!;
            if (IsRunning)
            {
                session = SnapshotRunning(session);
                if (session.FocusedSeconds >= session.PlannedSeconds)
                {
                    await CompleteAsync(session);
                    return;
                }
                session = session with { Status = SessionStatus.Paused };
            }
            else
            {
                _runStarted = _clock.Milliseconds;
                _runBaseFocused = session.FocusedSeconds;
                session = session with { Status = SessionStatus.Running };
            }
            await PublishAsync(session);
            await PersistAsync(session);
            if (session.Status == SessionStatus.Running) EnsureHeartbeat();
        }
        finally { _operations.Release(); }
    }

    public Task CancelAsync() => FinishAsync(SessionStatus.Cancelled);
    public Task EndEarlyAsync() => FinishAsync(SessionStatus.EndedEarly);
    public Task InterruptAsync() => FinishAsync(SessionStatus.Interrupted);

    /// <summary>Called only after explicit user confirmation. Stops execution
    /// even if storage deletion fails so the user can create another task.</summary>
    public async Task DeleteCurrentRecordAsync()
    {
        await _operations.WaitAsync();
        try
        {
            var current = Current;
            if (current is null) return;
            var stopped = (IsRunning ? SnapshotRunning(current) : current) with
            {
                Status = SessionStatus.Cancelled,
                EndedAt = _clock.LocalNow
            };
            await PublishAsync(stopped);
            // Stop crash recovery from resurrecting a task whose files the
            // user has asked to delete. Failure here prevents file deletion.
            await _storage.SaveActiveSessionAsync(null);
            try { await _storage.DeleteSessionAsync(stopped); }
            catch
            {
                try { await _storage.SaveSessionAsync(stopped); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                throw;
            }
            await _dispatcher.InvokeAsync(() =>
            {
                _store.Set(new TimerState());
                Changed?.Invoke();
            });
        }
        finally { _operations.Release(); }
    }

    private async Task FinishAsync(SessionStatus status)
    {
        await _operations.WaitAsync();
        try
        {
            if (!IsActive) return;
            var session = IsRunning ? SnapshotRunning(Current!) : Current!;
            session = session with { Status = status, EndedAt = _clock.LocalNow };
            await PublishAsync(session);
            await PersistAsync(session);
        }
        finally { _operations.Release(); }
    }

    public async Task RestoreAsync()
    {
        await _operations.WaitAsync();
        try
        {
            var previous = await _storage.LoadActiveSessionAsync();
            if (previous is null) return;
            var restored = previous.Status is SessionStatus.Running or SessionStatus.Paused
                ? previous with { Status = SessionStatus.Interrupted, EndedAt = _clock.LocalNow }
                : previous;
            await PublishAsync(restored);
            await PersistAsync(restored);
        }
        finally { _operations.Release(); }
    }

    public async Task TickAsync()
    {
        await _operations.WaitAsync();
        try
        {
            if (_disposed || !IsRunning) return;
            var session = SnapshotRunning(Current!);
            if (session.FocusedSeconds >= session.PlannedSeconds)
            {
                await CompleteAsync(session);
                return;
            }
            await PublishAsync(session);
            if (_clock.Milliseconds - _lastCheckpoint >= 15_000)
            {
                _lastCheckpoint = _clock.Milliseconds;
                await PersistAsync(session);
            }
        }
        finally { _operations.Release(); }
    }

    private TimerSession SnapshotRunning(TimerSession session) => session with
    {
        FocusedSeconds = Math.Min(session.PlannedSeconds,
            _runBaseFocused + Math.Max(0, _clock.Milliseconds - _runStarted) / 1000d)
    };

    private async Task CompleteAsync(TimerSession session)
    {
        var completed = session with
        {
            Status = SessionStatus.Completed,
            FocusedSeconds = session.PlannedSeconds,
            EndedAt = _clock.LocalNow
        };
        await PublishAsync(completed);
        await PersistAsync(completed);
        await _dispatcher.InvokeAsync(() => Completed?.Invoke(completed));
    }

    private Task PublishAsync(TimerSession session) => _dispatcher.InvokeAsync(() =>
    {
        _store.Set(new TimerState(session));
        Changed?.Invoke();
    });

    private async Task PersistAsync(TimerSession session)
    {
        try
        {
            await _storage.SaveActiveSessionAsync(session);
            await _storage.SaveSessionAsync(session);
            if (session.Status is not (SessionStatus.Running or SessionStatus.Paused))
                await _storage.SaveActiveSessionAsync(null);
            LastStorageError = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            var recoveryMessage = "";
            try
            {
                await _storage.SaveRecoveryAsync(_storage.GetSessionMetadataPath(session), JsonSerializer.Serialize(session));
                recoveryMessage = " 计时记录已保留在本地恢复草稿中。";
            }
            catch (Exception recoveryError) when (recoveryError is IOException or UnauthorizedAccessException)
            {
                recoveryMessage = " 本地恢复草稿也写入失败，请勿立即退出软件。";
            }
            LastStorageError = "计时记录保存失败：" + error.Message + recoveryMessage;
            await _dispatcher.InvokeAsync(() => StorageError?.Invoke(LastStorageError));
        }
    }

    private void EnsureHeartbeat()
    {
        if (_heartbeat is null || _heartbeat.IsCompleted) _heartbeat = RunHeartbeatAsync();
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    private async Task RunHeartbeatAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                if (!IsRunning)
                {
                    await _wake.WaitAsync(_lifetime.Token);
                    continue;
                }
                await Task.Delay(TimeSpan.FromSeconds(1), _lifetime.Token);
                await TickAsync();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            LastStorageError = "计时任务出现异常：" + error.Message;
            await _dispatcher.InvokeAsync(() => StorageError?.Invoke(LastStorageError));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        if (_heartbeat is not null) await _heartbeat;
        _wake.Dispose();
        _lifetime.Dispose();
        Changed = null;
        Completed = null;
        StorageError = null;
    }

    private sealed class SystemClock : IClock
    {
        public long Milliseconds => Environment.TickCount64;
        public DateTimeOffset LocalNow => DateTimeOffset.Now;
    }
}
