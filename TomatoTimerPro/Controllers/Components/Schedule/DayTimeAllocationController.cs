using System.ComponentModel;
using System.Globalization;
using System.IO;
using TomatoTimerPro.Contracts.Storage;
using TomatoTimerPro.Controllers.Components.Countdown;
using TomatoTimerPro.Controllers.Pages.Calendar;
using TomatoTimerPro.Infrastructure.Binding;
using TomatoTimerPro.RuntimeStore.States.Timer;

namespace TomatoTimerPro.Controllers.Components.Schedule;

public sealed record DayTimeAllocationSegment(string Title, string DurationText, double Fraction, int Number = 0);

/// <summary>Today's planned allocation and measured focus, shared by schedule and calendar views.</summary>
public sealed class DayTimeAllocationController : ObservableObject, IAsyncDisposable
{
    private readonly CountdownController _countdown;
    private readonly IAppStorage _storage;
    private readonly CalendarPageController _calendar;
    private readonly Func<DateOnly> _today;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly object _gate = new();
    private readonly Dictionary<string, TimerSession> _sessions = new(StringComparer.Ordinal);
    private TimerSession? _previousCurrent;
    private DateOnly _day;
    private long _generation;
    private bool _disposed;
    private string _workspacePath = "";
    private string _statusMessage = "";

    public string WorkspacePath => _workspacePath;
    public string StatusMessage => _statusMessage;
    public IReadOnlyList<DayTimeAllocationSegment> Segments { get; private set; } = [];
    public bool HasPlans => Segments.Count > 0;
    public double PlannedSeconds { get; private set; }
    public double ActualSeconds { get; private set; }
    public string PlannedText => "计划 " + FormatDuration(PlannedSeconds);
    public string WorkedText => "已专注 " + FormatDuration(ActualSeconds);
    public event Action? Changed;

    public DayTimeAllocationController(CalendarPageController calendar, CountdownController countdown,
        IAppStorage storage, Func<DateOnly>? today = null) : this(countdown, storage, calendar, today) { }

    public DayTimeAllocationController(CountdownController countdown, IAppStorage storage,
        CalendarPageController calendar, Func<DateOnly>? today = null)
    {
        _countdown = countdown; _storage = storage; _calendar = calendar;
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Now));
        _day = _today();
        _previousCurrent = countdown.Current;
        countdown.Changed += CountdownChanged;
        calendar.PropertyChanged += CalendarChanged;
        Rebuild();
    }

    public async Task SetWorkspaceAsync(string? path)
    {
        var normalized = string.IsNullOrWhiteSpace(path) ? "" : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        await OnOwnerAsync(() =>
        {
            if (_disposed) return;
            if (!SameWorkspace(_workspacePath, normalized))
            {
                _workspacePath = normalized;
                _sessions.Clear();
                _generation++;
                _statusMessage = "";
                OnPropertyChanged(nameof(WorkspacePath));
                OnPropertyChanged(nameof(StatusMessage));
            }
            EnsureDay();
            Rebuild();
        });
        await RefreshAsync();
    }

    /// <summary>Reads only the selected workspace's start-day session folder, never all history.</summary>
    public async Task RefreshAsync()
    {
        var workspace = "";
        var day = default(DateOnly);
        long request = -1;
        await OnOwnerAsync(() =>
        {
            if (_disposed) return;
            EnsureDay();
            workspace = _workspacePath; day = _day; request = ++_generation;
            Rebuild();
        });
        if (request < 0) return;
        try
        {
            IReadOnlyList<TimerSession> saved = workspace.Length == 0
                ? [] : await _storage.LoadSessionsAsync(workspace, day).ConfigureAwait(false);
            await OnOwnerAsync(() =>
            {
                if (_disposed || request != _generation || day != _today()) return;
                _sessions.Clear();
                foreach (var session in saved)
                    if (Matches(session) && !string.IsNullOrWhiteSpace(session.Id)) _sessions[session.Id] = session;
                _statusMessage = "";
                OnPropertyChanged(nameof(StatusMessage));
                Rebuild();
            });
        }
        catch (Exception error)
        {
            await OnOwnerAsync(() =>
            {
                if (_disposed || request != _generation || day != _today()) return;
                _statusMessage = "今日专注记录读取失败，请重试：" + error.Message;
                OnPropertyChanged(nameof(StatusMessage));
                Rebuild();
            });
        }
    }

    private void CountdownChanged() => Observe(UpdateCountdownAsync());

    private async Task UpdateCountdownAsync()
    {
        var refresh = false;
        await OnOwnerAsync(() =>
        {
            if (_disposed) return;
            refresh = EnsureDay();
            var current = _countdown.Current;
            if (_previousCurrent?.Id != current?.Id)
            {
                if (_previousCurrent is { } previous && Matches(previous))
                {
                    if (current is null) _sessions.Remove(previous.Id);
                    else _sessions[previous.Id] = previous;
                }
                // Invalidate reads started before deletion or a new session.
                _generation++;
                refresh = true;
            }
            _previousCurrent = current;
            Rebuild();
        });
        if (refresh) await RefreshAsync();
    }

    private void CalendarChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName is nameof(CalendarPageController.TodayPlans)
            or nameof(CalendarPageController.TodayLabel)) Observe(UpdateCalendarAsync());
    }

    private async Task UpdateCalendarAsync()
    {
        var refresh = false;
        await OnOwnerAsync(() =>
        {
            if (_disposed) return;
            refresh = EnsureDay();
            Rebuild();
        });
        if (refresh) await RefreshAsync();
    }

    private bool EnsureDay()
    {
        var today = _today();
        if (_day == today) return false;
        _day = today; _sessions.Clear(); _generation++;
        _statusMessage = "";
        OnPropertyChanged(nameof(StatusMessage));
        _calendar.RefreshToday();
        return true;
    }

    private void Rebuild()
    {
        var plans = _calendar.TodayPlans.Where(plan => plan.HasPomodoro && !plan.IsOnHold && !plan.IsAbandoned
            && double.IsFinite(plan.DurationMinutes) && plan.DurationMinutes > 0).ToArray();
        PlannedSeconds = plans.Sum(plan => plan.DurationMinutes * 60);
        Segments = plans.Select((plan, index) => new DayTimeAllocationSegment(plan.Title,
            FormatDuration(plan.DurationMinutes * 60), Math.Clamp(plan.DurationMinutes * 60 / PlannedSeconds, 0, 1), index + 1)).ToArray();
        var measured = new Dictionary<string, TimerSession>(_sessions, StringComparer.Ordinal);
        if (_countdown.Current is { } current && Matches(current)) measured[current.Id] = current;
        ActualSeconds = measured.Values.Where(Matches).Sum(session =>
            double.IsFinite(session.FocusedSeconds) ? Math.Max(0, session.FocusedSeconds) : 0);
        foreach (var name in new[] { nameof(Segments), nameof(HasPlans), nameof(PlannedSeconds),
            nameof(ActualSeconds), nameof(PlannedText), nameof(WorkedText) }) OnPropertyChanged(name);
        Changed?.Invoke();
    }

    private bool Matches(TimerSession session) => _workspacePath.Length > 0 && session.Day == _day
        && SameWorkspace(session.WorkspacePath, _workspacePath);

    private static bool SameWorkspace(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return left == right;
        try { return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static string FormatDuration(double seconds)
    {
        var whole = (long)Math.Floor(Math.Max(0, seconds));
        var hours = whole / 3600; var minutes = whole % 3600 / 60; var remainder = whole % 60;
        var parts = new List<string>();
        if (hours > 0) parts.Add(hours.ToString(CultureInfo.InvariantCulture) + " 小时");
        if (minutes > 0) parts.Add(minutes.ToString(CultureInfo.InvariantCulture) + " 分钟");
        if (remainder > 0 || parts.Count == 0) parts.Add(remainder.ToString(CultureInfo.InvariantCulture) + " 秒");
        return string.Join(" ", parts);
    }

    private Task OnOwnerAsync(Action action)
    {
        if (_context is null || ReferenceEquals(_context, SynchronizationContext.Current))
        {
            lock (_gate) action();
            return Task.CompletedTask;
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _context.Post(_ =>
        {
            try { lock (_gate) action(); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
        }, null);
        return completion.Task;
    }

    private async void Observe(Task operation)
    {
        try { await operation; }
        catch (Exception error)
        {
            await OnOwnerAsync(() =>
            {
                if (_disposed) return;
                _statusMessage = "时间统计更新失败：" + error.Message;
                OnPropertyChanged(nameof(StatusMessage));
            });
        }
    }

    public ValueTask DisposeAsync() => new(OnOwnerAsync(() =>
    {
        if (_disposed) return;
        _disposed = true; _generation++;
        _countdown.Changed -= CountdownChanged;
        _calendar.PropertyChanged -= CalendarChanged;
        Changed = null;
        _sessions.Clear();
    }));
}
