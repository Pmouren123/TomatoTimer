using System.IO;
using System.Windows.Input;
using TomatoTimerPro.Contracts.Interaction;
using TomatoTimerPro.Contracts.Navigation;
using TomatoTimerPro.Contracts.Threading;
using TomatoTimerPro.Contracts.Timing;
using TomatoTimerPro.Controllers.Components.Countdown;
using TomatoTimerPro.Controllers.Pages.Timer;
using TomatoTimerPro.Infrastructure.Persistence;
using TomatoTimerPro.RuntimeStore.Implementation;
using TomatoTimerPro.RuntimeStore.States.Timer;

public static class TaskExitChecks
{
    public static async Task RunAsync(string testRoot)
    {
        var storage = new LocalAppStorage(Path.Combine(testRoot, "TaskExitData"));
        var workspace = Path.Combine(testRoot, "TaskExitWorkspace");
        using var store = new RuntimeStore();
        var clock = new FakeClock();
        var interaction = new FakeInteraction();
        await using var countdown = new CountdownController(store, storage, new ImmediateDispatcher(), clock);
        await using var page = new TimerPageController(countdown, storage, interaction, new FakeNavigator());
        page.RestoreWorkspacePath(workspace);
        var scheduled = new TomatoTimerPro.RuntimeStore.States.Planning.PlanEntry(
            Guid.NewGuid().ToString("N"), DateOnly.FromDateTime(clock.LocalNow.LocalDateTime), "日程专注", DurationMinutes: 37.5);
        await page.StartPlanAsync(scheduled);
        var scheduledId = countdown.Current?.Id;
        Check(countdown.IsRunning && countdown.Current?.Title == scheduled.Title && countdown.Current.PlannedSeconds == 2250,
            "Starting a schedule applies its saved title and duration");
        await page.StartPlanAsync(scheduled with { Title = "不能覆盖正在进行的任务", DurationMinutes = 5 });
        Check(countdown.Current?.Id == scheduledId && countdown.Current?.PlannedSeconds == 2250 && page.TaskTitle == scheduled.Title,
            "Starting another schedule cannot overwrite an active task or its prepared settings");
        Check(interaction.Messages.Count == 1, "An active task is reported explicitly when another schedule is requested");
        interaction.Messages.Clear();
        await countdown.DeleteCurrentRecordAsync();
        var completed = 0;
        var stoppedAlarms = 0;
        countdown.Completed += _ => completed++;
        page.StopAlarmRequested += () => stoppedAlarms++;

        await countdown.StartAsync(workspace, "Paused task", TimeSpan.FromMinutes(10), null);
        clock.Advance(12);
        await countdown.PauseResumeAsync();
        interaction.EndDecision = SaveDecision.Cancel;
        await page.EndTaskAsync();
        Check(countdown.IsPaused && !page.StartCommand.CanExecute(null), "Dismissing end prompt keeps the paused task active");

        interaction.EndDecision = SaveDecision.Save;
        await page.EndTaskAsync();
        Check(countdown.Current is { Status: SessionStatus.EndedEarly, FocusedSeconds: 12 }, "Early finish preserves measured focused time");
        Check(completed == 0 && stoppedAlarms == 1, "Early finish stops pending alarm without firing natural completion");
        Check(page.StartCommand.CanExecute(null) && page.WriteReflectionCommand.CanExecute(null), "Early finish unlocks next task and optional reflection");
        Check(page.StatusText == "提前结束", "Early finish has its own visible status");
        Check(!File.Exists(storage.GetReflectionPath(countdown.Current!))
            && !File.Exists(Path.Combine(storage.GetDayDirectory(workspace, countdown.Current!.Day), "Daily.md")),
            "Early finish creates timing statistics only; optional reflection files and daily notes are not generated");

        page.TaskTitle = "Next task without writing records";
        page.DurationMinutes = "1";
        await ExecuteAsync(page.StartCommand, () => countdown.IsRunning);
        await countdown.TickAsync();
        Check(countdown.IsRunning, "A new task starts immediately without opening or writing reflection");
        clock.Advance(3);
        interaction.EndDecision = SaveDecision.Discard;
        interaction.Confirm = true;
        await page.EndTaskAsync();
        Check(countdown.Current is null && page.StartCommand.CanExecute(null), "Confirmed discard deletes current task and releases the timer");
        Check(completed == 0 && stoppedAlarms == 2, "Discard stops the alarm without finish bell");

        await ExecuteAsync(page.StartCommand, () => countdown.IsRunning);
        await countdown.TickAsync();
        interaction.Confirm = false;
        await ExecuteAsync(page.CancelCommand);
        Check(countdown.IsRunning, "Rejecting explicit abandon confirmation leaves the task running");
        interaction.Confirm = true;
        await ExecuteAsync(page.CancelCommand, () => !countdown.IsActive);
        await countdown.TickAsync();
        Check(!countdown.IsActive && page.StartCommand.CanExecute(null), "Confirming abandon unlocks a new task");
        var history = await storage.LoadSessionsAsync(workspace, DateOnly.FromDateTime(clock.LocalNow.LocalDateTime));
        Check(history.Count == 1 && history.Any(session => session.Status == SessionStatus.EndedEarly), "Confirmed deletion removes only selected tasks and preserves unrelated history");

        page.DurationMinutes = "not a duration";
        await ExecuteAsync(page.StartCommand);
        Check(interaction.Messages.Any(message => message.Contains("有效时长")), "Invalid input opens an explicit message dialog");
    }

    private static async Task ExecuteAsync(ICommand command, Func<bool>? finished = null)
    {
        Check(command.CanExecute(null), "Expected command to be available");
        command.Execute(null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!(finished?.Invoke() ?? command.CanExecute(null)))
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeClock : IClock
    {
        public long Milliseconds { get; private set; }
        public DateTimeOffset LocalNow { get; private set; } = new(2026, 10, 29, 12, 0, 0, TimeSpan.FromHours(8));
        public void Advance(int seconds) { Milliseconds += seconds * 1000L; LocalNow = LocalNow.AddSeconds(seconds); }
    }
    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
    }
    private sealed class FakeNavigator : IPageNavigator
    {
        public Task<bool> NavigateAsync(string pageId) => Task.FromResult(true);
    }
    private sealed class FakeInteraction : IUserInteraction
    {
        public SaveDecision EndDecision { get; set; }
        public bool Confirm { get; set; }
        public List<string> Messages { get; } = [];
        public Task<SaveDecision> ChooseTaskEndAsync(string taskTitle) => Task.FromResult(EndDecision);
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
        public Task<string[]> PickAudioFilesAsync() => Task.FromResult(Array.Empty<string>());
        public Task<string?> PickSaveFileAsync(string suggestedName) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(Confirm);
        public Task<SaveDecision> ResolveUnsavedAsync(string title, string message) => Task.FromResult(SaveDecision.Cancel);
        public void ShowMessage(string title, string message) => Messages.Add(message);
        public void OpenFolder(string path) { }
    }
}
