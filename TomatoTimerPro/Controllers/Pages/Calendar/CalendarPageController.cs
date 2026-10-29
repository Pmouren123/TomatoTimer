using System.Collections.Immutable;
using System.Globalization;
using TomatoTimerPro.Contracts.Interaction;
using TomatoTimerPro.Contracts.Storage;
using TomatoTimerPro.Contracts.Threading;
using TomatoTimerPro.Controllers.Components.Planning;
using TomatoTimerPro.Controllers.Components.Schedule;
using TomatoTimerPro.Infrastructure.Binding;
using TomatoTimerPro.Infrastructure.Commands;
using TomatoTimerPro.RuntimeStore.Abstractions;
using TomatoTimerPro.RuntimeStore.States.Planning;

namespace TomatoTimerPro.Controllers.Pages.Calendar;

public sealed record CalendarDay(DateOnly? Day, bool IsCurrentMonth, bool IsToday, bool IsSelected,
    int PlanCount, AsyncRelayCommand SelectCommand)
{
    public string Number => Day?.Day.ToString(CultureInfo.InvariantCulture) ?? "";
    public string PlanHint => PlanCount == 0 ? "" : $"{PlanCount} 项";
    public int PendingPlanCount { get; init; }
    public bool HasJournal { get; init; }
    public bool HasPendingPlans => PendingPlanCount > 0;
    public string Description => Day is { } day ? $"{day:yyyy-MM-dd}，{PlanCount} 项计划，未完成 {PendingPlanCount} 项" + (HasJournal ? "，有日志" : "") : "";
}

public sealed class PlanListItem : ObservableObject
{
    private PlanStatusChoice _selectedStatus;
    private readonly AsyncRelayCommand _changeStatus;
    public string Title { get; }
    public double DurationMinutes { get; }
    public bool HasPomodoro { get; }
    public string DurationText => HasPomodoro ? DurationMinutes.ToString("0.##", CultureInfo.InvariantCulture) + " 分钟" : "";
    public bool IsCompleted => _selectedStatus.Value == PlanStatus.Completed;
    public bool IsOnHold => _selectedStatus.Value == PlanStatus.OnHold;
    public bool IsAbandoned => _selectedStatus.Value == PlanStatus.Abandoned;
    public bool IsPending => _selectedStatus.Value == PlanStatus.Incomplete;
    public bool CanRestore => IsOnHold || IsAbandoned;
    public IReadOnlyList<PlanStatusChoice> StatusChoices => PlanStatusChoice.All;
    public PlanStatusChoice SelectedStatus
    {
        get => _selectedStatus;
        set
        {
            if (value is not null && SetProperty(ref _selectedStatus, value))
            {
                OnPropertyChanged(nameof(IsCompleted)); OnPropertyChanged(nameof(IsOnHold));
                OnPropertyChanged(nameof(IsAbandoned)); OnPropertyChanged(nameof(IsPending));
                StartFocusCommand.NotifyCanExecuteChanged();
                RestoreCommand.NotifyCanExecuteChanged(); RunLeisureCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanRestore));
                _changeStatus.Execute(null);
            }
        }
    }
    public AsyncRelayCommand EditCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand StartFocusCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }
    public AsyncRelayCommand RunLeisureCommand { get; }

    public PlanListItem(PlanEntry entry, Func<DateOnly, bool, Task> edit, Func<string, Task> delete,
        Func<string, PlanStatus, Task> changeStatus, Action<Exception> report,
        Func<string, Task> startFocus, Func<bool> canStartFocus,
        Func<string, Task>? restore = null, Func<string, Task>? runLeisure = null,
        Func<string, Task>? editSingle = null)
    {
        Title = entry.Title;
        DurationMinutes = entry.DurationMinutes;
        HasPomodoro = entry.HasPomodoro;
        _selectedStatus = PlanStatusChoice.All.Single(s => s.Value == entry.Status);
        _changeStatus = new AsyncRelayCommand(() => changeStatus(entry.Id, _selectedStatus.Value), report);
        EditCommand = new AsyncRelayCommand(() => entry.Status is PlanStatus.OnHold or PlanStatus.Abandoned && editSingle is not null
            ? editSingle(entry.Id) : edit(entry.Day, false), report);
        DeleteCommand = new AsyncRelayCommand(() => delete(entry.Id), report);
        StartFocusCommand = new AsyncRelayCommand(() => startFocus(entry.Id), report,
            () => HasPomodoro && IsPending && canStartFocus());
        RestoreCommand = new AsyncRelayCommand(() => restore?.Invoke(entry.Id) ?? Task.CompletedTask,
            report, () => CanRestore && canStartFocus());
        RunLeisureCommand = new AsyncRelayCommand(() => runLeisure?.Invoke(entry.Id) ?? Task.CompletedTask,
            report, () => IsOnHold && HasPomodoro && canStartFocus());
    }
}

/// <summary>Shared persisted plans; editing drafts belong exclusively to a dedicated modal controller.</summary>
public sealed class CalendarPageController : ObservableObject, IAsyncDisposable
{
    private static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh-CN");
    private readonly IRuntimeStore _store;
    private readonly IPlanStorage _storage;
    private readonly IUserInteraction _interaction;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<DateOnly> _today;
    private readonly IDisposable _subscription;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _heartbeat;
    private Task _cleanupTask = Task.CompletedTask;
    private string _selectedBox = "active";
    private DateOnly _currentDay;
    private DateOnly _selectedDate;
    private DateOnly _month;
    private string _statusMessage = "";
    private bool _loaded;
    private bool _busy;
    private bool _editing;
    private bool _disposed;
    private string _journalWorkspace = "";
    private long _journalGeneration;
    private HashSet<DateOnly> _journalDays = [];
    public bool SelectedHasJournal => _journalDays.Contains(_selectedDate);
    public AsyncRelayCommand OpenSelectedJournalCommand { get; }
    public event Func<DateOnly, Task>? JournalRequested;
    public IReadOnlyList<CalendarDay> Days { get; private set; } = [];
    public DayTimeAllocationController? TimeAllocation { get; set; }
    public IReadOnlyList<PlanListItem> TodayPlans { get; private set; } = [];
    public IReadOnlyList<PlanListItem> SelectedPlans { get; private set; } = [];
    public IReadOnlyList<PlanListItem> OnHoldPlans { get; private set; } = [];
    public IReadOnlyList<PlanListItem> AbandonedPlans { get; private set; } = [];
    public IReadOnlyList<PlanListItem> VisiblePlans => _selectedBox switch { "hold" => OnHoldPlans, "trash" => AbandonedPlans, _ => SelectedPlans };
    public bool IsActiveBox => _selectedBox == "active";
    public bool IsOnHoldBox => _selectedBox == "hold";
    public bool IsTrashBox => _selectedBox == "trash";
    public string OnHoldLabel => $"搁置箱 ({OnHoldPlans.Count})";
    public string TrashLabel => $"垃圾箱 ({AbandonedPlans.Count})";
    public string ListTitle => _selectedBox switch { "hold" => "搁置箱", "trash" => "垃圾箱", _ => "这一天的计划" };
    public string ListSubtitle => _selectedBox switch { "hold" => "随时恢复或休闲执行", "trash" => "当天可恢复，次日清空", _ => SelectedDateTitle };
    public string[] Weekdays { get; } = ["一", "二", "三", "四", "五", "六", "日"];
    public DateOnly SelectedDate => _selectedDate;
    public string MonthTitle => _month.ToString("yyyy 年 M 月", Chinese);
    public string SelectedDateTitle => _selectedDate.ToString("M 月 d 日 · dddd", Chinese);
    public string TodayLabel => _currentDay.ToString("M 月 d 日 · dddd", Chinese);
    public string TodayProgress => $"已完成 {TodayPlans.Count(p => p.IsCompleted)} / {TodayPlans.Count} 项";
    public string SelectedProgress => $"未完成 {SelectedPlans.Count(p => p.IsPending)} · 已完成 {SelectedPlans.Count(p => p.IsCompleted)}";
    public bool CanEdit => _loaded && !_busy && !_editing && !_disposed;
    public bool IsLoaded => _loaded;
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public AsyncRelayCommand PreviousMonthCommand { get; }
    public AsyncRelayCommand NextMonthCommand { get; }
    public AsyncRelayCommand TodayCommand { get; }
    public AsyncRelayCommand EditTodayCommand { get; }
    public AsyncRelayCommand EditSelectedCommand { get; }
    public AsyncRelayCommand AddPlanCommand { get; }
    public AsyncRelayCommand AddTodayPlanCommand { get; }
    public AsyncRelayCommand ReloadCommand { get; }
    public RelayCommand ShowActiveCommand { get; }
    public RelayCommand ShowOnHoldCommand { get; }
    public RelayCommand ShowTrashCommand { get; }
    public event Func<PlanEntry, Task>? FocusRequested;
    public event Func<PlanEntry, Task>? LeisureFocusRequested;

    public CalendarPageController(IRuntimeStore store, IPlanStorage storage, IUserInteraction interaction,
        IUiDispatcher dispatcher, Func<DateOnly>? today = null)
    {
        _store = store; _storage = storage; _interaction = interaction; _dispatcher = dispatcher;
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Now));
        _currentDay = _selectedDate = _today();
        _month = new DateOnly(_selectedDate.Year, _selectedDate.Month, 1);
        try { _ = store.Get<PlanState>(); } catch (KeyNotFoundException) { store.Set(new PlanState()); }
        PreviousMonthCommand = new AsyncRelayCommand(() => MoveMonthAsync(-1), ReportError, () => CanEdit && _month > DateOnly.MinValue);
        NextMonthCommand = new AsyncRelayCommand(() => MoveMonthAsync(1), ReportError, () => CanEdit && (_month.Year < 9999 || _month.Month < 12));
        TodayCommand = new AsyncRelayCommand(async () => { await SelectDateAsync(_today()); }, ReportError, () => CanEdit);
        EditTodayCommand = new AsyncRelayCommand(() => EditDateAsync(_today()), ReportError, () => CanEdit);
        EditSelectedCommand = new AsyncRelayCommand(() => EditDateAsync(_selectedDate), ReportError, () => CanEdit);
        AddPlanCommand = new AsyncRelayCommand(() => EditDateAsync(_selectedDate, true), ReportError, () => CanEdit);
        AddTodayPlanCommand = new AsyncRelayCommand(() => EditDateAsync(_today(), true), ReportError, () => CanEdit);
        ReloadCommand = new AsyncRelayCommand(InitializeAsync, ReportError, () => !_busy && !_loaded && !_disposed);
        ShowActiveCommand = new RelayCommand(() => SelectBox("active"));
        ShowOnHoldCommand = new RelayCommand(() => SelectBox("hold"));
        ShowTrashCommand = new RelayCommand(() => SelectBox("trash"));
        OpenSelectedJournalCommand = new AsyncRelayCommand(async () =>
        {
            if (JournalRequested is { } requested)
                foreach (Func<DateOnly, Task> handler in requested.GetInvocationList()) await handler(_selectedDate);
        }, ReportError, () => SelectedHasJournal && !_disposed);
        _subscription = store.Subscribe<PlanState>(_ => _dispatcher.Invoke(RefreshPresentation));
        RefreshPresentation();
    }

    public async Task InitializeAsync()
    {
        await _operations.WaitAsync();
        try
        {
            if (_loaded || _disposed) return;
            SetBusy(true);
            _store.Set(await _storage.LoadPlansAsync());
            _loaded = true;
            StatusMessage = "";
            await ClearExpiredTrashCoreAsync();
            _heartbeat ??= WatchDayAsync();
        }
        catch (Exception error) { ReportError(error); }
        finally { SetBusy(false); _operations.Release(); }
    }

    public async Task<bool> SelectDateAsync(DateOnly day)
    {
        await _operations.WaitAsync();
        try
        {
            if (_disposed || _editing) return false;
            _selectedDate = day;
            _month = new DateOnly(day.Year, day.Month, 1);
            _selectedBox = "active";
            RefreshToday();
            RefreshPresentation();
            SetBusy(false);
            return true;
        }
        finally { _operations.Release(); }
    }

    private Task MoveMonthAsync(int offset)
    {
        var month = _month.AddMonths(offset);
        return SelectDateAsync(new DateOnly(month.Year, month.Month, Math.Min(_selectedDate.Day, DateTime.DaysInMonth(month.Year, month.Month))));
    }

    public async Task EditDateAsync(DateOnly day, bool addNew = false)
    {
        if (!CanEdit || !await SelectDateAsync(day)) return;
        _editing = true;
        SetBusy(false);
        try
        {
            var entries = _store.Get<PlanState>().Entries.Where(p => p.Day == day && p.Status is PlanStatus.Incomplete or PlanStatus.Completed).ToArray();
            await _interaction.EditPlansAsync(day, entries, addNew, plans => SaveDayAsync(day, plans));
        }
        finally { _editing = false; SetBusy(false); }
    }

    public async Task<string?> SaveDayAsync(DateOnly day, IReadOnlyList<PlanEntry> entries)
    {
        await _operations.WaitAsync();
        try
        {
            SetBusy(true);
            if (!_loaded || _disposed) return "计划数据尚未读取成功，请重试读取后再编辑。";
            if (entries.Any(p => p.Day != day || string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Title) || !Enum.IsDefined(p.Status)
                || (p.HasPomodoro && (!double.IsFinite(p.DurationMinutes) || p.DurationMinutes is < .01 or > 1440)))
                || entries.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != entries.Count)
                return "请填写计划内容和有效状态；番茄钟时长须为 0.01 至 1440 分钟。";
            var old = _store.Get<PlanState>();
            if (old.Entries.Any(p => p.Day != day && entries.Any(e => e.Id == p.Id)))
                return "计划标识与其他日期重复，请重新打开编辑面板。";
            var priorById = old.Entries.ToDictionary(p => p.Id, StringComparer.Ordinal);
            return await CommitAsync(old with { Entries = old.Entries.Where(p => p.Day != day
                || ((p.Status is PlanStatus.OnHold or PlanStatus.Abandoned) && !entries.Any(e => e.Id == p.Id)))
                .Concat(entries.Select(p => p with { Title = p.Title.Trim(),
                    AbandonedOn = p.Status == PlanStatus.Abandoned
                        ? priorById.TryGetValue(p.Id, out var prior) && prior.Status == PlanStatus.Abandoned
                            ? prior.AbandonedOn ?? prior.Day : _today()
                        : null,
                    DurationMinutes = !p.HasPomodoro
                    && (!double.IsFinite(p.DurationMinutes) || p.DurationMinutes is < .01 or > 1440) ? 25 : p.DurationMinutes })).ToImmutableArray() }, "计划已保存。");
        }
        finally { SetBusy(false); _operations.Release(); }
    }

    public async Task EditPlanAsync(string id)
    {
        if (!CanEdit) return;
        var entry = _store.Get<PlanState>().Entries.FirstOrDefault(p => p.Id == id);
        if (entry is null) return;
        _editing = true;
        SetBusy(false);
        try
        {
            await _interaction.EditPlansAsync(entry.Day, new[] { entry }, false,
                edited => SaveSinglePlanAsync(entry, edited));
        }
        finally { _editing = false; SetBusy(false); }
    }

    private async Task<string?> SaveSinglePlanAsync(PlanEntry original, IReadOnlyList<PlanEntry> entries)
    {
        await _operations.WaitAsync();
        try
        {
            SetBusy(true);
            if (!_loaded || _disposed) return "计划尚未就绪。";
            if (entries.Any(p => p.Day != original.Day || string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Title)
                || !Enum.IsDefined(p.Status) || (p.HasPomodoro && (!double.IsFinite(p.DurationMinutes) || p.DurationMinutes is < .01 or > 1440)))
                || entries.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != entries.Count)
                return "请填写有效计划内容、状态与番茄钟时长。";
            var old = _store.Get<PlanState>();
            if (!old.Entries.Any(p => p.Id == original.Id)) return "这项计划已不存在，请重新打开。";
            if (entries.Any(p => p.Id != original.Id && old.Entries.Any(existing => existing.Id == p.Id)))
                return "计划标识重复，请重新打开编辑面板。";
            var normalized = entries.Select(p => p with
            {
                Title = p.Title.Trim(),
                AbandonedOn = p.Status == PlanStatus.Abandoned
                    ? original.Status == PlanStatus.Abandoned && p.Id == original.Id ? original.AbandonedOn ?? original.Day : _today()
                    : null,
                Day = p.Id == original.Id && p.Status == PlanStatus.Incomplete
                    && (original.Status is PlanStatus.OnHold or PlanStatus.Abandoned) ? _today() : p.Day,
                DurationMinutes = !p.HasPomodoro && (!double.IsFinite(p.DurationMinutes) || p.DurationMinutes is < .01 or > 1440) ? 25 : p.DurationMinutes
            });
            return await CommitAsync(old with { Entries = old.Entries.Where(p => p.Id != original.Id).Concat(normalized).ToImmutableArray() }, "计划已保存。");
        }
        finally { SetBusy(false); _operations.Release(); }
    }

    public async Task ChangeStatusAsync(string id, PlanStatus status)
    {
        await _operations.WaitAsync();
        try
        {
            SetBusy(true);
            if (!_loaded || _disposed || _editing || !Enum.IsDefined(status)) return;
            var old = _store.Get<PlanState>();
            if (!old.Entries.Any(p => p.Id == id)) return;
            await CommitAsync(old with { Entries = old.Entries.Select(p => p.Id == id ? p with { Status = status,
                Day = status == PlanStatus.Incomplete && p.Status is PlanStatus.OnHold or PlanStatus.Abandoned ? _today() : p.Day,
                AbandonedOn = status == PlanStatus.Abandoned ? p.Status == PlanStatus.Abandoned ? p.AbandonedOn ?? p.Day : _today() : null } : p).ToImmutableArray() },
                "计划状态已保存。");
        }
        finally { RefreshPresentation(); SetBusy(false); _operations.Release(); }
    }

    public async Task StartFocusAsync(string id)
    {
        if (!CanEdit) return;
        var plan = _store.Get<PlanState>().Entries.FirstOrDefault(p => p.Id == id);
        if (plan is null) { StatusMessage = "这项计划已不存在，请重新选择。"; return; }
        if (!plan.HasPomodoro || plan.Status != PlanStatus.Incomplete)
        { StatusMessage = "仅未完成且已设置番茄钟的计划可以开始专注。"; return; }
        var handlers = FocusRequested;
        if (handlers is null) { StatusMessage = "专注模块尚未就绪，请重试。"; return; }
        foreach (Func<PlanEntry, Task> handler in handlers.GetInvocationList()) await handler(plan);
    }

    public async Task RestoreAsync(string id)
    {
        await _operations.WaitAsync();
        try
        {
            SetBusy(true);
            if (!_loaded || _disposed || _editing) return;
            var old = _store.Get<PlanState>();
            var plan = old.Entries.FirstOrDefault(p => p.Id == id);
            if (plan is null || plan.Status is not (PlanStatus.OnHold or PlanStatus.Abandoned)) return;
            if (plan.Status == PlanStatus.Abandoned && (plan.AbandonedOn ?? plan.Day) < _today())
            { await ClearExpiredTrashCoreAsync(); return; }
            await CommitAsync(old with { Entries = old.Entries.Select(p => p.Id == id
                ? p with { Status = PlanStatus.Incomplete, Day = _today(), AbandonedOn = null } : p).ToImmutableArray() }, "已加入今天日程");
        }
        finally { SetBusy(false); _operations.Release(); }
    }

    public async Task RunLeisureAsync(string id)
    {
        if (!CanEdit) return;
        var plan = _store.Get<PlanState>().Entries.FirstOrDefault(p => p.Id == id);
        if (plan is null || plan.Status != PlanStatus.OnHold || !plan.HasPomodoro) return;
        if (LeisureFocusRequested is not { } handlers) { StatusMessage = "休闲计时尚未就绪。"; return; }
        foreach (Func<PlanEntry, Task> handler in handlers.GetInvocationList()) await handler(plan);
    }

    private void SelectBox(string box)
    {
        _selectedBox = box;
        NotifyBox();
    }

    private void NotifyBox()
    {
        foreach (var name in new[] { nameof(VisiblePlans), nameof(IsActiveBox), nameof(IsOnHoldBox), nameof(IsTrashBox),
            nameof(OnHoldLabel), nameof(TrashLabel), nameof(ListTitle), nameof(ListSubtitle) }) OnPropertyChanged(name);
    }

    public async Task DeleteAsync(string id)
    {
        await _operations.WaitAsync();
        try
        {
            SetBusy(true);
            if (!_loaded || _disposed || _editing) return;
            var old = _store.Get<PlanState>();
            var plan = old.Entries.FirstOrDefault(p => p.Id == id);
            if (plan is null || !await _interaction.ConfirmAsync("删除计划", $"确定删除「{plan.Title}」吗？其他计划会保留。")) return;
            await CommitAsync(old with { Entries = old.Entries.Where(p => p.Id != id).ToImmutableArray() }, "计划已删除。");
        }
        finally { SetBusy(false); _operations.Release(); }
    }

    private async Task<string?> CommitAsync(PlanState next, string success)
    {
        try
        {
            await _storage.SavePlansAsync(next);
            _store.Set(next);
            StatusMessage = success;
            return null;
        }
        catch (Exception error)
        {
            ReportError(error);
            return StatusMessage;
        }
    }

    public void RefreshToday()
    {
        if (_disposed) return;
        if (_currentDay != _today()) { _currentDay = _today(); RefreshPresentation(); }
        _cleanupTask = RefreshTodayAsync();
    }

    public async Task RefreshTodayAsync()
    {
        await _operations.WaitAsync();
        try
        {
            if (_disposed || !_loaded) return;
            _currentDay = _today();
            await ClearExpiredTrashCoreAsync();
            RefreshPresentation();
        }
        catch (Exception error) { ReportError(error); }
        finally { _operations.Release(); }
    }

    private async Task ClearExpiredTrashCoreAsync()
    {
        var old = _store.Get<PlanState>();
        var retained = old.Entries.Where(p => p.Status != PlanStatus.Abandoned || (p.AbandonedOn ?? p.Day) >= _today()).ToImmutableArray();
        if (retained.Length != old.Entries.Length) await CommitAsync(old with { Entries = retained }, "");
    }

    private async Task WatchDayAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
                await _dispatcher.InvokeAsync(() => { if (!_disposed) RefreshToday(); });
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private void RefreshPresentation()
    {
        var entries = _store.Get<PlanState>().Entries;
        PlanListItem Item(PlanEntry p) => new(p, EditDateAsync, DeleteAsync, ChangeStatusAsync, ReportError,
            StartFocusAsync, () => CanEdit, RestoreAsync, RunLeisureAsync, EditPlanAsync);
        var active = entries.Where(p => p.Status is PlanStatus.Incomplete or PlanStatus.Completed).ToArray();
        TodayPlans = active.Where(p => p.Day == _currentDay).Select(Item).ToArray();
        SelectedPlans = active.Where(p => p.Day == _selectedDate).Select(Item).ToArray();
        OnHoldPlans = entries.Where(p => p.Status == PlanStatus.OnHold).Select(Item).ToArray();
        AbandonedPlans = entries.Where(p => p.Status == PlanStatus.Abandoned).Select(Item).ToArray();
        var counts = active.GroupBy(p => p.Day).ToDictionary(g => g.Key, g => g.Count());
        var pendingCounts = entries.Where(p => p.Status == PlanStatus.Incomplete).GroupBy(p => p.Day)
            .ToDictionary(g => g.Key, g => g.Count());
        var offset = ((int)_month.DayOfWeek + 6) % 7;
        Days = Enumerable.Range(0, 42).Select(index =>
        {
            var number = _month.DayNumber - offset + index;
            DateOnly? day = number >= DateOnly.MinValue.DayNumber && number <= DateOnly.MaxValue.DayNumber ? DateOnly.FromDayNumber(number) : null;
            return new CalendarDay(day, day?.Month == _month.Month, day == _currentDay, day == _selectedDate,
                day is { } date ? counts.GetValueOrDefault(date) : 0,
                new AsyncRelayCommand(async () => { if (day is { } selected) await SelectDateAsync(selected); }, ReportError, () => day is not null && CanEdit))
                { PendingPlanCount = day is { } pendingDay ? pendingCounts.GetValueOrDefault(pendingDay) : 0,
                    HasJournal = day is { } journalDay && _journalDays.Contains(journalDay) };
        }).ToArray();
        foreach (var name in new[] { nameof(Days), nameof(MonthTitle), nameof(SelectedDate), nameof(SelectedDateTitle), nameof(TodayPlans), nameof(SelectedPlans), nameof(TodayLabel), nameof(TodayProgress), nameof(SelectedProgress) })
            OnPropertyChanged(name);
        OnPropertyChanged(nameof(OnHoldPlans)); OnPropertyChanged(nameof(AbandonedPlans)); NotifyBox();
        OnPropertyChanged(nameof(SelectedHasJournal));
        OpenSelectedJournalCommand.NotifyCanExecuteChanged();
    }

    public async Task SetJournalWorkspaceAsync(string? workspace, IAppStorage storage)
    {
        var path = string.IsNullOrWhiteSpace(workspace) ? "" : Path.GetFullPath(workspace);
        var request = ++_journalGeneration;
        if (!StringComparer.OrdinalIgnoreCase.Equals(path, _journalWorkspace))
        {
            _journalWorkspace = path;
            _journalDays.Clear();
        }
        try
        {
            var history = path.Length == 0 ? [] : await storage.LoadJournalHistoryAsync(path);
            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed || request != _journalGeneration) return;
                _journalDays = history.Select(entry => entry.Day).ToHashSet();
                RefreshPresentation();
            });
        }
        catch (Exception error)
        {
            if (!_disposed && request == _journalGeneration)
                StatusMessage = "日志索引读取失败：" + error.Message;
        }
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(IsLoaded));
        PreviousMonthCommand.NotifyCanExecuteChanged(); NextMonthCommand.NotifyCanExecuteChanged();
        TodayCommand.NotifyCanExecuteChanged(); EditTodayCommand.NotifyCanExecuteChanged(); ReloadCommand.NotifyCanExecuteChanged();
        EditSelectedCommand.NotifyCanExecuteChanged(); AddPlanCommand.NotifyCanExecuteChanged();
        AddTodayPlanCommand.NotifyCanExecuteChanged();
        foreach (var day in Days) day.SelectCommand.NotifyCanExecuteChanged();
        foreach (var plan in TodayPlans.Concat(SelectedPlans).Concat(OnHoldPlans).Concat(AbandonedPlans))
        { plan.StartFocusCommand.NotifyCanExecuteChanged(); plan.RestoreCommand.NotifyCanExecuteChanged(); plan.RunLeisureCommand.NotifyCanExecuteChanged(); }
    }

    private void ReportError(Exception error) => StatusMessage = "计划操作未完成，内容仍保留，请重试：" + error.Message;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        if (_heartbeat is not null) await _heartbeat;
        await _cleanupTask;
        await _operations.WaitAsync();
        try { _subscription.Dispose(); FocusRequested = null; LeisureFocusRequested = null; JournalRequested = null; _journalGeneration++; } finally { _operations.Release(); }
        _lifetime.Dispose();
    }
}
