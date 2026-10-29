using System.IO;
using TomatoTimerPro.Contracts.Interaction;
using TomatoTimerPro.Contracts.Storage;
using TomatoTimerPro.Contracts.Threading;
using TomatoTimerPro.Controllers.Components.Planning;
using TomatoTimerPro.Controllers.Pages.Calendar;
using TomatoTimerPro.Infrastructure.Persistence;
using TomatoTimerPro.RuntimeStore.Implementation;
using TomatoTimerPro.RuntimeStore.States.Planning;

public static class PlanningChecks
{
    public static async Task RunAsync(string testRoot)
    {
        var storage = new LocalAppStorage(Path.Combine(testRoot, "PlansData"));
        var faulty = new FaultStorage(storage);
        using var store = new RuntimeStore();
        var interaction = new Interaction();
        var today = new DateOnly(2028, 2, 29);
        await using var calendar = new CalendarPageController(store, faulty, interaction, new Dispatcher(), () => today);
        await calendar.InitializeAsync();
        Check(calendar.IsLoaded && calendar.Days.Count == 42 && calendar.Days[0].Day?.DayOfWeek == DayOfWeek.Monday,
            "Month grid starts on Monday and supports leap dates");
        interaction.EditorAction = async editor =>
        {
            editor.Plans[0].Title = "写作";
            editor.Plans[0].HasPomodoro = true;
            editor.Plans[0].DurationMinutes = "45.5";
            editor.AddCommand.Execute(null);
            editor.Plans[1].Title = "读书";
            editor.Plans[1].HasPomodoro = true;
            editor.Plans[1].DurationMinutes = "15";
            editor.AddCommand.Execute(null);
            editor.Plans[2].Title = "散步";
            editor.Plans[2].HasPomodoro = true;
            editor.Plans[2].DurationMinutes = "30";
            editor.Plans[1].SelectedStatus = PlanStatusChoice.All.Single(s => s.Value == PlanStatus.Completed);
            editor.Plans[2].SelectedStatus = PlanStatusChoice.All.Single(s => s.Value == PlanStatus.OnHold);
            Check(calendar.TodayPlans.Count == 0, "Unsaved editor rows are isolated from persisted plans");
            Check(await editor.SaveAsync(), "A dedicated editor saves several independent plans for one day");
        };
        await calendar.EditDateAsync(today);
        Check(interaction.EditorOpened == 1 && calendar.TodayPlans.Count == 2 && calendar.OnHoldPlans.Count == 1, "Calendar separates saved active plans from the on-hold box");
        var entries = store.Get<PlanState>().Entries.ToArray();
        Check(entries.Select(p => p.Status).SequenceEqual(new[] { PlanStatus.Incomplete, PlanStatus.Completed, PlanStatus.OnHold }),
            "Existing incomplete, completed and on-hold plans keep their distinct states");
        Check(PlanStatusChoice.All.Select(choice => choice.Value).SequenceEqual(Enum.GetValues<PlanStatus>())
            && PlanStatusChoice.All.Single(choice => choice.Value == PlanStatus.Abandoned).Label == "放弃",
            "All four statuses including abandoned are available in selection controls");
        Check(calendar.TodayProgress.Contains("1 / 2") && calendar.OnHoldPlans.Count == 1, "Completion totals exclude the independent on-hold box");
        Check(entries.Select(p => p.DurationMinutes).SequenceEqual(new[] { 45.5, 15d, 30d })
            && (await storage.LoadPlansAsync()).Entries[0].DurationMinutes == 45.5,
            "Each plan persists its own focus duration, including fractional minutes");
        Check(calendar.TodayPlans[0].DurationText == "45.5 分钟", "Plan rows show the saved duration");
        PlanEntry? requested = null;
        calendar.FocusRequested += plan => { requested = plan; return Task.CompletedTask; };
        calendar.TodayPlans[0].StartFocusCommand.Execute(null);
        Check(requested is { Title: "写作", DurationMinutes: 45.5 }, "Start-focus command forwards the plan's persisted title and duration");
        using (var durationEditor = new PlanEditorController(today, entries, false, interaction, p => calendar.SaveDayAsync(today, p)))
        {
            foreach (var invalid in new[] { "", "invalid", "NaN", "Infinity", "0", "-1", "0.009", "1440.1" })
            {
                durationEditor.Plans[0].DurationMinutes = invalid;
                Check(durationEditor.IsDirty && !await durationEditor.SaveAsync()
                    && durationEditor.Plans[0].DurationMinutes == invalid && durationEditor.CanEdit,
                    "Invalid duration keeps the editor and raw input available: " + invalid);
                Check((await storage.LoadPlansAsync()).Entries[0].DurationMinutes == 45.5,
                    "Invalid duration never replaces saved plans");
            }
            durationEditor.Plans[0].DurationMinutes = "45.5";
            Check(!durationEditor.IsDirty, "Restoring the original duration clears the draft change");
        }
        Check(await calendar.SaveDayAsync(today, entries.Select((p, i) => i == 0 ? p with { DurationMinutes = double.NaN } : p).ToArray()) is not null,
            "Calendar storage boundary rejects invalid focus durations");

        interaction.EditorAction = async editor =>
        {
            Check(editor.Plans.Count == 3 && editor.Plans[2].Title == "", "Add plan opens the editor with active plans and a new independent row");
            editor.Plans[2].Title = "第四项计划";
            editor.Plans[1].Title = "修改已完成的计划名称";
            Check(await editor.SaveAsync(), "Additional plans can be appended to the same date");
        };
        await calendar.EditDateAsync(today, true);
        Check(store.Get<PlanState>().Entries.Single(p => p.Id == entries[1].Id).Title == "修改已完成的计划名称" && calendar.TodayPlans[1].IsCompleted,
            "Renaming a plan preserves its identity and status");
        Check(store.Get<PlanState>().Entries.Single(p => p.Id == entries[1].Id).DurationMinutes == 15, "Editing other plan fields preserves its focus duration");

        interaction.Decision = SaveDecision.Cancel;
        using (var editor = new PlanEditorController(today, store.Get<PlanState>().Entries, false, interaction, p => calendar.SaveDayAsync(today, p)))
        {
            var closed = false;
            editor.CloseRequested += () => closed = true;
            editor.Plans[0].Title = "未保存的修改";
            Check(!await editor.RequestCloseAsync() && !closed && editor.IsDirty, "Cancelling close keeps the editing panel and draft");
            faulty.FailWrite = true;
            Check(!await editor.SaveAsync() && editor.Plans[0].Title == "未保存的修改" && !closed,
                "Write failure keeps the edit panel open and preserves typed content");
            Check((await storage.LoadPlansAsync()).Entries.Single(p => p.Id == entries[0].Id).Title == "写作", "Failed save does not replace persisted titles");
            interaction.Decision = SaveDecision.Discard;
            Check(await editor.RequestCloseAsync() && closed && calendar.TodayPlans[0].Title == "写作",
                "Discarding a draft leaves saved plans untouched");
        }
        await calendar.ChangeStatusAsync(entries[0].Id, PlanStatus.OnHold);
        Check(!calendar.TodayPlans[0].IsOnHold, "Failed status save restores the visible authoritative state");
        faulty.FailWrite = false;
        foreach (var status in Enum.GetValues<PlanStatus>())
        {
            await calendar.ChangeStatusAsync(entries[0].Id, status);
            Check((await storage.LoadPlansAsync()).Entries.Single(p => p.Id == entries[0].Id).Status == status,
                "Every status persists across a storage roundtrip");
        }

        interaction.Confirm = false;
        await calendar.DeleteAsync(entries[1].Id);
        Check(calendar.TodayPlans.Count == 2, "Rejecting delete preserves the selected active plan");
        interaction.Confirm = true;
        faulty.FailWrite = true;
        await calendar.DeleteAsync(entries[1].Id);
        Check(calendar.TodayPlans.Count == 2, "Failed delete preserves all active plans");
        faulty.FailWrite = false;
        await calendar.DeleteAsync(entries[1].Id);
        Check(calendar.TodayPlans.Count == 1 && !(await storage.LoadPlansAsync()).Entries.Any(p => p.Id == entries[1].Id),
            "Confirmed delete removes only the requested plan and persists the result");

        var other = today.AddDays(1);
        Check(await calendar.SaveDayAsync(other, new[] { new PlanEntry(Guid.NewGuid().ToString("N"), other, "三月计划") }) is null,
            "Another day has independent plans");
        Check(calendar.TodayPlans.Count == 1, "Future plans do not appear in today's list");
        today = other;
        calendar.RefreshToday();
        await calendar.RefreshTodayAsync();
        Check(calendar.TodayPlans.Single().Title == "三月计划", "Today list rolls over after midnight");

        using (var editor = new PlanEditorController(other, (await storage.LoadPlansAsync()).Entries.Where(p => p.Day == other).ToArray(),
            false, interaction, p => calendar.SaveDayAsync(other, p)))
        {
            editor.Plans[0].DeleteCommand.Execute(null);
            Check(editor.Plans.Count == 0 && await editor.SaveAsync(), "Editor supports deleting the last plan of a day");
        }
        Check(calendar.TodayPlans.Count == 0 && (await storage.LoadPlansAsync()).Entries.Length == 2,
            "Deleting all plans for one day preserves other days");
        using (var emptyEditor = new PlanEditorController(today, Array.Empty<PlanEntry>(), true, interaction, p => calendar.SaveDayAsync(today, p)))
        {
            Check(!await emptyEditor.SaveAsync(), "Empty individual plan titles are rejected with an editable draft");
        }
        await calendar.SelectDateAsync(DateOnly.MinValue);
        Check(!calendar.PreviousMonthCommand.CanExecute(null) && calendar.Days.Count == 42, "Minimum supported date is safe");
        await calendar.SelectDateAsync(DateOnly.MaxValue);
        Check(!calendar.NextMonthCommand.CanExecute(null) && calendar.Days.Count == 42, "Maximum supported date is safe");

        using var restoredStore = new RuntimeStore();
        await using var restored = new CalendarPageController(restoredStore, storage, interaction, new Dispatcher(), () => new DateOnly(2028, 2, 29));
        await restored.InitializeAsync();
        Check(restored.TodayPlans.Count == 1 && restored.OnHoldPlans.Count == 1 && restored.AbandonedPlans.Count == 0, "A fresh planner restores remaining active and indefinitely held plans after expired trash cleanup");
        Check(restored.OnHoldPlans[0].DurationMinutes == 30, "A fresh planner restores held plans' saved durations");

        var legacyPath = Path.Combine(testRoot, "LegacyPlans");
        Directory.CreateDirectory(legacyPath);
        await File.WriteAllTextAsync(Path.Combine(legacyPath, "plans.json"),
            "{\"Entries\":[{\"Id\":\"legacy-done\",\"Day\":\"2028-02-29\",\"Title\":\"旧计划\",\"IsCompleted\":true},{\"Id\":\"legacy-todo\",\"Day\":\"2028-02-29\",\"Title\":\"未完成旧计划\",\"IsCompleted\":false}]}");
        var migrated = await new LocalAppStorage(legacyPath).LoadPlansAsync();
        Check(migrated.Entries[0].Status == PlanStatus.Completed && migrated.Entries[1].Status == PlanStatus.Incomplete,
            "Old checkbox data preserves completed and incomplete states after four-state migration");
        Check(migrated.Entries.All(p => p.DurationMinutes == 25), "Old plan JSON without a duration defaults to 25 minutes");
        Check(migrated.Entries.All(p => p.HasPomodoro), "Old saved plans retain their existing focus timers");
        Check(System.Text.Json.JsonSerializer.Deserialize<PlanEntry>(
            "{\"Id\":\"old\",\"Day\":\"2028-02-29\",\"Title\":\"No duration\"}")?.DurationMinutes == 25,
            "PlanEntry constructor keeps backward-compatible JSON defaults");

        var corruptPath = Path.Combine(testRoot, "CorruptPlans");
        Directory.CreateDirectory(corruptPath);
        var corruptFile = Path.Combine(corruptPath, "plans.json");
        await File.WriteAllTextAsync(corruptFile, "{invalid");
        using var badStore = new RuntimeStore();
        await using var bad = new CalendarPageController(badStore, new LocalAppStorage(corruptPath), interaction, new Dispatcher());
        await bad.InitializeAsync();
        Check(!bad.CanEdit && await bad.SaveDayAsync(today, Array.Empty<PlanEntry>()) is not null
            && await File.ReadAllTextAsync(corruptFile) == "{invalid", "Unreadable plans cannot be overwritten");
        await File.WriteAllTextAsync(corruptFile, "{\"Entries\":[]}");
        await bad.InitializeAsync();
        Check(bad.CanEdit, "Loading can be retried after repair");
        await CheckPendingAndOptionalTimersAsync(testRoot);
    }

    private static async Task CheckPendingAndOptionalTimersAsync(string testRoot)
    {
        var storage = new LocalAppStorage(Path.Combine(testRoot, "OptionalPlanningData"));
        using var store = new RuntimeStore();
        var today = new DateOnly(2028, 3, 2);
        var interaction = new Interaction();
        await using var calendar = new CalendarPageController(store, storage, interaction, new Dispatcher(), () => today);
        await calendar.InitializeAsync();
        var plans = new[]
        {
            new PlanEntry("pending-timer", today, "待办番茄钟", DurationMinutes: 15),
            new PlanEntry("pending-note", today, "普通待办", DurationMinutes: double.NaN, HasPomodoro: false),
            new PlanEntry("done", today, "已完成", PlanStatus.Completed),
            new PlanEntry("hold", today, "搁置", PlanStatus.OnHold),
            new PlanEntry("abandoned", today, "放弃", PlanStatus.Abandoned)
        };
        Check(await calendar.SaveDayAsync(today, plans) is null,
            "A plain plan can be saved without requiring an enabled timer duration");
        var saved = (await storage.LoadPlansAsync()).Entries;
        Check(saved.Single(p => p.Id == "pending-note") is { HasPomodoro: false, DurationMinutes: 25 },
            "Disabled invalid duration is normalized to a safe saved default without adding a timer");
        Check(calendar.TodayPlans.Count(plan => plan.IsPending) == 2
            && calendar.TodayPlans.Concat(calendar.OnHoldPlans).Concat(calendar.AbandonedPlans)
                .Where(plan => plan.Title != "待办番茄钟" && plan.Title != "普通待办").All(plan => !plan.IsPending),
            "Only incomplete plans carry the pending marker, independent of timer presence");
        var day = calendar.Days.Single(d => d.Day == today);
        Check(day.HasPendingPlans && day.PendingPlanCount == 2 && day.Description.Contains("未完成 2"),
            "Calendar date marker and accessible description count only pending plans");
        var plain = calendar.TodayPlans.Single(plan => !plan.HasPomodoro);
        Check(plain.DurationText == "" && !plain.StartFocusCommand.CanExecute(null),
            "Plain plans have no timer label or executable focus command");
        var requested = new List<string>();
        calendar.FocusRequested += plan => { requested.Add(plan.Id); return Task.CompletedTask; };
        foreach (var id in new[] { "pending-note", "done", "hold", "abandoned" }) await calendar.StartFocusAsync(id);
        Check(requested.Count == 0, "Direct focus calls reject plans without timers and nonpending states");
        await calendar.StartFocusAsync("pending-timer");
        Check(requested.SequenceEqual(new[] { "pending-timer" }), "Pending timed plans can request focus normally");
        await calendar.ChangeStatusAsync("abandoned", PlanStatus.Incomplete);
        Check(calendar.TodayPlans.Single(plan => plan.Title == "放弃").StartFocusCommand.CanExecute(null),
            "Returning an abandoned timed plan to incomplete makes it focusable again");
        foreach (var id in new[] { "pending-timer", "pending-note", "abandoned" })
            await calendar.ChangeStatusAsync(id, PlanStatus.Abandoned);
        Check(!calendar.Days.Single(d => d.Day == today).HasPendingPlans
            && calendar.TodayPlans.All(plan => !plan.IsPending), "Date and row markers disappear once no incomplete plan remains");
        using var restoredStore = new RuntimeStore();
        await using var restored = new CalendarPageController(restoredStore, storage, interaction, new Dispatcher(), () => today);
        await restored.InitializeAsync();
        Check(restored.AbandonedPlans.Count == 3 && !restored.AbandonedPlans.Single(plan => plan.Title == "普通待办").HasPomodoro,
            "Abandoned states and optional timer choices survive a fresh planner load");
        await CheckBinsAndExpiryAsync(testRoot);
    }

    private static async Task CheckBinsAndExpiryAsync(string testRoot)
    {
        var storage = new LocalAppStorage(Path.Combine(testRoot, "PlanBinsData"));
        var faulty = new FaultStorage(storage);
        using var store = new RuntimeStore();
        var today = new DateOnly(2028, 4, 4);
        var interaction = new Interaction();
        await using var calendar = new CalendarPageController(store, faulty, interaction, new Dispatcher(), () => today);
        await calendar.InitializeAsync();
        var past = today.AddDays(-10);
        Check(await calendar.SaveDayAsync(past, new[]
        {
            new PlanEntry("held", past, "长期搁置", PlanStatus.OnHold, HasPomodoro: false),
            new PlanEntry("done", past, "历史完成", PlanStatus.Completed),
            new PlanEntry("trash", past, "今天放弃", PlanStatus.Abandoned)
        }) is null, "Saving plans can put past-date entries into independent boxes");
        Check(store.Get<PlanState>().Entries.Single(p => p.Id == "trash").AbandonedOn == today,
            "Trash expiry is based on discard date rather than original planned date");
        Check(calendar.OnHoldPlans.Single().Title == "长期搁置" && calendar.AbandonedPlans.Count == 1,
            "Held and discarded plans are available across planning dates");
        Check(!calendar.OnHoldPlans.Single().RunLeisureCommand.CanExecute(null),
            "An untimed held plan cannot start leisure until a timer is added");
        interaction.EditorAction = async editor =>
        {
            Check(editor.Plans.Count == 1 && editor.Plans[0].Id == "held", "Editing a held title opens that exact isolated plan");
            editor.Plans[0].HasPomodoro = true;
            editor.Plans[0].DurationMinutes = "12.5";
            Check(await editor.SaveAsync(), "A timer can be added directly inside the on-hold box");
        };
        calendar.OnHoldPlans.Single().EditCommand.Execute(null);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while (!calendar.CanEdit) await Task.Delay(10, timeout.Token);
        Check(store.Get<PlanState>().Entries.Single(p => p.Id == "held") is { Status: PlanStatus.OnHold, HasPomodoro: true, DurationMinutes: 12.5 }
            && store.Get<PlanState>().Entries.Length == 3 && calendar.AbandonedPlans.Count == 1,
            "Editing a held timer preserves its held status and all unrelated plans");
        interaction.EditorAction = null;
        PlanEntry? leisure = null;
        calendar.LeisureFocusRequested += plan => { leisure = plan; return Task.CompletedTask; };
        await calendar.RunLeisureAsync("held");
        Check(leisure?.Id == "held" && store.Get<PlanState>().Entries.Single(p => p.Id == "held").Status == PlanStatus.OnHold,
            "Leisure execution forwards held work without changing its held status");
        await calendar.RestoreAsync("trash");
        Check(calendar.TodayPlans.Any(p => p.Title == "今天放弃" && p.IsPending)
            && store.Get<PlanState>().Entries.Single(p => p.Id == "trash").AbandonedOn is null,
            "Same-day recovery moves a discarded plan into today's incomplete schedule");
        await calendar.ChangeStatusAsync("trash", PlanStatus.Abandoned);
        today = today.AddDays(1);
        faulty.FailWrite = true;
        await calendar.RefreshTodayAsync();
        Check(calendar.AbandonedPlans.Count == 1 && store.Get<PlanState>().Entries.Any(p => p.Id == "trash")
            && calendar.StatusMessage.Length > 0, "Failed trash cleanup preserves authoritative entries and reports the failure");
        faulty.FailWrite = false;
        await calendar.RefreshTodayAsync();
        Check(calendar.AbandonedPlans.Count == 0 && !(await storage.LoadPlansAsync()).Entries.Any(p => p.Id == "trash"),
            "Expired trash is removed only after successful persistence");
        Check(calendar.OnHoldPlans.Count == 1 && store.Get<PlanState>().Entries.Any(p => p.Id == "done"),
            "Day rollover never clears indefinitely held or completed plans");
        await calendar.RestoreAsync("held");
        Check(calendar.OnHoldPlans.Count == 0 && calendar.TodayPlans.Single().IsPending,
            "An indefinitely held plan can rejoin today's schedule");
        // Old data has no AbandonedOn. Its original day provides the conservative expiry fallback.
        var state = store.Get<PlanState>();
        await storage.SavePlansAsync(state with { Entries = state.Entries.Add(new PlanEntry("legacy-trash", past, "旧垃圾", PlanStatus.Abandoned)) });
        using var freshStore = new RuntimeStore();
        await using var fresh = new CalendarPageController(freshStore, storage, new Interaction(), new Dispatcher(), () => today);
        await fresh.InitializeAsync();
        Check(fresh.AbandonedPlans.Count == 0 && freshStore.Get<PlanState>().Entries.All(p => p.Id != "legacy-trash"),
            "Startup prunes expired legacy trash while preserving other saved entries");
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class FaultStorage(IPlanStorage storage) : IPlanStorage
    {
        public bool FailWrite { get; set; }
        public Task<PlanState> LoadPlansAsync() => storage.LoadPlansAsync();
        public Task SavePlansAsync(PlanState plans) => FailWrite ? Task.FromException(new IOException("Disk unavailable")) : storage.SavePlansAsync(plans);
    }
    private sealed class Dispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
    }
    private sealed class Interaction : IUserInteraction
    {
        public int EditorOpened { get; private set; }
        public Func<PlanEditorController, Task>? EditorAction { get; set; }
        public bool Confirm { get; set; }
        public SaveDecision Decision { get; set; }
        public async Task EditPlansAsync(DateOnly day, IReadOnlyList<PlanEntry> plans, bool addNew, Func<IReadOnlyList<PlanEntry>, Task<string?>> save)
        {
            EditorOpened++;
            using var editor = new PlanEditorController(day, plans, addNew, this, save);
            if (EditorAction is not null) await EditorAction(editor);
        }
        public Task<SaveDecision> ResolveUnsavedAsync(string title, string message) => Task.FromResult(Decision);
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
        public Task<string[]> PickAudioFilesAsync() => Task.FromResult(Array.Empty<string>());
        public Task<string?> PickSaveFileAsync(string suggestedName) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(Confirm);
        public void ShowMessage(string title, string message) => throw new InvalidOperationException(message);
        public void OpenFolder(string path) => throw new InvalidOperationException(path);
    }
}
