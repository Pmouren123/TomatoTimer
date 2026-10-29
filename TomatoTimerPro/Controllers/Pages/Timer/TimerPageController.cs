using System.Collections.ObjectModel;
using System.Globalization;
using System.ComponentModel;
using TomatoTimerPro.Contracts.Interaction;
using TomatoTimerPro.Contracts.Navigation;
using TomatoTimerPro.Contracts.Storage;
using TomatoTimerPro.Controllers.Components.Countdown;
using TomatoTimerPro.Controllers.Pages.Calendar;
using TomatoTimerPro.Infrastructure.Commands;
using TomatoTimerPro.Infrastructure.Binding;
using TomatoTimerPro.RuntimeStore.States.Timer;
using TomatoTimerPro.RuntimeStore.States.Planning;
using TomatoTimerPro.Controllers.Components.Schedule;
using TomatoTimerPro.Tools.Time;

namespace TomatoTimerPro.Controllers.Pages.Timer;

public sealed record AlarmChoice(string Label, string? Path);

public sealed class TimerPageController : ObservableObject, IAsyncDisposable
{
    private readonly IAppStorage _storage;
    private readonly IUserInteraction _interaction;
    private readonly IPageNavigator _navigator;
    private readonly Dictionary<string, AlarmChoice> _alarms = new(StringComparer.OrdinalIgnoreCase);
    private string _taskTitle = "专注任务";
    private string _workspacePath = "";
    private string _remainingText = "25:00";
    private string _statusText = "待开始";
    private string _currentTaskTitle = "尚未开始";
    private string _progressText = "0%";
    private double _progress;
    private string _statusMessage = "";
    private AlarmChoice? _selectedAlarm;
    private AlarmChoice? _selectedStartAlarm;
    private string _scheduleRestMinutes = "0";
    private bool _ending;
    private bool _scheduleMode;
    private bool _startingTask;

    public CountdownController Countdown { get; }
    public CalendarPageController? Planner { get; }
    public DurationInputController Duration { get; } = new();
    public SchedulePlaybackController? Schedule { get; private set; }
    public DayTimeAllocationController? TimeAllocation { get; set; }
    public bool IsScheduleMode => _scheduleMode;
    public bool IsLeisureMode => !_scheduleMode;
    public bool HasRunnablePlans => Planner is { IsLoaded: true }
        && Planner.TodayPlans.Any(plan => plan.IsPending && plan.HasPomodoro);
    public bool ShowScheduleEmptyState => IsScheduleMode && !HasRunnablePlans && !IsActive && Schedule?.IsActive != true;
    public RelayCommand SelectScheduleModeCommand { get; }
    public RelayCommand SelectLeisureModeCommand { get; }
    public AsyncRelayCommand AddSchedulePlanCommand { get; }
    public event Func<Task>? ScheduleStartRequested;
    public ObservableCollection<AlarmChoice> AlarmChoices { get; } = [];
    public DurationInputController ScheduleRest { get; } = new();
    public AsyncRelayCommand SelectWorkspaceCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand PauseResumeCommand { get; }
    public AsyncRelayCommand CancelCommand { get; }
    public AsyncRelayCommand EndTaskCommand { get; }
    // Compatibility command for older integrations; there is no UI entry for it.
    public RelayCommand WriteReflectionCommand { get; }
    public RelayCommand StopAlarmCommand { get; }
    public AsyncRelayCommand PreviewAlarmCommand { get; }
    public event Action<string>? WorkspaceChanged;
    public event Action? FocusStarted;
    public event Action? StopAlarmRequested;
    public event Func<string?, Task>? StartAlarmRequested;
    public event Func<string?, Task>? PreviewAlarmRequested;
    public event Func<TimerSession, Task>? RecordDeleted;

    public string TaskTitle { get => _taskTitle; set => SetProperty(ref _taskTitle, value); }
    public string DurationMinutes
    {
        get => Duration.TryGetDuration(out var duration) ? duration.TotalMinutes.ToString(CultureInfo.InvariantCulture) : Duration.Minutes;
        set => Duration.SetMinutes(value);
    }
    public string WorkspacePath { get => _workspacePath; private set { if (SetProperty(ref _workspacePath, value)) OnPropertyChanged(nameof(WorkspaceName)); } }
    public string WorkspaceName => string.IsNullOrWhiteSpace(WorkspacePath) ? "未选择工作区" : new DirectoryInfo(WorkspacePath).Name;
    public string RemainingText { get => _remainingText; private set => SetProperty(ref _remainingText, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string CurrentTaskTitle { get => _currentTaskTitle; private set => SetProperty(ref _currentTaskTitle, value); }
    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public AlarmChoice? SelectedAlarm { get => _selectedAlarm; set => SetProperty(ref _selectedAlarm, value); }
    public AlarmChoice? SelectedStartAlarm { get => _selectedStartAlarm; set => SetProperty(ref _selectedStartAlarm, value); }
    public bool IsActive => Countdown.IsActive;
    public bool IsRunning => Countdown.IsRunning;
    public bool IsPaused => Countdown.IsPaused;

    public TimerPageController(CountdownController countdown, IAppStorage storage, IUserInteraction interaction, IPageNavigator navigator, CalendarPageController? planner = null)
    {
        Countdown = countdown;
        Planner = planner;
        _storage = storage;
        _interaction = interaction;
        _navigator = navigator;
        ScheduleRest.SetDuration(TimeSpan.FromMinutes(1));
        ScheduleRest.Changed += OnScheduleRestChanged;
        SelectWorkspaceCommand = new AsyncRelayCommand(SelectWorkspaceAsync, ReportError);
        StartCommand = new AsyncRelayCommand(StartSelectedModeAsync, ReportError,
            () => !IsActive && Schedule?.IsActive != true && !_startingTask && (!IsScheduleMode || HasRunnablePlans));
        SelectScheduleModeCommand = new RelayCommand(() => SelectMode(true));
        SelectLeisureModeCommand = new RelayCommand(() => SelectMode(false));
        AddSchedulePlanCommand = new AsyncRelayCommand(async () =>
        {
            if (Planner is not null) await Planner.EditDateAsync(DateOnly.FromDateTime(DateTime.Now), true);
            else await _navigator.NavigateAsync("calendar");
        }, ReportError, () => !IsActive && Schedule?.IsActive != true);
        PauseResumeCommand = new AsyncRelayCommand(Countdown.PauseResumeAsync, ReportError, () => IsActive);
        CancelCommand = new AsyncRelayCommand(AbandonAsync, ReportError, () => IsActive && !_ending);
        EndTaskCommand = new AsyncRelayCommand(EndTaskAsync, ReportError, () => IsActive && !_ending);
        WriteReflectionCommand = new RelayCommand(() => { }, () => Countdown.Current is not null && !IsActive);
        StopAlarmCommand = new RelayCommand(() => StopAlarmRequested?.Invoke());
        PreviewAlarmCommand = new AsyncRelayCommand(async () =>
        {
            if (PreviewAlarmRequested is null) return;
            foreach (Func<string?, Task> handler in PreviewAlarmRequested.GetInvocationList())
                await handler(SelectedAlarm?.Path);
        }, ReportError);
        Countdown.Changed += Refresh;
        Countdown.Completed += OnCompleted;
        Countdown.StorageError += OnStorageError;
        Duration.Changed += OnDurationChanged;
        if (Planner is not null) Planner.PropertyChanged += OnPlannerChanged;
    }

    public void UpdateAlarmChoices(IEnumerable<AlarmChoice> choices)
    {
        AlarmChoices.Clear();
        _alarms.Clear();
        foreach (var choice in choices.Where(choice => choice is not null))
        {
            if (choice.Path is not null && !_alarms.ContainsKey(choice.Path)) _alarms.Add(choice.Path, choice);
            AlarmChoices.Add(choice);
        }
        if (SelectedAlarm is null) SelectedAlarm = AlarmChoices.FirstOrDefault();
    }

    public async Task SetWorkspaceAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            path = Path.GetFullPath(path);
            await _storage.PrepareDayAsync(path, DateOnly.FromDateTime(DateTime.Now));
            WorkspacePath = path;
            WorkspaceChanged?.Invoke(path);
            StatusMessage = "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowProblem("工作区不可用", error.Message);
        }
    }

    /// <summary>Restores the recent workspace label without probing the disk during startup.</summary>
    public void RestoreWorkspacePath(string path)
    {
        WorkspacePath = string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(path);
        StatusMessage = "";
    }

    public async Task SelectWorkspaceAsync()
    {
        var path = await _interaction.PickFolderAsync();
        if (path is not null) await SetWorkspaceAsync(path);
    }

    public async Task StartPlanAsync(PlanEntry plan)
    {
        if (IsActive || Schedule?.IsActive == true || _startingTask)
        {
            ShowProblem("已有专注任务", "请先结束当前任务。");
            return;
        }
        SelectMode(false);
        await StartScheduledPlanAsync(plan);
    }

    public void AttachSchedule(SchedulePlaybackController schedule)
    {
        Schedule = schedule;
        if (ScheduleRest.TryGetDuration(out var rest)) schedule.RestDuration = rest;
        schedule.Changed += Refresh;
        OnPropertyChanged(nameof(Schedule));
    }

    private void SelectMode(bool schedule)
    {
        if (_scheduleMode == schedule) return;
        if (IsActive || Schedule?.IsActive == true || _startingTask)
        {
            ShowProblem("当前任务尚未结束", "请先结束当前任务，再切换模式。");
            return;
        }
        _scheduleMode = schedule;
        OnPropertyChanged(nameof(IsScheduleMode));
        OnPropertyChanged(nameof(IsLeisureMode));
        RefreshScheduleAvailability();
    }

    private async Task StartSelectedModeAsync()
    {
        if (IsScheduleMode)
        {
            if (!HasRunnablePlans) return;
            if (ScheduleStartRequested is not { } requested) return;
            foreach (Func<Task> handler in requested.GetInvocationList()) await handler();
        }
        else await StartAsync();
    }

    public async Task<bool> StartScheduledPlanAsync(PlanEntry plan)
    {
        if (IsActive || _startingTask || !plan.HasPomodoro) return false;
        TaskTitle = plan.Title;
        await StartAsync();
        return IsActive;
    }

    private async Task StartAsync()
    {
        if (_startingTask || IsActive) return;
        _startingTask = true;
        Refresh();
        try
        {
        if (string.IsNullOrWhiteSpace(WorkspacePath))
        {
            await SelectWorkspaceAsync();
            if (string.IsNullOrWhiteSpace(WorkspacePath)) return;
        }
        if (!Duration.TryGetDuration(out var duration))
        {
            ShowProblem("专注时长不正确", Duration.ValidationMessage);
            return;
        }
        try
        {
            await Countdown.StartAsync(WorkspacePath, TaskTitle, duration, SelectedAlarm?.Path);
            FocusStarted?.Invoke();
            if (StartAlarmRequested is not null)
                foreach (Func<string?, Task> handler in StartAlarmRequested.GetInvocationList()) await handler(SelectedStartAlarm?.Path);
            StatusMessage = "";
            Refresh();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            ReportError(error);
        }
        }
        finally { _startingTask = false; Refresh(); }
    }

    public async Task EndTaskAsync()
    {
        if (_ending || !IsActive) return;
        var sessionId = Countdown.Current!.Id;
        _ending = true;
        Refresh();
        try
        {
            var decision = await _interaction.ChooseTaskEndAsync(Countdown.Current!.Title);
            // The clock may have naturally finished while the question was
            // open. An old prompt must never cancel a different/new session.
            if (Countdown.Current?.Id != sessionId || !IsActive) return;
            if (decision == SaveDecision.Cancel) return;
            if (decision == SaveDecision.Save)
            {
                await Countdown.EndEarlyAsync();
                StatusMessage = "本次记录已保留";
            }
            else
            {
                if (!await ConfirmDeleteCoreAsync(sessionId)) return;
            }
            if (decision == SaveDecision.Save) StopAlarmRequested?.Invoke();
        }
        finally { _ending = false; Refresh(); }
    }

    public async Task DeleteCurrentRecordAsync()
    {
        if (_ending || Countdown.Current is null) return;
        var sessionId = Countdown.Current!.Id;
        _ending = true;
        Refresh();
        try
        {
            await ConfirmDeleteCoreAsync(sessionId);
        }
        finally { _ending = false; Refresh(); }
    }

    private Task AbandonAsync() => DeleteCurrentRecordAsync();

    private async Task<bool> ConfirmDeleteCoreAsync(string sessionId)
    {
        if (!await _interaction.ConfirmAsync("删除本次记录", "确认放弃并删除本次番茄钟记录吗？\n本次任务会停止，删除后无法撤销。\n不会删除当日日志文件或其他番茄钟记录。")) return false;
        if (Countdown.Current?.Id != sessionId) return false;
        var deletedSession = Countdown.Current!;
        StopAlarmRequested?.Invoke();
        await Countdown.DeleteCurrentRecordAsync();
        if (RecordDeleted is not null)
        {
            foreach (Func<TimerSession, Task> handler in RecordDeleted.GetInvocationList())
                await handler(deletedSession);
        }
        StatusMessage = "本次记录已删除";
        return true;
    }

    private void OnCompleted(TimerSession session)
    {
        StatusMessage = "";
        Refresh();
    }

    private void OnStorageError(string error) => ShowProblem("计时记录保存失败", error);
    private void OnDurationChanged()
    {
        OnPropertyChanged(nameof(DurationMinutes));
        if (Countdown.Current is null && Duration.TryGetDuration(out var duration)) RemainingText = DurationFormatter.Format(duration);
    }
    private void OnScheduleRestChanged()
    {
        if (ScheduleRest.TryGetDuration(out var rest))
        {
            ScheduleRestMinutes = rest.TotalMinutes.ToString(CultureInfo.InvariantCulture);
            if (Schedule is not null) Schedule.RestDuration = rest;
        }
    }
    public string ScheduleRestMinutes
    {
        get => _scheduleRestMinutes;
        set
        {
            if (!SetProperty(ref _scheduleRestMinutes, value)) return;
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes)
                && double.IsFinite(minutes) && minutes is >= 0 and <= 1440)
            {
                var duration = TimeSpan.FromMinutes(minutes);
                if (duration < TimeSpan.FromSeconds(.6)) duration = TimeSpan.Zero;
                if (Schedule is not null) Schedule.RestDuration = duration;
            }
        }
    }
    private void ReportError(Exception error) => ShowProblem("操作未完成", error.Message);
    private void OnPlannerChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(CalendarPageController.TodayPlans) or nameof(CalendarPageController.IsLoaded)
            or nameof(CalendarPageController.TodayLabel)) RefreshScheduleAvailability();
    }

    private void RefreshScheduleAvailability()
    {
        OnPropertyChanged(nameof(HasRunnablePlans));
        OnPropertyChanged(nameof(ShowScheduleEmptyState));
        StartCommand.NotifyCanExecuteChanged();
        AddSchedulePlanCommand.NotifyCanExecuteChanged();
    }
    private void ShowProblem(string title, string message)
    {
        StatusMessage = "";
        _interaction.ShowMessage(title, message);
    }

    private void Refresh()
    {
        var current = Countdown.Current;
        var remaining = current is null && Duration.TryGetDuration(out var selectedDuration) ? selectedDuration : Countdown.Remaining;
        RemainingText = DurationFormatter.Format(remaining);
        CurrentTaskTitle = current?.Title ?? "尚未开始";
        StatusText = current?.Status switch
        {
            SessionStatus.Running => "专注中",
            SessionStatus.Paused => "已暂停",
            SessionStatus.Completed => "已完成",
            SessionStatus.Cancelled => "已取消",
            SessionStatus.Interrupted => "已中断",
            SessionStatus.EndedEarly => "提前结束",
            _ => "待开始"
        };
        var planned = current?.PlannedSeconds ?? 0;
        Progress = planned <= 0 ? 0 : Math.Clamp((planned - remaining.TotalSeconds) / planned * 100, 0, 100);
        ProgressText = $"{Progress:0}%";
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsPaused));
        RefreshScheduleAvailability();
        PauseResumeCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        EndTaskCommand.NotifyCanExecuteChanged();
    }

    public async ValueTask DisposeAsync()
    {
        if (Schedule is not null) Schedule.Changed -= Refresh;
        ScheduleStartRequested = null;
        Countdown.Changed -= Refresh;
        Countdown.Completed -= OnCompleted;
        Countdown.StorageError -= OnStorageError;
        Duration.Changed -= OnDurationChanged;
        if (Planner is not null) Planner.PropertyChanged -= OnPlannerChanged;
        ScheduleRest.Changed -= OnScheduleRestChanged;
        WorkspaceChanged = null;
        FocusStarted = null;
        StopAlarmRequested = null;
        PreviewAlarmRequested = null;
        StartAlarmRequested = null;
        RecordDeleted = null;
        // Countdown is application-owned so that hiding or recreating this page
        // cannot terminate an active session. ApplicationMgr disposes it at exit.
        await Task.CompletedTask;
    }
}
