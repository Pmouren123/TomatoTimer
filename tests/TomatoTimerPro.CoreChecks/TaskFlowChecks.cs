using System.IO;
using TomatoTimerPro.Contracts.Interaction;
using TomatoTimerPro.Contracts.Threading;
using TomatoTimerPro.Contracts.Timing;
using TomatoTimerPro.Controllers.Components.Countdown;
using TomatoTimerPro.Controllers.Pages.Records;
using TomatoTimerPro.Controllers.Pages.Timer;
using TomatoTimerPro.Infrastructure.Persistence;
using TomatoTimerPro.Managers.Pages;
using TomatoTimerPro.RuntimeStore.Implementation;
using TomatoTimerPro.RuntimeStore.States.Timer;
using TomatoTimerPro.Tools.Markdown;

public static class TaskFlowChecks
{
    public static async Task RunAsync(string testRoot)
    {
        var directory = Path.Combine(testRoot, "TaskFlow");
        var storage = new LocalAppStorage(Path.Combine(directory, "Data"));
        var workspace = Path.Combine(directory, "Workspace");
        var date = new DateTime(2026, 10, 29);
        var clock = new FlowClock(new DateTimeOffset(date.AddHours(9), DateTimeOffset.Now.Offset));
        var interaction = new FlowInteraction();
        using var store = new RuntimeStore();
        await using var countdown = new CountdownController(store, storage, new ImmediateDispatcher(), clock);
        await using var navigator = new PageMgr();
        await using var records = new RecordsPageController(storage, interaction, navigator);
        await using var timer = new TimerPageController(countdown, storage, interaction, navigator);
        navigator.Register("timer", () => new PageScope(new object(), new PageLifecycle()), true);
        navigator.Register("records", () => new PageScope(new object(), new PageLifecycle(canLeave: records.CanLeaveAsync)), true);
        await navigator.NavigateAsync("timer");
        await records.ChangeContextAsync(workspace, date);
        records.EditorText = "Original saved daily note";
        Check(await records.SaveAsync(), "Baseline daily note saves");
        var notePath = Path.Combine(storage.GetDayDirectory(workspace, DateOnly.FromDateTime(date)), "Daily.md");
        records.EditorText = "Unsaved thoughts must not trap the task";
        interaction.UnsavedChoice = SaveDecision.Cancel;
        await navigator.NavigateAsync("records");
        Check(!await navigator.NavigateAsync("timer"), "Cancelling the note prompt preserves records page");
        Check(navigator.CurrentId == "records" && records.IsDirty, "Dirty editor and page remain intact after refusing navigation");

        var completions = 0;
        countdown.Completed += _ => completions++;
        await countdown.StartAsync(workspace, "Finish while editing notes", TimeSpan.FromMinutes(25), null);
        clock.Advance(12);
        interaction.TaskChoice = SaveDecision.Cancel;
        await timer.EndTaskAsync();
        Check(countdown.IsRunning && records.IsDirty, "Cancelling the task dialog keeps both countdown and note edits");
        interaction.TaskChoice = SaveDecision.Save;
        await timer.EndTaskAsync();
        Check(countdown.Current is { Status: SessionStatus.EndedEarly, FocusedSeconds: 12 },
            "Finishing early preserves actual focus rather than planned duration");
        Check(!countdown.IsActive && timer.StartCommand.CanExecute(null), "Early end immediately permits a new task");
        Check(completions == 0, "Early end does not emit a natural-completion alarm");
        Check(records.IsDirty && navigator.CurrentId == "records", "Task end is independent of dirty record navigation");
        Check(await storage.LoadActiveSessionAsync() is null, "Early end clears active checkpoint");
        var savedEarly = (await storage.LoadSessionsAsync(workspace, DateOnly.FromDateTime(date)))
            .Single(session => session.Id == countdown.Current!.Id);
        Check(savedEarly.Status == SessionStatus.EndedEarly && savedEarly.FocusedSeconds == 12,
            "Early-end status and measured focus persist in history");
        Check(!File.Exists(storage.GetReflectionPath(savedEarly)) && await File.ReadAllTextAsync(notePath) == "Original saved daily note",
            "Ending a timer preserves statistics without automatically creating or appending any reflection");

        await countdown.StartAsync(workspace, "Abandon while editing notes", TimeSpan.FromMinutes(10), null);
        var deletedSession = countdown.Current!;
        var deletedMetadataPath = storage.GetSessionMetadataPath(deletedSession);
        var deletedReflectionPath = storage.GetReflectionPath(deletedSession);
        var beforeReflection = await storage.ReadDocumentAsync(notePath);
        await storage.SaveDocumentAsync(beforeReflection, DailyReflection.Upsert(beforeReflection.Content, deletedSession,
            "Reflection belonging only to the discarded task"));
        clock.Advance(4);
        interaction.TaskChoice = SaveDecision.Discard;
        interaction.Confirm = false;
        await timer.EndTaskAsync();
        Check(countdown.IsRunning && countdown.Current?.Id == deletedSession.Id && File.Exists(deletedMetadataPath)
            && DailyReflection.Read(await File.ReadAllTextAsync(notePath), deletedSession.Id) is not null,
            "Refusing destructive confirmation keeps task statistics and the associated daily reflection");
        interaction.Confirm = true;
        await timer.EndTaskAsync();
        Check(countdown.Current is null && !countdown.IsActive && !File.Exists(deletedMetadataPath) && !File.Exists(deletedReflectionPath)
            && DailyReflection.Read(await File.ReadAllTextAsync(notePath), deletedSession.Id) is null,
            "Confirmed discard removes only current metadata and associated daily reflection and clears current state");
        Check(records.IsDirty && records.EditorText == "Unsaved thoughts must not trap the task",
            "Abandoning a task does not discard unrelated note edits");
        Check(timer.StartCommand.CanExecute(null), "Abandoning a task re-enables creating the next one");
        Check(await storage.LoadActiveSessionAsync() is null, "Confirmed deletion clears the active task checkpoint");
        Check((await File.ReadAllTextAsync(notePath)).TrimEnd() == "Original saved daily note", "Deleting the current task does not delete the daily summary");

        interaction.Confirm = false;
        await records.DiscardAndLeaveAsync();
        Check(records.IsDirty && navigator.CurrentId == "records", "Refusing discard preserves unsaved note and page");
        interaction.Confirm = true;
        await records.DiscardAndLeaveAsync();
        Check(!records.IsDirty && navigator.CurrentId == "timer", "Explicit discard leaves records immediately without another save prompt");
        Check(records.EditorText == "Original saved daily note" && (await File.ReadAllTextAsync(notePath)).TrimEnd() == "Original saved daily note",
            "Discard only resets unsaved editing and never changes the saved daily note");

        await countdown.StartAsync(workspace, "Old dialog session", TimeSpan.FromSeconds(2), null);
        var oldId = countdown.Current!.Id;
        interaction.DelayedTaskChoice = new TaskCompletionSource<SaveDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingEnd = timer.EndTaskAsync();
        Check(interaction.TaskPromptCount >= 4, "Task prompt started before the simulated session completion");
        clock.Advance(2);
        await countdown.TickAsync();
        await countdown.StartAsync(workspace, "New session after natural completion", TimeSpan.FromMinutes(5), null);
        var newId = countdown.Current!.Id;
        interaction.DelayedTaskChoice.SetResult(SaveDecision.Discard);
        await pendingEnd;
        interaction.DelayedTaskChoice = null;
        Check(newId != oldId && countdown.Current?.Id == newId && countdown.IsRunning,
            "Resolving a stale task dialog cannot cancel a different session");
        await countdown.CancelAsync();
        var remainingSessions = await storage.LoadSessionsAsync(workspace, DateOnly.FromDateTime(date));
        Check(remainingSessions.Count == 3 && remainingSessions.All(session => session.Id != deletedSession.Id)
            && remainingSessions.Any(session => session.Id == savedEarly.Id),
            "Confirmed deletion affects only its selected task while early-ended and other history remain");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Task flow check failed: " + message);
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
    }

    private sealed class FlowClock(DateTimeOffset now) : IClock
    {
        public long Milliseconds { get; private set; }
        public DateTimeOffset LocalNow { get; private set; } = now;
        public void Advance(double seconds)
        {
            Milliseconds += (long)(seconds * 1000);
            LocalNow = LocalNow.AddSeconds(seconds);
        }
    }

    private sealed class FlowInteraction : IUserInteraction
    {
        public SaveDecision TaskChoice { get; set; } = SaveDecision.Cancel;
        public SaveDecision UnsavedChoice { get; set; } = SaveDecision.Cancel;
        public TaskCompletionSource<SaveDecision>? DelayedTaskChoice { get; set; }
        public int TaskPromptCount { get; private set; }
        public bool Confirm { get; set; } = true;
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
        public Task<string[]> PickAudioFilesAsync() => Task.FromResult(Array.Empty<string>());
        public Task<string?> PickSaveFileAsync(string suggestedName) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(Confirm);
        public Task<SaveDecision> ResolveUnsavedAsync(string title, string message) => Task.FromResult(UnsavedChoice);
        public Task<SaveDecision> ChooseTaskEndAsync(string taskTitle)
        {
            TaskPromptCount++;
            return DelayedTaskChoice?.Task ?? Task.FromResult(TaskChoice);
        }
        public void ShowMessage(string title, string message) { }
        public void OpenFolder(string path) { }
    }
}
