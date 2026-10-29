using System.IO;
using TomatoTimerPro.Contracts.Interaction;
using TomatoTimerPro.Contracts.Storage;
using TomatoTimerPro.Contracts.Threading;
using TomatoTimerPro.Contracts.Timing;
using TomatoTimerPro.Controllers.Components.Countdown;
using TomatoTimerPro.Controllers.Components.Schedule;
using TomatoTimerPro.Controllers.Pages.Calendar;
using TomatoTimerPro.Infrastructure.Persistence;
using TomatoTimerPro.RuntimeStore.Implementation;
using TomatoTimerPro.RuntimeStore.States.Planning;
using TomatoTimerPro.RuntimeStore.States.Settings;
using TomatoTimerPro.RuntimeStore.States.Timer;

public static class DayTimeAllocationChecks
{
    public static async Task RunAsync(string testRoot)
    {
        var root = Path.Combine(testRoot, "DayTimeAllocation");
        var workspace = Path.Combine(root, "First");
        var otherWorkspace = Path.Combine(root, "Second");
        var today = new DateOnly(2026, 10, 29);
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, DateTimeOffset.Now.Offset);
        var local = new LocalAppStorage(Path.Combine(root, "AppData"));
        var storage = new ControlledStorage(local);
        using var store = new RuntimeStore();
        var dispatcher = new Dispatcher();
        var clock = new Clock(now);
        await using var calendar = new CalendarPageController(store, local, new Interaction(), dispatcher, () => today);
        await using var countdown = new CountdownController(store, storage, dispatcher, clock);
        await using var allocation = new DayTimeAllocationController(calendar, countdown, storage, () => today);
        await calendar.InitializeAsync();
        var plans = new[]
        {
            new PlanEntry("pending", today, "Thirty minutes", DurationMinutes: 30),
            new PlanEntry("completed", today, "Completed hour", PlanStatus.Completed, 60),
            new PlanEntry("plain", today, "Untimed", HasPomodoro: false),
            new PlanEntry("hold", today, "Held", PlanStatus.OnHold, 40),
            new PlanEntry("trash", today, "Abandoned", PlanStatus.Abandoned, 50)
        };
        Check(await calendar.SaveDayAsync(today, plans) is null, "Allocation plans save");
        var historical = new TimerSession
        {
            Id = Guid.NewGuid().ToString("N"), WorkspacePath = workspace, StartedAt = now.AddHours(-1),
            PlannedSeconds = 3600, FocusedSeconds = 20, Status = SessionStatus.EndedEarly
        };
        await storage.SaveSessionAsync(historical);
        await storage.SaveSessionAsync(historical with
        { Id = "yesterday", StartedAt = now.AddDays(-1), FocusedSeconds = 1000 });
        await storage.SaveSessionAsync(historical with
        { Id = "other-workspace", WorkspacePath = otherWorkspace, FocusedSeconds = 400 });
        await allocation.SetWorkspaceAsync(workspace);
        Check(allocation.HasPlans && allocation.Segments.Count == 2 && allocation.PlannedSeconds == 5400,
            "Today's completed and incomplete timed plans allocate time; other statuses and untimed plans do not");
        Check(Math.Abs(allocation.Segments.Sum(segment => segment.Fraction) - 1) < .000001
            && allocation.Segments.All(segment => segment.Fraction is > 0 and <= 1
                && segment.Title.Length > 0 && segment.DurationText.Length > 0), "Segments expose normalized fractions and labels");
        Check(allocation.ActualSeconds == 20, "Actual work uses focused seconds in this workspace's start-day folder");
        await calendar.ChangeStatusAsync("pending", PlanStatus.Completed);
        Check(allocation.ActualSeconds == 20 && allocation.PlannedSeconds == 5400,
            "Manual plan completion cannot inflate actual work");

        await countdown.StartAsync(workspace, "Live", TimeSpan.FromMinutes(25), null);
        await allocation.RefreshAsync();
        var reads = storage.Reads.Count;
        clock.Advance(7);
        await countdown.TickAsync();
        Check(allocation.ActualSeconds == 27, "Realtime focused seconds overlay the saved session ID without duplication");
        Check(storage.Reads.Count == reads, "A normal running tick never rereads history");
        await countdown.PauseResumeAsync();
        clock.Advance(120);
        await countdown.TickAsync();
        Check(allocation.ActualSeconds == 27, "Paused time cannot grow the work total");
        storage.DuplicateRows = true;
        await allocation.RefreshAsync();
        Check(allocation.ActualSeconds == 27, "Repeated metadata IDs count once");
        storage.DuplicateRows = false;
        await countdown.DeleteCurrentRecordAsync();
        await allocation.RefreshAsync();
        Check(allocation.ActualSeconds == 20, "Deleting the current record removes its focused time");
        await storage.DeleteSessionAsync(historical);
        await allocation.RefreshAsync();
        Check(allocation.ActualSeconds == 0, "Explicit history refresh removes deleted saved records");

        await storage.SaveSessionAsync(historical);
        storage.BlockNext = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = storage.BlockNext;
        var pending = allocation.RefreshAsync();
        await storage.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await allocation.SetWorkspaceAsync(otherWorkspace);
        Check(allocation.ActualSeconds == 400, "Switching workspace replaces measured focus");
        blocked.SetResult();
        await pending;
        Check(allocation.ActualSeconds == 400 && allocation.WorkspacePath == Path.GetFullPath(otherWorkspace),
            "An older workspace read cannot overwrite a later workspace");

        today = today.AddDays(1);
        await calendar.RefreshTodayAsync();
        await allocation.RefreshAsync();
        Check(allocation.ActualSeconds == 0 && !allocation.HasPlans,
            "Midnight clears yesterday's allocation and uses the new start-day folder");
        Check(storage.Reads.All(read => read.Day == new DateOnly(2026, 10, 29) || read.Day == today),
            "Allocation reads today's folder only, never scans older history");
        await storage.SaveSessionAsync(historical with
        { Id = "cross-midnight", WorkspacePath = otherWorkspace, StartedAt = now.Date.AddDays(1).AddSeconds(-1), FocusedSeconds = 80 });
        await allocation.RefreshAsync();
        Check(allocation.ActualSeconds == 0, "Cross-midnight work remains attributed to the session's start day");

        var notifications = 0;
        allocation.Changed += () => notifications++;
        await allocation.DisposeAsync();
        var before = notifications;
        await allocation.RefreshAsync();
        await calendar.RefreshTodayAsync();
        Check(notifications == before, "Disposed allocation ignores new loads and notifications");
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Dispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
    }
    private sealed class Clock(DateTimeOffset now) : IClock
    {
        public long Milliseconds { get; private set; }
        public DateTimeOffset LocalNow { get; private set; } = now;
        public void Advance(double seconds) { Milliseconds += (long)(seconds * 1000); LocalNow = LocalNow.AddSeconds(seconds); }
    }
    private sealed class Interaction : IUserInteraction
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
        public Task<string[]> PickAudioFilesAsync() => Task.FromResult(Array.Empty<string>());
        public Task<string?> PickSaveFileAsync(string suggestedName) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);
        public Task<SaveDecision> ResolveUnsavedAsync(string title, string message) => Task.FromResult(SaveDecision.Cancel);
        public void ShowMessage(string title, string message) => throw new InvalidOperationException(message);
        public void OpenFolder(string path) => throw new InvalidOperationException(path);
    }
    private sealed class ControlledStorage(IAppStorage inner) : IAppStorage
    {
        public List<(string Workspace, DateOnly Day)> Reads { get; } = [];
        public bool DuplicateRows { get; set; }
        public TaskCompletionSource? BlockNext { get; set; }
        public TaskCompletionSource LoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string AppDataDirectory => inner.AppDataDirectory;
        public Task<AppPreferences> LoadPreferencesAsync() => inner.LoadPreferencesAsync();
        public Task SavePreferencesAsync(AppPreferences value) => inner.SavePreferencesAsync(value);
        public string GetDayDirectory(string workspace, DateOnly day) => inner.GetDayDirectory(workspace, day);
        public Task<string> PrepareDayAsync(string workspace, DateOnly day) => inner.PrepareDayAsync(workspace, day);
        public string GetSessionMetadataPath(TimerSession session) => inner.GetSessionMetadataPath(session);
        public string GetReflectionPath(TimerSession session) => inner.GetReflectionPath(session);
        public Task SaveSessionAsync(TimerSession session) => inner.SaveSessionAsync(session);
        public Task DeleteSessionAsync(TimerSession session) => inner.DeleteSessionAsync(session);
        public async Task<IReadOnlyList<TimerSession>> LoadSessionsAsync(string workspace, DateOnly day)
        {
            Reads.Add((workspace, day));
            var block = BlockNext; BlockNext = null;
            var rows = await inner.LoadSessionsAsync(workspace, day);
            if (block is not null) { LoadStarted.TrySetResult(); await block.Task; }
            return DuplicateRows ? rows.Concat(rows).ToArray() : rows;
        }
        public Task<DocumentSnapshot> ReadDocumentAsync(string path, string defaultText = "") => inner.ReadDocumentAsync(path, defaultText);
        public Task SaveDocumentAsync(DocumentSnapshot original, string content, bool overwrite = false) => inner.SaveDocumentAsync(original, content, overwrite);
        public Task SaveRecoveryAsync(string path, string content) => inner.SaveRecoveryAsync(path, content);
        public Task<IReadOnlyList<RecoveryDraft>> LoadRecoveryDraftsAsync() => inner.LoadRecoveryDraftsAsync();
        public Task DeleteRecoveryAsync(string id) => inner.DeleteRecoveryAsync(id);
        public Task<TimerSession?> LoadActiveSessionAsync() => inner.LoadActiveSessionAsync();
        public Task SaveActiveSessionAsync(TimerSession? session) => inner.SaveActiveSessionAsync(session);
    }
}
