using System.Collections.Immutable;
using TomatoTimerPro.Contracts.Audio;
using TomatoTimerPro.Contracts.Interaction;
using TomatoTimerPro.Contracts.Storage;
using TomatoTimerPro.Contracts.Threading;
using TomatoTimerPro.Controllers.Components.Countdown;
using TomatoTimerPro.Controllers.Pages.Music;
using TomatoTimerPro.Controllers.Pages.Records;
using TomatoTimerPro.Controllers.Pages.Settings;
using TomatoTimerPro.Controllers.Pages.Timer;
using TomatoTimerPro.Controllers.Pages.Calendar;
using TomatoTimerPro.Controllers.Components.Schedule;
using TomatoTimerPro.RuntimeStore.States.Planning;
using TomatoTimerPro.Controllers.Shell;
using TomatoTimerPro.Infrastructure.Audio;
using TomatoTimerPro.Infrastructure.Persistence;
using TomatoTimerPro.Managers.Pages;
using TomatoTimerPro.RuntimeStore.States.Settings;
using TomatoTimerPro.RuntimeStore.States.Timer;
using TomatoTimerPro.RuntimeStore.States.Workspaces;
using TomatoTimerPro.Views.Pages.Music;
using TomatoTimerPro.Views.Pages.Records;
using TomatoTimerPro.Views.Pages.Settings;
using TomatoTimerPro.Views.Pages.Timer;
using TomatoTimerPro.Views.Pages.Calendar;
using StoreType = TomatoTimerPro.RuntimeStore.Implementation.RuntimeStore;

namespace TomatoTimerPro.Bootstrap;

public sealed class ApplicationComposition : IAsyncDisposable
{
    private readonly SemaphoreSlim _preferenceWrites = new(1, 1);
    private Task _pendingSave = Task.CompletedTask;
    private bool _disposed;
    private bool _loadingPreferences;
    private readonly IUserInteraction _interaction;
    private readonly IDisposable _durationSubscription;
    private double _defaultDurationMinutes = 25;
    private bool _syncingDuration;
    public StoreType Store { get; } = new();
    public IAppStorage Storage { get; }
    public IMusicService Music { get; }
    public CountdownController Countdown { get; }
    public PageMgr Pages { get; } = new();
    public MainWindowController Shell { get; }
    public TimerPageController Timer { get; }
    public CalendarPageController Calendar { get; }
    public SchedulePlaybackController Schedule { get; }
    public DayTimeAllocationController TimeAllocation { get; }
    public RecordsPageController? Records { get; private set; }

    public ApplicationComposition(IUiDispatcher dispatcher, IUserInteraction interaction, string? dataDirectory = null)
    {
        _interaction = interaction;
        var localStorage = new LocalAppStorage(dataDirectory);
        Storage = localStorage;
        Store.Set(new AppPreferences()); Store.Set(new TimerState()); Store.Set(new WorkspaceState());
        Music = new LocalMusicService(Storage.AppDataDirectory, dispatcher);
        Countdown = new CountdownController(Store, Storage, dispatcher);
        Calendar = new CalendarPageController(Store, localStorage, interaction, dispatcher);
        Timer = new TimerPageController(Countdown, Storage, interaction, Pages, Calendar);
        TimeAllocation = new DayTimeAllocationController(Calendar, Countdown, Storage);
        Timer.TimeAllocation = TimeAllocation;
        Calendar.TimeAllocation = TimeAllocation;
        Schedule = new SchedulePlaybackController(Countdown, async plan =>
        {
            var current = Store.Get<PlanState>().Entries.FirstOrDefault(entry => entry.Id == plan.Id);
            if (current is null || current.Status != PlanStatus.Incomplete || !current.HasPomodoro)
                throw new InvalidOperationException("日程中的计划已变更，请重新开始日程。");
            return await Timer.StartScheduledPlanAsync(plan);
        }, async plan =>
        {
            var current = Store.Get<PlanState>().Entries.FirstOrDefault(entry => entry.Id == plan.Id);
            if (current is null || current.Status != PlanStatus.Incomplete)
                throw new InvalidOperationException("当前计划状态已变更，自动日程已停止。");
            await Calendar.ChangeStatusAsync(plan.Id, PlanStatus.Completed);
            if (Store.Get<PlanState>().Entries.FirstOrDefault(entry => entry.Id == plan.Id)?.Status != PlanStatus.Completed)
                throw new IOException("计划完成状态未能保存，自动日程已停止。");
        }, error => interaction.ShowMessage("日程已停止", error.Message));
        Timer.AttachSchedule(Schedule);
        Timer.ScheduleStartRequested += async () =>
        {
            await Calendar.RefreshTodayAsync();
            await Schedule.StartAsync(UseSharedDuration(Store.Get<PlanState>().Entries.Where(plan => plan.Day == DateOnly.FromDateTime(DateTime.Now))));
        };
        Timer.Duration.Changed += SaveDurationPreferences;
        Timer.ScheduleRest.Changed += SaveSchedulePreferences;
        _durationSubscription = Store.Subscribe<AppPreferences>(preferences =>
        {
            if (_defaultDurationMinutes == preferences.DefaultDurationMinutes) return;
            _defaultDurationMinutes = preferences.DefaultDurationMinutes;
            if (_syncingDuration) return;
            _syncingDuration = true;
            try { Timer.Duration.SetDuration(TimeSpan.FromMinutes(preferences.DefaultDurationMinutes)); }
            finally { _syncingDuration = false; }
        });
        Shell = new MainWindowController(Pages, Timer, interaction, Store);
        Timer.StopAlarmRequested += Music.StopAlarm;
        Timer.PreviewAlarmRequested += path => Music.PlayAlarmAsync(path, path == "__silent__");
        Timer.StartAlarmRequested += path => Music.PlayAlarmAsync(path, path == "__silent__");
        Timer.WorkspaceChanged += OnWorkspaceChanged;
        Calendar.JournalRequested += async day =>
        {
            if (await Pages.NavigateAsync("records") && Records is not null)
            {
                var workspace = Store.Get<WorkspaceState>().ActivePath ?? "";
                await Records.ChangeContextAsync(workspace, day.ToDateTime(TimeOnly.MinValue), closeDateChooser: true);
                Records.ShowHistoryCommand.Execute(null);
            }
        };
        Calendar.FocusRequested += async plan =>
        {
            if (!await Pages.NavigateAsync("timer")) return;
            Timer.SelectScheduleModeCommand.Execute(null);
            var today = DateOnly.FromDateTime(DateTime.Now);
            var plans = Store.Get<PlanState>().Entries.Where(entry => entry.Day == today).ToArray();
            var index = Array.FindIndex(plans, entry => entry.Id == plan.Id);
            if (index < 0)
            {
                interaction.ShowMessage("该计划不在今日日程", "请将计划加入今日日程后开始，或使用休闲模式。");
                return;
            }
            await Schedule.StartAsync(UseSharedDuration(plans.Skip(index)));
        };
        Calendar.LeisureFocusRequested += async plan =>
        {
            if (await Pages.NavigateAsync("timer")) await Timer.StartPlanAsync(plan);
        };
        Timer.RecordDeleted += async session =>
        { if (Records is not null) await Records.ForgetDeletedSessionAsync(session.Id); };
        Countdown.StorageError += message => Shell.StatusMessage = message;
        Music.LibraryChanged += (_, _) => RefreshAlarmChoices();
        Music.Changed += (_, _) => SaveAudioPreferences();
        Timer.PropertyChanged += (_, args) =>
        {
            if (!_loadingPreferences && args.PropertyName == nameof(Timer.SelectedAlarm))
            { Store.Update<AppPreferences>(p => p with { DefaultAlarmPath = Timer.SelectedAlarm?.Path }); QueueSave(); }
            if (!_loadingPreferences && args.PropertyName == nameof(Timer.SelectedStartAlarm))
            { Store.Update<AppPreferences>(p => p with { DefaultStartAlarmPath = Timer.SelectedStartAlarm?.Path }); QueueSave(); }
            if (!_loadingPreferences && args.PropertyName == nameof(Timer.ScheduleRestMinutes)) SaveSchedulePreferences();
        };
        Pages.Changed += () => { Shell.CurrentPage = Pages.Current?.View; Shell.CurrentPageId = Pages.CurrentId; };
        Pages.Register("timer", () => new PageScope(new TimerPage { DataContext = Timer }, new PageLifecycle(dispose: Timer.DisposeAsync)), true);
        Pages.Register("records", CreateRecords, true);
        Pages.Register("calendar", () => new PageScope(new CalendarPage { DataContext = Calendar },
            new PageLifecycle(enter: async () =>
            {
                await Calendar.SetJournalWorkspaceAsync(Store.Get<WorkspaceState>().ActivePath, Storage);
            })), true);
        Pages.Register("music", () =>
        {
            var controller = new MusicPageController(Music, interaction);
            return new PageScope(new MusicPage { DataContext = controller }, new PageLifecycle(
                initialize: controller.InitializeAsync, dispose: () => { controller.Dispose(); return ValueTask.CompletedTask; }));
        }, true);
        Pages.Register("settings", () =>
        {
            var controller = new SettingsPageController(Store, Storage, Music, interaction);
            controller.PreferencesChanged += QueueSave;
            return new PageScope(new SettingsPage { DataContext = controller }, new PageLifecycle(dispose: () =>
            { controller.Dispose(); return ValueTask.CompletedTask; }));
        }, true);
    }
    public async Task InitializeAsync()
    {
        _loadingPreferences = true;
        try
        {
            try { Store.Set(await Storage.LoadPreferencesAsync()); }
            catch (Exception error) { Shell.StatusMessage = "偏好读取失败，使用默认设置：" + error.Message; }
            var p = Store.Get<AppPreferences>();
            Music.MusicVolume = p.MusicVolume; Music.AlarmVolume = p.AlarmVolume; Music.ResumeMusic = p.ResumeMusic;
            Timer.DurationMinutes = p.DefaultDurationMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Timer.ScheduleRestMinutes = p.ScheduleRestMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(p.LastWorkspace))
            {
                Timer.RestoreWorkspacePath(p.LastWorkspace); Shell.WorkspaceName = new DirectoryInfo(p.LastWorkspace).Name;
                Store.Set(new WorkspaceState(p.LastWorkspace, p.RecentWorkspaces));
            }
            RefreshAlarmChoices();
        }
        finally { _loadingPreferences = false; }
        await Calendar.InitializeAsync();
        await Calendar.SetJournalWorkspaceAsync(Timer.WorkspacePath, Storage);
        await TimeAllocation.SetWorkspaceAsync(Timer.WorkspacePath);
        await Pages.NavigateAsync("timer");
        try { await Countdown.RestoreAsync(); }
        catch (Exception error) { Shell.StatusMessage = "上次计时恢复失败：" + error.Message; }
        try { await Music.InitializeAsync(); RefreshAlarmChoices(); }
        catch (Exception error) { Shell.StatusMessage = "音乐列表读取失败：" + error.Message; }
    }
    private PageScope CreateRecords()
    {
        Records = new RecordsPageController(Storage, _interaction, Pages);
        Records.WorkspaceChanged += OnWorkspaceChanged;
        Records.JournalSaved += () => _ = Calendar.SetJournalWorkspaceAsync(Store.Get<WorkspaceState>().ActivePath, Storage);
        Records.DeleteSessionRequested += async session =>
        {
            if (Countdown.Current?.Id == session.Id)
            { Music.StopAlarm(); await Countdown.DeleteCurrentRecordAsync(); }
            else await Storage.DeleteSessionAsync(session);
            await TimeAllocation.RefreshAsync();
        };
        return new PageScope(new RecordsPage { DataContext = Records }, new PageLifecycle(
            initialize: async () =>
            {
                var workspace = Store.Get<WorkspaceState>().ActivePath;
                if (!string.IsNullOrEmpty(workspace)) await Records.ChangeContextAsync(workspace, DateTime.Today);
            }, enter: async () =>
            {
                var active = Store.Get<WorkspaceState>().ActivePath;
                if (!string.IsNullOrEmpty(active) && !StringComparer.OrdinalIgnoreCase.Equals(active, Records.WorkspacePath))
                    await Records.ChangeContextAsync(active, DateTime.Today);
                await Records.EnterAsync();
            }, canLeave: Records.CanLeaveAsync, dispose: Records.DisposeAsync));
    }
    private void RefreshAlarmChoices()
    {
        var preferences = Store.Get<AppPreferences>();
        var preferred = Timer.SelectedAlarm?.Path ?? preferences.DefaultAlarmPath;
        var preferredStart = Timer.SelectedStartAlarm?.Path ?? preferences.DefaultStartAlarmPath;
        var choices = new[] { new AlarmChoice("默认提示音", null), new AlarmChoice("静音 · 仅显示提醒", "__silent__") }
            .Concat(Music.Tracks.Select(t => new AlarmChoice(t.Title, t.FilePath))).ToList();
        if (preferred is not null && !choices.Any(c => c.Path == preferred)) choices.Add(new AlarmChoice("上次选择（文件可能不可用）", preferred));
        if (preferredStart is not null && !choices.Any(c => c.Path == preferredStart)) choices.Add(new AlarmChoice("上次开始铃声（文件可能不可用）", preferredStart));
        Timer.UpdateAlarmChoices(choices);
        Timer.SelectedAlarm = choices.FirstOrDefault(c => c.Path == preferred) ?? choices[0];
        Timer.SelectedStartAlarm = choices.FirstOrDefault(c => c.Path == preferredStart) ?? choices[0];
    }
    private IReadOnlyList<PlanEntry> UseSharedDuration(IEnumerable<PlanEntry> plans)
    {
        if (!Timer.Duration.TryGetDuration(out var duration)) return plans.ToArray();
        var minutes = duration.TotalMinutes;
        return plans.Select(plan => plan with { DurationMinutes = minutes }).ToArray();
    }
    private void OnWorkspaceChanged(string path)
    {
        var recent = Store.Get<AppPreferences>().RecentWorkspaces.Where(p => !StringComparer.OrdinalIgnoreCase.Equals(p, path)).Prepend(path).Take(12).ToImmutableArray();
        Store.Set(new WorkspaceState(path, recent));
        Store.Update<AppPreferences>(p => p with { LastWorkspace = path, RecentWorkspaces = recent });
        Timer.RestoreWorkspacePath(path); Shell.WorkspaceName = new DirectoryInfo(path).Name; QueueSave();
        _ = TimeAllocation.SetWorkspaceAsync(path);
        _ = Calendar.SetJournalWorkspaceAsync(path, Storage);
    }
    private void SaveAudioPreferences()
    {
        if (_disposed || _loadingPreferences) return;
        var prefs = Store.Get<AppPreferences>();
        if (prefs.MusicVolume == Music.MusicVolume && prefs.AlarmVolume == Music.AlarmVolume && prefs.ResumeMusic == Music.ResumeMusic) return;
        Store.Update<AppPreferences>(p => p with { MusicVolume = Music.MusicVolume, AlarmVolume = Music.AlarmVolume, ResumeMusic = Music.ResumeMusic }); QueueSave();
    }
    private void SaveDurationPreferences()
    {
        if (_disposed || _loadingPreferences || _syncingDuration || !Timer.Duration.TryGetDuration(out var duration)) return;
        if (Store.Get<AppPreferences>().DefaultDurationMinutes == duration.TotalMinutes) return;
        _syncingDuration = true;
        try { Store.Update<AppPreferences>(p => p with { DefaultDurationMinutes = duration.TotalMinutes }); }
        finally { _syncingDuration = false; }
        QueueSave();
    }
    private void SaveSchedulePreferences()
    {
        if (_disposed || _loadingPreferences) return;
        if (!double.TryParse(Timer.ScheduleRestMinutes, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var minutes)
            || !double.IsFinite(minutes) || minutes is < 0 or > 1440) return;
        if (Store.Get<AppPreferences>().ScheduleRestMinutes == minutes) return;
        Store.Update<AppPreferences>(p => p with { ScheduleRestMinutes = minutes });
        QueueSave();
    }
    public void QueueSave() => _pendingSave = SavePreferencesSafeAsync();
    private async Task SavePreferencesSafeAsync()
    {
        await _preferenceWrites.WaitAsync();
        try { await Storage.SavePreferencesAsync(Store.Get<AppPreferences>()); }
        catch (Exception error) { Shell.StatusMessage = "偏好保存失败：" + error.Message; }
        finally { _preferenceWrites.Release(); }
    }
    public async Task FlushAsync() { await _pendingSave; await SavePreferencesSafeAsync(); }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Timer.Duration.Changed -= SaveDurationPreferences;
        Timer.ScheduleRest.Changed -= SaveSchedulePreferences;
        _durationSubscription.Dispose();
        await TimeAllocation.DisposeAsync();
        await Schedule.DisposeAsync(); await Countdown.DisposeAsync(); await Pages.DisposeAsync(); await Calendar.DisposeAsync(); Shell.Dispose(); Music.Dispose(); Store.Dispose();
    }
}
