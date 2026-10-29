using System.Diagnostics;
using TomatoTimerPro.Controllers.Components.Countdown;
using TomatoTimerPro.Infrastructure.Binding;
using TomatoTimerPro.RuntimeStore.States.Planning;
using TomatoTimerPro.RuntimeStore.States.Timer;
using TomatoTimerPro.Tools.Time;

namespace TomatoTimerPro.Controllers.Components.Schedule;

public sealed record ScheduleRow(string Id, string Title, string DurationText,
    bool IsCurrent, bool IsCompleted, bool IsUpcoming);

/// <summary>Runs an immutable ordered snapshot of scheduled pomodoros. It owns
/// automatic advancement only; stopping it never modifies an independent clock.</summary>
public sealed class SchedulePlaybackController : ObservableObject, IAsyncDisposable
{
    private readonly CountdownController _countdown;
    private readonly Func<PlanEntry, Task<bool>> _startPlan;
    private readonly Func<PlanEntry, Task> _markCompleted;
    private readonly Action<Exception> _reportError;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly object _state = new();
    private readonly HashSet<string> _completed = new(StringComparer.Ordinal);
    private PlanEntry[] _plans = [];
    private IReadOnlyList<ScheduleRow> _entries = [];
    private int _index = -1;
    private long _generation;
    private bool _active;
    private bool _starting;
    private bool _disposed;
    private string? _sessionId;
    private string? _processingSession;
    private Task _pendingCompletion = Task.CompletedTask;
    private CancellationTokenSource? _restCancellation;
    private bool _resting;
    private TimeSpan _restDuration;

    public event Action? Changed;
    public IReadOnlyList<ScheduleRow> Entries { get { lock (_state) return _entries; } }
    public bool IsActive { get { lock (_state) return _active; } }
    public bool IsResting { get { lock (_state) return _resting; } }
    public TimeSpan RestDuration
    {
        get { lock (_state) return _restDuration; }
        set { lock (_state) _restDuration = value < TimeSpan.Zero ? TimeSpan.Zero : value > TimeSpan.FromHours(24) ? TimeSpan.FromHours(24) : value; Notify(); }
    }
    public string RestText { get { lock (_state) return _resting ? $"休息中 · {RestRemainingTextCore()}" : ""; } }
    private string RestRemainingTextCore()
    {
        if (!_resting || _restUntil is null) return "";
        var remaining = _restUntil.Value - DateTimeOffset.UtcNow;
        return DurationFormatter.Format(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
    }
    private DateTimeOffset? _restUntil;
    public string CurrentTitle { get { lock (_state) return _index >= 0 ? _plans[_index].Title : "尚未开始"; } }
    public string PositionText { get { lock (_state) return $"{Math.Max(0, _index + 1)} / {_plans.Length}"; } }
    public double Progress
    {
        get
        {
            lock (_state)
            {
                if (_plans.Length == 0) return 0;
                var fraction = 0d;
                var current = _countdown.Current;
                if (_index >= 0 && !_completed.Contains(_plans[_index].Id) && current is not null && current.Id == _sessionId && current.PlannedSeconds > 0)
                    fraction = Math.Clamp(current.FocusedSeconds / current.PlannedSeconds, 0, 1);
                return Math.Clamp((_completed.Count + fraction) / _plans.Length * 100, 0, 100);
            }
        }
    }

    public SchedulePlaybackController(CountdownController countdown, Func<PlanEntry, Task<bool>> startPlan,
        Func<PlanEntry, Task> markCompleted, Action<Exception> reportError)
    {
        _countdown = countdown;
        _startPlan = startPlan;
        _markCompleted = markCompleted;
        _reportError = reportError;
        _countdown.Changed += OnCountdownChanged;
        _countdown.Completed += OnCountdownCompleted;
    }

    public async Task StartAsync(IReadOnlyList<PlanEntry> orderedPlans)
    {
        ArgumentNullException.ThrowIfNull(orderedPlans);
        // Do not wait behind an in-flight completion callback just to reject a
        // second start. StopAsync must remain able to invalidate that callback.
        lock (_state)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SchedulePlaybackController));
            if (_active || _countdown.IsActive)
            {
                Report(new InvalidOperationException("已有番茄钟正在运行或暂停，请先结束当前任务，再启动日程。"));
                return;
            }
        }
        await _operations.WaitAsync();
        try
        {
            long generation;
            lock (_state)
            {
                if (_disposed) return;
                if (_active || _countdown.IsActive)
                    throw new InvalidOperationException("已有番茄钟正在运行或暂停，请先结束当前任务，再启动日程。");
                var plans = orderedPlans.Where(plan => plan.HasPomodoro && plan.Status == PlanStatus.Incomplete).ToArray();
                if (plans.Select(plan => plan.Id).Distinct(StringComparer.Ordinal).Count() != plans.Length)
                    throw new InvalidOperationException("日程中存在重复的计划标识，请重新加载计划。");
                if (plans.Any(plan => string.IsNullOrWhiteSpace(plan.Id) || !double.IsFinite(plan.DurationMinutes)
                    || plan.DurationMinutes is < .01 or > 1440))
                    throw new InvalidOperationException("日程中的番茄钟时长无效，请先检查计划设置。");
                _plans = plans;
                _index = -1;
                _sessionId = _processingSession = null;
                _completed.Clear();
                generation = ++_generation;
                _active = _plans.Length != 0;
            }
            Notify();
            if (!IsActive)
            {
                Report(new InvalidOperationException("当天没有未完成的番茄钟计划，请先在计划里添加番茄钟。"));
                return;
            }
            await StartIndexAsync(0, generation);
        }
        catch (Exception error) { Fail(error); }
        finally { _operations.Release(); }
    }

    /// <summary>Invalidates pending callbacks immediately, including an awaited
    /// status save. The current countdown is intentionally left untouched.</summary>
    public Task StopAsync()
    {
        lock (_state)
        {
            ++_generation;
            _active = false;
            _starting = false;
            _resting = false;
            _restUntil = null;
            _restCancellation?.Cancel();
        }
        Notify();
        return Task.CompletedTask;
    }

    private async Task StartIndexAsync(int index, long generation)
    {
        PlanEntry plan;
        string? previousSession;
        lock (_state)
        {
            if (!IsCurrentGeneration(generation)) return;
            if (index >= _plans.Length) { _active = false; Notify(); return; }
            plan = _plans[index];
            previousSession = _countdown.Current?.Id;
            _starting = true;
        }
        bool started;
        try { started = await _startPlan(plan); }
        finally { lock (_state) _starting = false; }
        lock (_state)
        {
            if (!IsCurrentGeneration(generation)) return;
            var session = _countdown.Current;
            if (!started || !_countdown.IsActive || session is null || session.Id == previousSession)
                throw new InvalidOperationException($"无法启动计划「{plan.Title}」，日程已停止，请检查工作区和时长后重试。");
            _index = index;
            _sessionId = session.Id;
            _processingSession = null;
        }
        Notify();
    }

    private void OnCountdownChanged()
    {
        lock (_state)
        {
            if (_disposed || !_active || _starting) return;
            var session = _countdown.Current;
            if (_sessionId is not null && (session is null || session.Id != _sessionId
                || session.Status is SessionStatus.Cancelled or SessionStatus.EndedEarly or SessionStatus.Interrupted))
            {
                ++_generation;
                _active = false;
            }
            // Completed is published before Completed event. Keep the queue
            // alive here so its status-save callback can finish exactly once.
        }
        Notify();
    }

    private void OnCountdownCompleted(TimerSession session)
    {
        lock (_state)
        {
            if (_disposed || !_active || session.Id != _sessionId || _processingSession == session.Id) return;
            _processingSession = session.Id;
            _pendingCompletion = CompleteCurrentAsync(session.Id, _generation);
        }
    }

    private async Task CompleteCurrentAsync(string sessionId, long generation)
    {
        // Countdown emits its completion while holding its operation gate.
        // Yield before attempting the next countdown, avoiding lock re-entry.
        await Task.Yield();
        await _operations.WaitAsync();
        try
        {
            PlanEntry plan;
            int index;
            lock (_state)
            {
                if (!IsCurrentGeneration(generation) || _sessionId != sessionId || _index < 0) return;
                index = _index;
                plan = _plans[index];
            }
            await _markCompleted(plan);
            lock (_state)
            {
                // Preserve a successfully saved completion indicator even when
                // Stop was requested during the save, but never start another.
                if (_sessionId == sessionId) _completed.Add(plan.Id);
                if (!IsCurrentGeneration(generation)) { Notify(); return; }
            }
            Notify();
            TimeSpan rest;
            lock (_state) rest = index + 1 < _plans.Length ? _restDuration : TimeSpan.Zero;
            if (rest > TimeSpan.Zero)
            {
                CancellationToken token;
                lock (_state)
                {
                    if (!IsCurrentGeneration(generation)) return;
                    _resting = true;
                    _restUntil = DateTimeOffset.UtcNow + rest;
                    _restCancellation?.Dispose();
                    _restCancellation = new CancellationTokenSource();
                    token = _restCancellation.Token;
                }
                Notify();
                try { await Task.Delay(rest, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                finally
                {
                    lock (_state)
                    {
                        _resting = false;
                        _restUntil = null;
                    }
                    Notify();
                }
            }
            await StartIndexAsync(index + 1, generation);
        }
        catch (Exception error) { Fail(error, generation); }
        finally { _operations.Release(); }
    }

    private bool IsCurrentGeneration(long generation) => !_disposed && _active && _generation == generation;

    private void Fail(Exception error, long? generation = null)
    {
        lock (_state)
        {
            if (generation is not null && _generation != generation) return;
            ++_generation;
            _active = false;
            _starting = false;
        }
        Notify();
        Report(error);
    }

    private void Report(Exception error)
    {
        try { _reportError(error); }
        catch (Exception reportFailure) { Trace.TraceError("Schedule error reporting failed: {0}; original: {1}", reportFailure, error); }
    }

    private void Notify()
    {
        lock (_state)
        {
            _entries = _plans.Select((plan, index) => new ScheduleRow(plan.Id, plan.Title,
                DurationFormatter.Format(TimeSpan.FromMinutes(plan.DurationMinutes)), index == _index,
                _completed.Contains(plan.Id), index > _index && !_completed.Contains(plan.Id))).ToArray();
        }
        foreach (var property in new[] { nameof(Entries), nameof(IsActive), nameof(IsResting), nameof(RestText), nameof(CurrentTitle), nameof(PositionText), nameof(Progress) })
            OnPropertyChanged(property);
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        Task pending;
        lock (_state)
        {
            if (_disposed) return;
            _disposed = true;
            _active = false;
            ++_generation;
            _restCancellation?.Cancel();
            pending = _pendingCompletion;
        }
        _countdown.Changed -= OnCountdownChanged;
        _countdown.Completed -= OnCountdownCompleted;
        await pending;
        await _operations.WaitAsync();
        _operations.Release();
        Changed = null;
    }
}
