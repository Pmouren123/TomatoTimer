using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using TomatoTimerPro.Contracts.Interaction;
using TomatoTimerPro.Infrastructure.Binding;
using TomatoTimerPro.Infrastructure.Commands;
using TomatoTimerPro.RuntimeStore.States.Planning;

namespace TomatoTimerPro.Controllers.Components.Planning;

public sealed class EditablePlan : ObservableObject
{
    private string _title;
    private PlanStatusChoice _status;
    private string _durationMinutes;
    private bool _hasPomodoro;
    private double _lastValidDurationMinutes;
    public string Id { get; }
    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public string DurationMinutes
    {
        get => _durationMinutes;
        set
        {
            if (!SetProperty(ref _durationMinutes, value)) return;
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes)
                && double.IsFinite(minutes) && minutes is >= .01 and <= 1440) _lastValidDurationMinutes = minutes;
        }
    }
    public bool HasPomodoro
    {
        get => _hasPomodoro;
        set
        {
            if (!SetProperty(ref _hasPomodoro, value)) return;
            AddPomodoroCommand.NotifyCanExecuteChanged(); RemovePomodoroCommand.NotifyCanExecuteChanged();
        }
    }
    internal double DurationForSave => _lastValidDurationMinutes;
    public IReadOnlyList<PlanStatusChoice> StatusChoices => PlanStatusChoice.All;
    public PlanStatusChoice SelectedStatus { get => _status; set { if (value is not null) SetProperty(ref _status, value); } }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand AddPomodoroCommand { get; }
    public RelayCommand RemovePomodoroCommand { get; }
    public RelayCommand ShortFocusCommand { get; }
    public RelayCommand StandardFocusCommand { get; }
    public RelayCommand LongFocusCommand { get; }

    public EditablePlan(PlanEntry entry, Action<EditablePlan> remove)
    {
        Id = entry.Id; _title = entry.Title; _status = PlanStatusChoice.All.Single(s => s.Value == entry.Status);
        _durationMinutes = entry.DurationMinutes.ToString("0.################", CultureInfo.InvariantCulture);
        _hasPomodoro = entry.HasPomodoro;
        _lastValidDurationMinutes = double.IsFinite(entry.DurationMinutes) && entry.DurationMinutes is >= .01 and <= 1440
            ? entry.DurationMinutes : 25;
        DeleteCommand = new RelayCommand(() => remove(this));
        AddPomodoroCommand = new RelayCommand(() => HasPomodoro = true, () => !HasPomodoro);
        RemovePomodoroCommand = new RelayCommand(() => HasPomodoro = false, () => HasPomodoro);
        ShortFocusCommand = new RelayCommand(() => DurationMinutes = "15");
        StandardFocusCommand = new RelayCommand(() => DurationMinutes = "25");
        LongFocusCommand = new RelayCommand(() => DurationMinutes = "45");
    }
}

/// <summary>Private dialog drafts. The shared store changes only after a successful save.</summary>
public sealed class PlanEditorController : ObservableObject, IDisposable
{
    private readonly DateOnly _day;
    private readonly PlanEntry[] _original;
    private readonly IUserInteraction _interaction;
    private readonly Func<IReadOnlyList<PlanEntry>, Task<string?>> _save;
    private bool _busy;
    private bool _closed;
    private string _status = "";
    public ObservableCollection<EditablePlan> Plans { get; } = [];
    public string EditorTitle { get; }
    public string DateTitle => _day.ToString("yyyy 年 M 月 d 日 · dddd", CultureInfo.GetCultureInfo("zh-CN"));
    public string CountText => $"共 {Plans.Count} 项计划";
    public string StatusMessage { get => _status; private set => SetProperty(ref _status, value); }
    public bool CanEdit => !_busy && !_closed;
    public bool IsDirty => !_original.Select(p => (p.Id, p.Title, p.Status,
        Duration: p.DurationMinutes.ToString("0.################", CultureInfo.InvariantCulture), p.HasPomodoro))
        .SequenceEqual(Plans.Select(p => (p.Id, p.Title, p.SelectedStatus.Value, Duration: p.DurationMinutes, p.HasPomodoro)));
    public RelayCommand AddCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand CancelCommand { get; }
    public event Action? CloseRequested;

    public PlanEditorController(DateOnly day, IReadOnlyList<PlanEntry> plans, bool addNew,
        IUserInteraction interaction, Func<IReadOnlyList<PlanEntry>, Task<string?>> save)
    {
        _day = day; _original = plans.ToArray(); _interaction = interaction; _save = save;
        EditorTitle = addNew || plans.Count == 0 ? "写下新的计划" : "整理这一天的计划";
        AddCommand = new RelayCommand(AddPlan, () => CanEdit);
        SaveCommand = new AsyncRelayCommand(async () => { await SaveAsync(); }, ReportError, () => CanEdit);
        CancelCommand = new AsyncRelayCommand(async () => { await RequestCloseAsync(); }, ReportError, () => CanEdit);
        Plans.CollectionChanged += OnCollectionChanged;
        foreach (var plan in plans) Plans.Add(new EditablePlan(plan, RemovePlan));
        if (addNew || Plans.Count == 0) AddPlan();
    }

    private void AddPlan() => Plans.Add(new EditablePlan(new PlanEntry(Guid.NewGuid().ToString("N"), _day, "", HasPomodoro: false), RemovePlan));
    private void RemovePlan(EditablePlan plan) { if (CanEdit) Plans.Remove(plan); }
    private PlanEntry[] Snapshot() => Plans.Select(p => new PlanEntry(p.Id, _day, p.Title, p.SelectedStatus.Value,
        p.DurationForSave, p.HasPomodoro)).ToArray();

    public async Task<bool> SaveAsync()
    {
        if (!CanEdit) return false;
        var empty = Plans.Select((plan, index) => (plan, index)).FirstOrDefault(p => string.IsNullOrWhiteSpace(p.plan.Title));
        if (empty.plan is not null)
        {
            StatusMessage = $"第 {empty.index + 1} 项计划还没有内容，请填写或删除这一项。";
            return false;
        }
        var invalidDuration = Plans.Select((plan, index) => (plan, index)).FirstOrDefault(p => p.plan.HasPomodoro &&
            (!double.TryParse(p.plan.DurationMinutes, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes)
            || !double.IsFinite(minutes) || minutes is < .01 or > 1440));
        if (invalidDuration.plan is not null)
        {
            StatusMessage = $"第 {invalidDuration.index + 1} 项计划的专注时长须为 0.01 至 1440 分钟。";
            return false;
        }
        SetBusy(true);
        try
        {
            var entries = Snapshot().Select(p => p with { Title = p.Title.Trim() }).ToArray();
            var error = await _save(entries);
            if (error is not null) { StatusMessage = error; return false; }
            Close();
            return true;
        }
        catch (Exception error) { ReportError(error); return false; }
        finally { SetBusy(false); }
    }

    public async Task<bool> RequestCloseAsync()
    {
        if (_closed) return true;
        if (_busy) return false;
        if (!IsDirty) { Close(); return true; }
        SetBusy(true);
        SaveDecision decision;
        try { decision = await _interaction.ResolveUnsavedAsync("计划尚未保存", "编辑面板里有未保存的计划，是否保存后关闭？"); }
        catch (Exception error) { ReportError(error); return false; }
        finally { SetBusy(false); }
        if (decision == SaveDecision.Cancel) return false;
        if (decision == SaveDecision.Save) return await SaveAsync();
        Close();
        return true;
    }

    private void Close() { _closed = true; CloseRequested?.Invoke(); }
    private void SetBusy(bool value)
    {
        _busy = value;
        OnPropertyChanged(nameof(CanEdit));
        AddCommand.NotifyCanExecuteChanged(); SaveCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
    }
    private void ReportError(Exception error) => StatusMessage = "计划未能保存，编辑内容已保留：" + error.Message;
    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.OldItems is not null) foreach (EditablePlan plan in args.OldItems) plan.PropertyChanged -= OnPlanChanged;
        if (args.NewItems is not null) foreach (EditablePlan plan in args.NewItems) plan.PropertyChanged += OnPlanChanged;
        OnPropertyChanged(nameof(CountText)); OnPropertyChanged(nameof(IsDirty));
    }
    private void OnPlanChanged(object? sender, PropertyChangedEventArgs args) => OnPropertyChanged(nameof(IsDirty));
    public void Dispose()
    {
        Plans.CollectionChanged -= OnCollectionChanged;
        foreach (var plan in Plans) plan.PropertyChanged -= OnPlanChanged;
        CloseRequested = null;
    }
}
