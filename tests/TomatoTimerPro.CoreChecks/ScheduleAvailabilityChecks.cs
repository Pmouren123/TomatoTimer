using System.IO;
using TomatoTimerPro.Contracts.Interaction;
using TomatoTimerPro.Contracts.Navigation;
using TomatoTimerPro.Contracts.Threading;
using TomatoTimerPro.Controllers.Components.Countdown;
using TomatoTimerPro.Controllers.Pages.Calendar;
using TomatoTimerPro.Controllers.Pages.Timer;
using TomatoTimerPro.Infrastructure.Persistence;
using TomatoTimerPro.RuntimeStore.Implementation;
using TomatoTimerPro.RuntimeStore.States.Planning;

public static class ScheduleAvailabilityChecks
{
    public static async Task RunAsync(string testRoot)
    {
        var storage = new LocalAppStorage(Path.Combine(testRoot, "ScheduleAvailability"));
        using var store = new RuntimeStore();
        var ui = new Interaction();
        await using var calendar = new CalendarPageController(store, storage, ui, new Dispatcher());
        await using var countdown = new CountdownController(store, storage, new Dispatcher());
        await using var timer = new TimerPageController(countdown, storage, ui, new Navigator(), calendar);
        await calendar.InitializeAsync();
        var invocations = 0;
        timer.ScheduleStartRequested += () => { invocations++; return Task.CompletedTask; };
        timer.SelectScheduleModeCommand.Execute(null);
        Check(!timer.StartCommand.CanExecute(null), "An empty schedule cannot start");
        timer.StartCommand.Execute(null);
        Check(invocations == 0 && countdown.Current is null, "An empty schedule does not dispatch a timer start");
        var day = DateOnly.FromDateTime(DateTime.Now);
        var plain = new PlanEntry(Guid.NewGuid().ToString("N"), day, "普通计划", HasPomodoro: false);
        var completed = plain with { Id = Guid.NewGuid().ToString("N"), Status = PlanStatus.Completed, HasPomodoro = true };
        var held = completed with { Id = Guid.NewGuid().ToString("N"), Status = PlanStatus.OnHold };
        var abandoned = completed with { Id = Guid.NewGuid().ToString("N"), Status = PlanStatus.Abandoned };
        var future = completed with { Id = Guid.NewGuid().ToString("N"), Day = day.AddDays(1), Status = PlanStatus.Incomplete };
        Check(await calendar.SaveDayAsync(day, new[] { plain, completed, held, abandoned }) is null
            && await calendar.SaveDayAsync(future.Day, new[] { future }) is null,
            "Non-runnable and future plans save normally");
        Check(!timer.HasRunnablePlans && !timer.StartCommand.CanExecute(null),
            "Plain, finished, held, abandoned and future plans cannot unlock today's schedule");
        var changed = 0;
        timer.StartCommand.CanExecuteChanged += (_, _) => changed++;
        await calendar.RestoreAsync(held.Id);
        Check(timer.HasRunnablePlans && timer.StartCommand.CanExecute(null) && changed > 0,
            "Restoring a timed plan into today's schedule immediately enables start");
        await calendar.ChangeStatusAsync(held.Id, PlanStatus.Completed);
        Check(!timer.HasRunnablePlans && !timer.StartCommand.CanExecute(null),
            "Completing the last runnable plan immediately disables schedule start");
        timer.SelectLeisureModeCommand.Execute(null);
        Check(timer.StartCommand.CanExecute(null), "Leisure mode can start with no runnable schedule");
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private sealed class Dispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
    }
    private sealed class Navigator : IPageNavigator
    { public Task<bool> NavigateAsync(string id) => Task.FromResult(true); }
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
}
