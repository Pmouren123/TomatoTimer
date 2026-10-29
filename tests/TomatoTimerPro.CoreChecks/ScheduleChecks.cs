using System.IO;
using TomatoTimerPro.Contracts.Threading;
using TomatoTimerPro.Contracts.Timing;
using TomatoTimerPro.Controllers.Components.Countdown;
using TomatoTimerPro.Controllers.Components.Schedule;
using TomatoTimerPro.Infrastructure.Persistence;
using TomatoTimerPro.RuntimeStore.Implementation;
using TomatoTimerPro.RuntimeStore.States.Planning;
using TomatoTimerPro.RuntimeStore.States.Timer;

public static class ScheduleChecks
{
    public static async Task RunAsync(string testRoot)
    {
        await CheckOrderedCompletionAsync(testRoot);
        await CheckStopDuringMarkAsync(testRoot);
        await CheckRefusalAndFailuresAsync(testRoot);
        await CheckManualTerminationAsync(testRoot);
    }

    private static async Task CheckOrderedCompletionAsync(string testRoot)
    {
        await using var fixture = new Fixture(testRoot, "Ordered");
        var day = fixture.Day;
        var plans = new List<PlanEntry>
        {
            Plan(day, "Already completed", PlanStatus.Completed),
            Plan(day, "First"),
            Plan(day, "On hold", PlanStatus.OnHold),
            Plan(day, "No pomodoro") with { HasPomodoro = false },
            Plan(day, "Second")
        };
        await fixture.Schedule.StartAsync(plans);
        Check(fixture.Started.SequenceEqual(new[] { "First" }), "Only first eligible plan starts");
        Check(fixture.Schedule.Entries.Select(row => row.Title).SequenceEqual(new[] { "First", "Second" }),
            "Queue snapshot preserves source order and skips completed, on-hold and unscheduled plans");
        Check(fixture.Schedule.Entries[0].IsCurrent && fixture.Schedule.Entries[1].IsUpcoming
            && fixture.Schedule.PositionText == "1 / 2", "Visual pointer identifies current and upcoming rows");
        plans.Add(Plan(day, "Added after snapshot"));
        fixture.Clock.Advance(2);
        await fixture.Countdown.TickAsync();
        Check(fixture.Schedule.Progress is > 0 and < 50, "Queue progress includes measured current-task progress");
        await fixture.Countdown.PauseResumeAsync();
        fixture.Clock.Advance(100);
        await fixture.Countdown.TickAsync();
        Check(fixture.Started.Count == 1 && fixture.Schedule.IsActive && fixture.Countdown.IsPaused,
            "Manual pause does not advance the schedule");
        await fixture.Countdown.PauseResumeAsync();
        fixture.Clock.Advance(8);
        await fixture.Countdown.TickAsync();
        await WaitAsync(() => fixture.Started.Count == 2 && fixture.Countdown.Current?.Title == "Second");
        await fixture.Countdown.TickAsync();
        Check(fixture.Marked.SequenceEqual(new[] { "First" }) && fixture.Schedule.Entries[0].IsCompleted
            && fixture.Schedule.Entries[1].IsCurrent, "Completion is saved before the visual pointer advances");
        fixture.Clock.Advance(10);
        await fixture.Countdown.TickAsync();
        await WaitAsync(() => !fixture.Schedule.IsActive && fixture.Marked.Count == 2);
        Check(fixture.Started.SequenceEqual(new[] { "First", "Second" }) && fixture.Schedule.Entries.All(row => row.IsCompleted)
            && fixture.Schedule.Progress == 100 && fixture.Schedule.PositionText == "2 / 2",
            "Only the captured plans run, exactly in order, and final queue progress is complete");
        Check(fixture.Errors.Count == 0, "Successful playback reports no errors");
    }

    private static async Task CheckStopDuringMarkAsync(string testRoot)
    {
        await using var fixture = new Fixture(testRoot, "StopRace");
        var marking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowMark = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeMark = async _ => { marking.TrySetResult(); await allowMark.Task; };
        await fixture.Schedule.StartAsync(new[] { Plan(fixture.Day, "First"), Plan(fixture.Day, "Must not start") });
        fixture.Clock.Advance(10);
        await fixture.Countdown.TickAsync();
        await marking.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Schedule.StopAsync().WaitAsync(TimeSpan.FromSeconds(1));
        Check(!fixture.Schedule.IsActive && fixture.Schedule.Entries[0].IsCurrent,
            "Stop returns immediately during status persistence and keeps visual position");
        allowMark.SetResult();
        await WaitAsync(() => fixture.Marked.Count == 1);
        await fixture.Schedule.DisposeAsync();
        Check(fixture.Started.Count == 1 && fixture.Schedule.Entries[0].IsCompleted,
            "A delayed completion save cannot revive or advance a stopped schedule");
    }

    private static async Task CheckRefusalAndFailuresAsync(string testRoot)
    {
        await using (var fixture = new Fixture(testRoot, "Existing"))
        {
            await fixture.Countdown.StartAsync(fixture.Workspace, "Independent leisure timer", TimeSpan.FromSeconds(30), null);
            var independentId = fixture.Countdown.Current!.Id;
            await fixture.Schedule.StartAsync(new[] { Plan(fixture.Day, "Cannot replace") });
            Check(fixture.Started.Count == 0 && fixture.Countdown.Current?.Id == independentId
                && fixture.Countdown.IsRunning && !fixture.Schedule.IsActive && fixture.Errors.Count == 1,
                "An active independent timer is never replaced by starting a schedule");
        }
        await using (var fixture = new Fixture(testRoot, "StartFailure"))
        {
            fixture.StartSucceeds = false;
            await fixture.Schedule.StartAsync(new[] { Plan(fixture.Day, "Fails"), Plan(fixture.Day, "Never reached") });
            Check(!fixture.Schedule.IsActive && fixture.Started.Count == 0 && fixture.Errors.Count == 1
                && fixture.Schedule.Entries.All(row => !row.IsCurrent),
                "A failed start stops once, reports an error and never assigns a false current pointer");
        }
        await using (var fixture = new Fixture(testRoot, "MarkFailure"))
        {
            fixture.BeforeMark = _ => throw new IOException("Simulated plan-status save failure");
            await fixture.Schedule.StartAsync(new[] { Plan(fixture.Day, "First"), Plan(fixture.Day, "Never reached") });
            fixture.Clock.Advance(10);
            await fixture.Countdown.TickAsync();
            await WaitAsync(() => !fixture.Schedule.IsActive && fixture.Errors.Count == 1);
            Check(fixture.Started.Count == 1 && fixture.Marked.Count == 0,
                "Failed completion persistence stops advancement instead of silently skipping or looping");
        }
    }

    private static async Task CheckManualTerminationAsync(string testRoot)
    {
        foreach (var ending in new[] { SessionStatus.Cancelled, SessionStatus.EndedEarly, SessionStatus.Interrupted })
        {
            await using var fixture = new Fixture(testRoot, "Manual" + ending);
            await fixture.Schedule.StartAsync(new[] { Plan(fixture.Day, "Current"), Plan(fixture.Day, "Do not advance") });
            if (ending == SessionStatus.Cancelled) await fixture.Countdown.CancelAsync();
            else if (ending == SessionStatus.EndedEarly) await fixture.Countdown.EndEarlyAsync();
            else await fixture.Countdown.InterruptAsync();
            Check(!fixture.Schedule.IsActive && fixture.Started.Count == 1 && fixture.Marked.Count == 0
                && fixture.Schedule.Entries[0].IsCurrent && !fixture.Schedule.Entries[0].IsCompleted,
                "Manual terminal status stops advancement while retaining the pointer: " + ending);
        }
        await using (var fixture = new Fixture(testRoot, "StopDoesNotCancel"))
        {
            await fixture.Schedule.StartAsync(new[] { Plan(fixture.Day, "Continue independently") });
            await fixture.Schedule.StopAsync();
            Check(fixture.Countdown.IsRunning && !fixture.Schedule.IsActive,
                "Stopping automatic playback does not cancel its current countdown");
            fixture.Clock.Advance(10);
            await fixture.Countdown.TickAsync();
            Check(fixture.Marked.Count == 0, "An independently completing clock after Stop no longer updates scheduled plans");
        }
        await using (var fixture = new Fixture(testRoot, "DeleteCurrent"))
        {
            await fixture.Schedule.StartAsync(new[] { Plan(fixture.Day, "Delete this task"), Plan(fixture.Day, "Do not start") });
            await fixture.Countdown.DeleteCurrentRecordAsync();
            Check(fixture.Countdown.Current is null && !fixture.Schedule.IsActive && fixture.Started.Count == 1,
                "Deleting the current clock stops playback and cannot advance to an upcoming plan");
        }
    }

    private static PlanEntry Plan(DateOnly day, string title, PlanStatus status = PlanStatus.Incomplete) =>
        new(Guid.NewGuid().ToString("N"), day, title, status, DurationMinutes: 10d / 60, HasPomodoro: true);

    private static async Task WaitAsync(Func<bool> complete)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!complete()) await Task.Delay(10, timeout.Token);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Schedule check failed: " + message);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly RuntimeStore _store = new();
        public FakeClock Clock { get; } = new();
        public CountdownController Countdown { get; }
        public SchedulePlaybackController Schedule { get; }
        public string Workspace { get; }
        public DateOnly Day => DateOnly.FromDateTime(Clock.LocalNow.LocalDateTime);
        public List<string> Started { get; } = [];
        public List<string> Marked { get; } = [];
        public List<Exception> Errors { get; } = [];
        public bool StartSucceeds { get; set; } = true;
        public Func<PlanEntry, Task>? BeforeMark { get; set; }

        public Fixture(string testRoot, string name)
        {
            Workspace = Path.Combine(testRoot, "Schedule", name, "Workspace");
            var storage = new LocalAppStorage(Path.Combine(testRoot, "Schedule", name, "Data"));
            Countdown = new CountdownController(_store, storage, new ImmediateDispatcher(), Clock);
            Schedule = new SchedulePlaybackController(Countdown, async plan =>
            {
                if (!StartSucceeds) return false;
                await Countdown.StartAsync(Workspace, plan.Title, TimeSpan.FromMinutes(plan.DurationMinutes), null);
                Started.Add(plan.Title);
                return true;
            }, async plan =>
            {
                if (BeforeMark is not null) await BeforeMark(plan);
                Marked.Add(plan.Title);
            }, error => Errors.Add(error));
        }

        public async ValueTask DisposeAsync()
        {
            await Schedule.DisposeAsync();
            await Countdown.DisposeAsync();
            _store.Dispose();
        }
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
}
