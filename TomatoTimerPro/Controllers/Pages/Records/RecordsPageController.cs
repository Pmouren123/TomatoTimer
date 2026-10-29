using System.Collections.ObjectModel;
using TomatoTimerPro.Contracts.Interaction;
using TomatoTimerPro.Contracts.Navigation;
using TomatoTimerPro.Contracts.Storage;
using TomatoTimerPro.Infrastructure.Binding;
using TomatoTimerPro.Infrastructure.Commands;
using TomatoTimerPro.RuntimeStore.States.Timer;
using TomatoTimerPro.Tools.Time;

namespace TomatoTimerPro.Controllers.Pages.Records;

public sealed record SessionRow(TimerSession Session, bool HasReflection = false)
{
    public string Title => Session.Title;
    public string TimeText => Session.StartedAt.ToLocalTime().ToString("HH:mm");
    public string DurationText => DurationFormatter.Format(TimeSpan.FromSeconds(Session.FocusedSeconds));
    public string HasReflectionText => "";
    public string StatusText => Session.Status switch
    { SessionStatus.Completed => "已完成", SessionStatus.EndedEarly => "提前结束", SessionStatus.Cancelled => "已放弃", SessionStatus.Interrupted => "已中断", SessionStatus.Paused => "已暂停", _ => "专注中" };
}

public sealed class RecordsPageController : ObservableObject, IAsyncDisposable
{
    private readonly IAppStorage _storage;
    private readonly IUserInteraction _interaction;
    private readonly IPageNavigator? _navigator;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private DocumentSnapshot? _document;
    private string _baseline = "";
    private RecoveryDraft? _openedDraft;
    private RecoveryDraft? _selectedDraft;
    private JournalEntry? _selectedHistory;
    private bool _historyVisible = true;
    private string _workspace = "";
    private DateTime? _date = DateTime.Today;
    private SessionRow? _selected;
    private string _text = "";
    private string _title = "每日日志";
    private string _saveStatus = "请选择工作区";
    private string _status = "";
    private bool _loading;
    private bool _busy;
    private bool _disposed;
    private bool _dateChooserVisible;

    public event Action<string>? WorkspaceChanged;
    public event Action? JournalSaved;
    public event Func<TimerSession, Task>? DeleteSessionRequested;
    public ObservableCollection<SessionRow> Sessions { get; } = [];
    public ObservableCollection<JournalEntry> History { get; } = [];

    public JournalEntry? SelectedHistory
    {
        get => _selectedHistory;
        set
        {
            if (_loading) return;
            if (IsBusy || value is null) { OnPropertyChanged(); return; }
            if (value != _selectedHistory) _ = GuardAsync(() => ChangeContextAsync(_workspace, value.Day.ToDateTime(TimeOnly.MinValue)));
        }
    }

    public bool HistoryVisible
    {
        get => _historyVisible;
        private set { if (SetProperty(ref _historyVisible, value)) OnPropertyChanged(nameof(SessionsVisible)); }
    }
    public bool SessionsVisible => !HistoryVisible;
    public ObservableCollection<RecoveryDraft> RecoveryDrafts { get; } = [];
    public RecoveryDraft? SelectedRecoveryDraft { get => _selectedDraft; set => SetProperty(ref _selectedDraft, value); }
    public string WorkspacePath => _workspace;
    public string WorkspaceName => string.IsNullOrEmpty(_workspace) ? "未选择工作区" : new DirectoryInfo(_workspace).Name;

    public DateTime? SelectedDate
    {
        get => _date;
        set
        {
            if (_loading) return;
            if (IsBusy) { OnPropertyChanged(); return; }
            if (value is not null && value.Value.Date != _date?.Date)
                _ = GuardAsync(() => ChangeContextAsync(_workspace, value.Value.Date));
        }
    }

    public SessionRow? SelectedSession
    {
        get => _selected;
        set
        {
            if (_loading) return;
            if (IsBusy) { OnPropertyChanged(); return; }
            if (value != _selected)
            {
                _selected = value;
                NotifySelection();
                NotifyEditorState();
            }
        }
    }

    public string EditorText
    {
        get => _text;
        set
        {
            if (!_loading && (IsBusy || _disposed)) { OnPropertyChanged(); return; }
            if (SetProperty(ref _text, value) && !_loading)
            {
                SaveStatus = IsDirty ? "有未保存的修改" : "已保存";
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    public string EditorTitle { get => _title; private set => SetProperty(ref _title, value); }
    public string SaveStatus { get => _saveStatus; private set => SetProperty(ref _saveStatus, value); }
    public string StatusMessage { get => _status; private set => SetProperty(ref _status, value); }
    public bool DateChooserVisible { get => _dateChooserVisible; private set => SetProperty(ref _dateChooserVisible, value); }
    public bool IsDailyMode => true;
    public bool IsBusy { get => _busy; private set { if (SetProperty(ref _busy, value)) OnPropertyChanged(nameof(CanEdit)); } }
    public bool HasLogEditor => _document is not null;
    public bool HasSelectedSession => _selected is not null;
    public bool CanEdit => HasLogEditor && !IsBusy;
    public bool IsDirty => _document is not null && !StringComparer.Ordinal.Equals(_text, _baseline);
    public string DailyContextText => $"{_date:yyyy年MM月dd日} · 工作区日志";
    public string SelectedSessionSummary => _selected is null ? "选择计时记录可查看统计。" : $"{_selected.Title} · {_selected.StatusText} · {_selected.TimeText} · 专注 {_selected.DurationText}";

    // Compatibility aliases are intentionally inert: records now contain timing data only.
    public bool HasReflectionEditor => HasLogEditor;
    public bool IsReflectionMode => false;
    public bool CanOpenReflection => false;
    public bool NeedsReflection => false;
    public string ReflectionActionText => "";

    public AsyncRelayCommand SelectWorkspaceCommand { get; }
    public AsyncRelayCommand EditDailyCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand SaveAsCommand { get; }
    public AsyncRelayCommand OpenFolderCommand { get; }
    public AsyncRelayCommand RestoreDraftCommand { get; }
    public AsyncRelayCommand ReturnToTimelineCommand { get; }
    public AsyncRelayCommand DiscardAndLeaveCommand { get; }
    public AsyncRelayCommand DeleteRecordCommand { get; }
    public AsyncRelayCommand AddReflectionCommand { get; }
    public AsyncRelayCommand ShowHistoryCommand { get; }
    public AsyncRelayCommand ShowSessionsCommand { get; }

    public RecordsPageController(IAppStorage storage, IUserInteraction interaction, IPageNavigator? navigator = null)
    {
        _storage = storage;
        _interaction = interaction;
        _navigator = navigator;
        SelectWorkspaceCommand = new AsyncRelayCommand(async () =>
        {
            var path = await interaction.PickFolderAsync();
            if (path is not null) await ChangeContextAsync(path, _date ?? DateTime.Today);
        }, OnError);
        EditDailyCommand = new AsyncRelayCommand(SwitchDocumentAsync, OnError);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, OnError);
        SaveCommand = new AsyncRelayCommand(async () => { await SaveAsync(); }, OnError);
        SaveAsCommand = new AsyncRelayCommand(SaveAsAsync, OnError);
        OpenFolderCommand = new AsyncRelayCommand(() => RunAsync(async () =>
        {
            if (string.IsNullOrEmpty(_workspace)) return;
            var directory = await storage.PrepareDayAsync(_workspace, DateOnly.FromDateTime(_date ?? DateTime.Today));
            interaction.OpenFolder(directory);
        }), OnError);
        RestoreDraftCommand = new AsyncRelayCommand(RestoreSelectedDraftAsync, OnError);
        ReturnToTimelineCommand = new AsyncRelayCommand(ReturnToTimelineAsync, OnError);
        DiscardAndLeaveCommand = new AsyncRelayCommand(DiscardAndLeaveAsync, OnError);
        DeleteRecordCommand = new AsyncRelayCommand(DeleteSelectedRecordAsync, OnError);
        AddReflectionCommand = new AsyncRelayCommand(() => Task.CompletedTask, OnError, () => false);
        ShowHistoryCommand = new AsyncRelayCommand(() => RunAsync(async () =>
        { await RefreshJournalHistoryCoreAsync(); HistoryVisible = true; }), OnError);
        ShowSessionsCommand = new AsyncRelayCommand(() => RunAsync(() =>
        { HistoryVisible = false; return Task.CompletedTask; }), OnError);
    }

    public async Task DiscardAndLeaveAsync()
    {
        var leave = await RunAsync(async () =>
        {
            if (IsDirty && !await _interaction.ConfirmAsync("放弃本次编辑", "放弃当前未保存的文字并返回计时页？已保存的历史日志不会被删除。")) return false;
            ResetEditorToBaseline();
            _openedDraft = null;
            StatusMessage = "已退出本次编辑，历史日志保留。";
            return true;
        });
        if (leave && _navigator is not null) await _navigator.NavigateAsync("timer");
    }

    public Task ReturnToTimelineAsync() => RunAsync(async () =>
    {
        if (!await CanLeaveCoreAsync()) return;
        await RefreshJournalHistoryCoreAsync();
        DateChooserVisible = true;
        StatusMessage = "已返回日志时间线。";
    });

    public Task DeleteSelectedRecordAsync() => RunAsync(async () =>
    {
        var session = _selected?.Session;
        if (session is null) return;
        var warning = $"删除「{session.Title}」的本次计时记录？\n当日总结和其他番茄钟记录会保留。正在计时的该任务会立即停止。";
        if (!await _interaction.ConfirmAsync("删除本次番茄钟", warning)) return;
        if (DeleteSessionRequested is not null)
        {
            foreach (Func<TimerSession, Task> handler in DeleteSessionRequested.GetInvocationList()) await handler(session);
        }
        else await _storage.DeleteSessionAsync(session);
        await ForgetDeletedSessionCoreAsync(session.Id);
        StatusMessage = "本次番茄钟已删除，可以直接开始新的任务。";
    });

    public Task ForgetDeletedSessionAsync(string sessionId) => RunAsync(() => ForgetDeletedSessionCoreAsync(sessionId));

    private Task ForgetDeletedSessionCoreAsync(string sessionId)
    {
        var rows = Sessions.Where(row => row.Session.Id != sessionId).ToList();
        var selected = _selected?.Session.Id == sessionId ? null : _selected;
        CommitRows(rows, selected);
        NotifyEditorState();
        StatusMessage = "已移除本次记录，其他日志保留。";
        return Task.CompletedTask;
    }

    public Task<bool> ChangeContextAsync(string workspace, DateTime date, bool closeDateChooser = false) => RunAsync(async () =>
    {
        if (!await CanLeaveCoreAsync()) { NotifySelection(); return false; }
        workspace = string.IsNullOrWhiteSpace(workspace) ? "" : Path.GetFullPath(workspace);
        var targetDate = date.Date;
        if (!string.IsNullOrEmpty(workspace)) await _storage.PrepareDayAsync(workspace, DateOnly.FromDateTime(targetDate));
        var rows = await ReadRowsAsync(workspace, targetDate);
        var document = await ReadDocumentAsync(workspace, targetDate);
        var history = await ReadJournalHistoryAsync(workspace);
        CommitContext(workspace, targetDate, rows, document);
        CommitHistory(history);
        if (!string.IsNullOrEmpty(workspace)) WorkspaceChanged?.Invoke(workspace);
        if (closeDateChooser) DateChooserVisible = false;
        return true;
    });

    public Task<bool> SelectDateForEditingAsync(DateTime date) => ChangeContextAsync(_workspace, date, closeDateChooser: true);
    public Task OpenReflectionAsync(TimerSession session) => ChangeContextAsync(session.WorkspacePath, session.Day.ToDateTime(TimeOnly.MinValue), closeDateChooser: true);
    public Task AddReflectionAsync() => Task.CompletedTask;

    public Task EnterAsync() => RunAsync(async () =>
    {
        var rows = await ReadRowsAsync(_workspace, _date ?? DateTime.Today);
        var selected = RemapSelection(rows, _selected);
        var preserveEditor = IsDirty;
        var document = preserveEditor ? null : await ReadDocumentAsync(_workspace, _date ?? DateTime.Today);
        CommitRows(rows, selected);
        if (!preserveEditor) { if (document is not null) SetDocument(document); else ClearDocument(); UpdateEditorHeading(_date ?? DateTime.Today); }
        await RefreshDraftsCoreAsync();
        await RefreshJournalHistoryCoreAsync();
        DateChooserVisible = true;
    });

    public Task<bool> CanLeaveAsync() => RunAsync(CanLeaveCoreAsync);
    public Task<bool> SaveAsync() => RunAsync(SaveCoreAsync);
    public Task RefreshJournalHistoryAsync() => RunAsync(RefreshJournalHistoryCoreAsync);

    private Task<IReadOnlyList<JournalEntry>> ReadJournalHistoryAsync(string workspace) => string.IsNullOrEmpty(workspace)
        ? Task.FromResult<IReadOnlyList<JournalEntry>>(Array.Empty<JournalEntry>()) : _storage.LoadJournalHistoryAsync(workspace);
    private async Task RefreshJournalHistoryCoreAsync() => CommitHistory(await ReadJournalHistoryAsync(_workspace));

    private void CommitHistory(IReadOnlyList<JournalEntry> entries)
    {
        _loading = true;
        try
        {
            History.Clear();
            foreach (var entry in entries.OrderByDescending(entry => entry.Day)) History.Add(entry);
            _selectedHistory = History.FirstOrDefault(entry => entry.Day == DateOnly.FromDateTime(_date ?? DateTime.Today));
            OnPropertyChanged(nameof(SelectedHistory));
        }
        finally { _loading = false; }
    }

    private async Task<bool> CanLeaveCoreAsync()
    {
        if (!IsDirty) return true;
        var decision = await _interaction.ResolveUnsavedAsync("保存日志", "当前日志有未保存的修改，是否保存？");
        if (decision == SaveDecision.Cancel) return false;
        if (decision == SaveDecision.Save) return await SaveCoreAsync();
        ResetEditorToBaseline();
        _openedDraft = null;
        return true;
    }

    private Task SwitchDocumentAsync() => RunAsync(async () =>
    {
        if (!await CanLeaveCoreAsync()) { NotifySelection(); return; }
        var document = await ReadDocumentAsync(_workspace, _date ?? DateTime.Today);
        if (document is not null) SetDocument(document); else ClearDocument();
        UpdateEditorHeading(_date ?? DateTime.Today);
        NotifySelection();
    });

    private Task RefreshAsync() => RunAsync(async () =>
    {
        var rows = await ReadRowsAsync(_workspace, _date ?? DateTime.Today);
        var selected = RemapSelection(rows, _selected);
        var preserveEditor = IsDirty;
        var document = preserveEditor ? null : await ReadDocumentAsync(_workspace, _date ?? DateTime.Today);
        CommitRows(rows, selected);
        if (!preserveEditor) { if (document is not null) SetDocument(document); else ClearDocument(); UpdateEditorHeading(_date ?? DateTime.Today); }
        await RefreshDraftsCoreAsync();
        await RefreshJournalHistoryCoreAsync();
    });

    private async Task<List<SessionRow>> ReadRowsAsync(string workspace, DateTime date)
    {
        if (string.IsNullOrEmpty(workspace)) return [];
        var sessions = await _storage.LoadSessionsAsync(workspace, DateOnly.FromDateTime(date));
        return sessions.Select(session => new SessionRow(session)).ToList();
    }

    private async Task<EditorLoad?> ReadDocumentAsync(string workspace, DateTime date)
    {
        if (string.IsNullOrEmpty(workspace)) return null;
        var day = DateOnly.FromDateTime(date);
        var path = Path.Combine(_storage.GetDayDirectory(workspace, day), "Daily.md");
        var template = $"# {day:yyyy-MM-dd} 每日日志\n\n";
        var snapshot = await _storage.ReadDocumentAsync(path, template);
        return new EditorLoad(snapshot, snapshot.Content);
    }

    private sealed record EditorLoad(DocumentSnapshot Snapshot, string Text);

    private void CommitContext(string workspace, DateTime date, List<SessionRow> rows, EditorLoad? document)
    {
        _workspace = workspace;
        _date = date;
        CommitRows(rows, null);
        if (document is not null) SetDocument(document); else ClearDocument();
        UpdateEditorHeading(date);
        OnPropertyChanged(nameof(WorkspacePath));
        OnPropertyChanged(nameof(WorkspaceName));
        OnPropertyChanged(nameof(DailyContextText));
        NotifySelection();
        OnPropertyChanged(nameof(CanEdit));
    }

    private void CommitRows(IEnumerable<SessionRow> rows, SessionRow? selected)
    {
        _loading = true;
        try
        {
            Sessions.Clear();
            foreach (var row in rows) Sessions.Add(row);
            _selected = selected;
            OnPropertyChanged(nameof(SelectedSession));
            OnPropertyChanged(nameof(SelectedSessionSummary));
            OnPropertyChanged(nameof(HasSelectedSession));
        }
        finally { _loading = false; }
    }

    private static SessionRow? RemapSelection(IReadOnlyList<SessionRow> rows, SessionRow? selected) => selected is null
        ? null : rows.FirstOrDefault(row => row.Session.Id == selected.Session.Id);

    private void UpdateEditorHeading(DateTime date)
    {
        EditorTitle = $"{date:MM月dd日} · 每日日志";
        NotifyEditorState();
    }

    private void NotifySelection()
    {
        _loading = true;
        try { OnPropertyChanged(nameof(SelectedDate)); OnPropertyChanged(nameof(SelectedSession)); OnPropertyChanged(nameof(SelectedHistory)); }
        finally { _loading = false; }
    }

    private void SetDocument(EditorLoad editor)
    {
        _document = editor.Snapshot;
        _baseline = editor.Text;
        SetEditorText(editor.Text);
        _openedDraft = null;
        SaveStatus = editor.Snapshot.Fingerprint is null ? "未保存" : "已保存";
        NotifyEditorState();
    }

    private void ClearDocument()
    {
        _document = null;
        _baseline = "";
        _openedDraft = null;
        SetEditorText("");
        SaveStatus = string.IsNullOrEmpty(_workspace) ? "请选择工作区" : "暂无日志";
        NotifyEditorState();
    }

    private void ResetEditorToBaseline()
    {
        SetEditorText(_baseline);
        _openedDraft = null;
        SaveStatus = "已放弃未保存修改";
        NotifyEditorState();
    }

    private void NotifyEditorState()
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(HasLogEditor));
        OnPropertyChanged(nameof(HasReflectionEditor));
        OnPropertyChanged(nameof(IsReflectionMode));
        OnPropertyChanged(nameof(CanOpenReflection));
        OnPropertyChanged(nameof(ReflectionActionText));
        OnPropertyChanged(nameof(SelectedSessionSummary));
        OnPropertyChanged(nameof(HasSelectedSession));
        OnPropertyChanged(nameof(IsDirty));
    }

    private void SetEditorText(string text)
    {
        _loading = true;
        try { EditorText = text; }
        finally { _loading = false; }
    }

    private async Task<bool> SaveCoreAsync()
    {
        if (_document is null || !IsDirty) return true;
        var snapshot = _document;
        var content = EditorText;
        try
        {
            await _storage.SaveDocumentAsync(snapshot, content);
            var fresh = await _storage.ReadDocumentAsync(snapshot.Path);
            _document = StringComparer.Ordinal.Equals(fresh.Content, content) ? fresh : snapshot with { Content = content };
            _baseline = content;
            SaveStatus = $"已保存 · {DateTime.Now:HH:mm:ss}";
            StatusMessage = "日志已保存到工作区。";
            if (_openedDraft is not null)
            {
                await _storage.DeleteRecoveryAsync(_openedDraft.Id);
                RecoveryDrafts.Remove(_openedDraft);
                _openedDraft = null;
            }
            try { await RefreshJournalHistoryCoreAsync(); }
            catch (Exception error) { StatusMessage = "日志已保存，历史列表刷新失败：" + error.Message; }
            JournalSaved?.Invoke();
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SaveStatus = error is DocumentConflictException ? "外部修改冲突：请另存为，或放弃本地修改后刷新" : "保存失败，内容仍保留";
            StatusMessage = error.Message;
            try { await _storage.SaveRecoveryAsync(snapshot.Path, content); StatusMessage += " 已写入本地恢复草稿。"; }
            catch (Exception recoveryError) { StatusMessage += " 恢复草稿也未保存：" + recoveryError.Message; }
            _interaction.ShowMessage(error is DocumentConflictException ? "日志已被外部修改" : "日志保存失败", StatusMessage + "\n当前编辑内容仍然保留，可以重试或另存为。");
            return false;
        }
    }

    private Task SaveAsAsync() => RunAsync(async () =>
    {
        if (_document is null) return;
        var path = await _interaction.PickSaveFileAsync(Path.GetFileName(_document.Path));
        if (path is null) return;
        var target = await _storage.ReadDocumentAsync(path);
        await _storage.SaveDocumentAsync(target, EditorText);
        StatusMessage = "副本已保存：" + path + (IsDirty ? "。原日志仍有未保存修改。" : "");
    });

    private async Task RefreshDraftsCoreAsync()
    {
        var drafts = await _storage.LoadRecoveryDraftsAsync();
        var selectedId = SelectedRecoveryDraft?.Id;
        RecoveryDrafts.Clear();
        foreach (var draft in drafts) RecoveryDrafts.Add(draft);
        SelectedRecoveryDraft = drafts.FirstOrDefault(draft => draft.Id == selectedId) ?? drafts.FirstOrDefault();
        if (drafts.Count > 0) StatusMessage = $"发现 {drafts.Count} 份恢复草稿，可选择后点击恢复。";
    }

    private Task RestoreSelectedDraftAsync() => RunAsync(async () =>
    {
        var draft = SelectedRecoveryDraft;
        if (draft is null) { StatusMessage = "没有选中的恢复草稿。"; return; }
        if (!await CanLeaveCoreAsync()) return;
        var target = await _storage.ReadDocumentAsync(draft.OriginalPath);
        if (string.Equals(Path.GetExtension(draft.OriginalPath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            if (!await _interaction.ConfirmAsync("恢复计时记录", "将恢复草稿写回原计时记录文件，是否继续？\n" + draft.OriginalPath)) return;
            await _storage.SaveDocumentAsync(target, draft.Content);
            await _storage.DeleteRecoveryAsync(draft.Id);
            await RefreshDraftsCoreAsync();
            StatusMessage = "计时记录已恢复，请刷新对应日期查看。";
            return;
        }
        SetDocument(new EditorLoad(target, draft.Content));
        SetEditorText(draft.Content);
        _openedDraft = draft;
        EditorTitle = "恢复草稿 · " + Path.GetFileName(draft.OriginalPath);
        SaveStatus = "草稿已载入，请检查后保存或另存为";
        StatusMessage = "原始位置：" + draft.OriginalPath;
    });

    private async Task RunAsync(Func<Task> action)
    {
        await _operations.WaitAsync();
        try { if (_disposed) return; IsBusy = true; await action(); }
        finally { IsBusy = false; _operations.Release(); }
    }

    private async Task<bool> RunAsync(Func<Task<bool>> action)
    {
        await _operations.WaitAsync();
        try { if (_disposed) return false; IsBusy = true; return await action(); }
        finally { IsBusy = false; _operations.Release(); }
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { NotifySelection(); OnError(error); }
    }

    private void OnError(Exception error)
    {
        StatusMessage = error.Message;
        SaveStatus = "操作未完成，当前内容仍保留";
        _interaction.ShowMessage("记录操作未完成", error.Message);
    }

    public async ValueTask DisposeAsync()
    {
        await _operations.WaitAsync();
        try { _disposed = true; WorkspaceChanged = null; DeleteSessionRequested = null; JournalSaved = null; }
        finally { _operations.Release(); }
    }
}
