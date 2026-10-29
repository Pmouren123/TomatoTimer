using System.IO;
using TomatoTimerPro.Contracts.Storage;
using TomatoTimerPro.Contracts.Threading;
using TomatoTimerPro.Contracts.Timing;
using TomatoTimerPro.Controllers.Components.Countdown;
using TomatoTimerPro.Infrastructure.Persistence;
using TomatoTimerPro.RuntimeStore.Implementation;
using TomatoTimerPro.RuntimeStore.States.Timer;

var results = new List<string>();
var testRoot = Path.Combine(Path.GetTempPath(), "TomatoTimerPro.CoreChecks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
try
{
    if (args.Contains("--desktop-interactions"))
    {
        await DesktopInteractionChecks.RunAsync(testRoot);
        Console.WriteLine("PASS modal prompt buttons and focus window lifecycle");
        return 0;
    }
    await SingleInstanceChecks.RunAsync();
    results.Add("Single instance: same-build activation, mismatched-build notice, legacy fallback and timeout");
    using (var store = new RuntimeStore())
    {
        store.Set(new Counter(0));
        Parallel.For(0, 10_000, _ => store.Update<Counter>(counter => counter with { Value = counter.Value + 1 }));
        Check(store.Get<Counter>().Value == 10_000, "Concurrent store updates are atomic");
        var notifications = 0;
        using (store.Subscribe<Counter>(counter =>
        {
            // A separate thread can acquire the store during notification, so notification is outside its lock.
            Check(Task.Run(() => store.Get<Counter>()).Wait(TimeSpan.FromSeconds(2)), "Subscriber runs outside store lock");
            notifications++;
        })) store.Update<Counter>(counter => counter with { Value = counter.Value + 1 });
        store.Set(new Counter(1));
        Check(notifications == 1, "Disposed store subscriptions no longer receive updates");
    }
    results.Add("Runtime store: atomic updates, outside-lock notification, unsubscribe");

    var storage = new LocalAppStorage(Path.Combine(testRoot, "AppData"));
    var workspace = Path.Combine(testRoot, "Workspace");
    var day = new DateOnly(2026, 10, 29);
    var directory = await storage.PrepareDayAsync(workspace, day);
    var documentPath = Path.Combine(directory, "Daily.md");
    var missing = await storage.ReadDocumentAsync(documentPath, "initial");
    await storage.SaveDocumentAsync(missing, "第一份记录");
    var original = await storage.ReadDocumentAsync(documentPath);
    await File.WriteAllTextAsync(documentPath, "external edit");
    var conflict = false;
    try { await storage.SaveDocumentAsync(original, "must not overwrite"); }
    catch (DocumentConflictException) { conflict = true; }
    Check(conflict, "External document changes are detected");
    Check(await File.ReadAllTextAsync(documentPath) == "external edit", "Conflicting save preserves external contents");
    await storage.SaveRecoveryAsync(documentPath, "恢复文本");
    var recovery = await storage.LoadRecoveryDraftsAsync();
    Check(recovery.Count == 1 && recovery[0].Content == "恢复文本", "Recovery content is independently persisted");
    await storage.DeleteRecoveryAsync(recovery[0].Id);
    Check((await storage.LoadRecoveryDraftsAsync()).Count == 0, "Recovery drafts can be removed after resolution");
    var nearMidnight = new DateTimeOffset(2026, 10, 29, 23, 59, 0, DateTimeOffset.Now.Offset);
    var session = new TimerSession { WorkspacePath = workspace, StartedAt = nearMidnight, EndedAt = nearMidnight.AddMinutes(25), PlannedSeconds = 1500, Status = SessionStatus.Completed };
    Check(storage.GetReflectionPath(session).Contains("2026-10-29"), "Cross-midnight sessions stay in starting-day directory");
    await storage.SaveSessionAsync(session);
    Check((await storage.LoadSessionsAsync(workspace, day)).Single().Id == session.Id, "Session metadata survives roundtrip");
    results.Add("Storage: conflict protection, recovery, cross-midnight paths, session roundtrip");

    using (var store = new RuntimeStore())
    {
        var clock = new FakeClock(nearMidnight);
        await using var countdown = new CountdownController(store, storage, new ImmediateDispatcher(), clock);
        var completions = 0;
        countdown.Completed += _ => completions++;
        await countdown.StartAsync(workspace, "Deterministic timer", TimeSpan.FromSeconds(10), null);
        clock.Advance(4);
        await countdown.TickAsync();
        Check(Math.Abs(countdown.Remaining.TotalSeconds - 6) < .01, "Elapsed monotonic clock drives remaining time");
        await countdown.PauseResumeAsync();
        clock.Advance(120);
        await countdown.TickAsync();
        Check(Math.Abs(countdown.Remaining.TotalSeconds - 6) < .01, "Paused time is excluded");
        await countdown.PauseResumeAsync();
        clock.Advance(6);
        await countdown.TickAsync();
        await countdown.TickAsync();
        Check(countdown.Current?.Status == SessionStatus.Completed && completions == 1, "Completion is emitted exactly once");
        Check(await storage.LoadActiveSessionAsync() is null, "Completed active-session checkpoint is cleared");
        await countdown.StartAsync(workspace, "Cancellation", TimeSpan.FromSeconds(10), null);
        clock.Advance(3);
        await countdown.CancelAsync();
        Check(countdown.Current is { Status: SessionStatus.Cancelled, FocusedSeconds: 3 }, "Cancellation retains actual focused time");
    }
    results.Add("Countdown: fake-clock timing, pause exclusion, exactly-once completion, cancellation");

    await storage.SaveActiveSessionAsync(session with { Status = SessionStatus.Running, FocusedSeconds = 40 });
    using (var store = new RuntimeStore())
    {
        await using var restored = new CountdownController(store, storage, new ImmediateDispatcher(), new FakeClock(nearMidnight.AddDays(3)));
        await restored.RestoreAsync();
        Check(restored.Current is { Status: SessionStatus.Interrupted, FocusedSeconds: 40 }, "Crash checkpoint restores as interrupted without guessing completion");
    }
    results.Add("Recovery: interrupted checkpoint preserves measured focus");
    await TaskExitChecks.RunAsync(testRoot);
    results.Add("Task exit: early finish, abandon/cancel prompts, optional reflection, immediate next task and explicit validation dialogs");
    await AudioChecks.RunAsync(testRoot);
    results.Add("Audio: lazy startup, safe import/removal, volume bounds and persisted index");
    await AudioInteractionChecks.RunAsync(testRoot);
    results.Add("Audio interactions: floating alarm stop, selected/default/silent preview, no timer start, observer disposal");
    DailyReflectionChecks.Run();
    results.Add("Daily reflections: optional append, stable updates and isolated removal preserve daily prose");
    await RecordsChecks.RunAsync(testRoot);
    results.Add("Records: cancelled/failed navigation preservation, stable reflection selection and draft recovery");
    await TaskFlowChecks.RunAsync(testRoot);
    results.Add("Task flow: end early, confirmed current-record deletion, dirty-record independence, explicit discard and stale-dialog protection");
    await PlanningChecks.RunAsync(testRoot);
    await ScheduleChecks.RunAsync(testRoot);
    await ScheduleAvailabilityChecks.RunAsync(testRoot);
    await DayTimeAllocationChecks.RunAsync(testRoot);
    await JournalHistoryChecks.RunAsync(testRoot);
    results.Add("Journal history: date discovery, independent journals, safe navigation, refresh and workspace isolation");
    results.Add("Daily allocation: proportional plans, measured focus, pause exclusion, deletion, workspace isolation and rollover");
    results.Add("Schedule: automatic ordered playback, current pointer, pause, terminal stops and delayed completion safety");
    results.Add("Planning: optional embedded pomodoros, four states, pending dots, hold/leisure/restore, next-day trash cleanup, persistence and rollover");
    await DurationChecks.RunAsync(testRoot);
    results.Add("Duration and notifications: hour/minute/second sessions, fractional-minute compatibility, active-session isolation and native application-icon payload");
    await WpfChecks.RunAsync(testRoot);
    results.Add("WPF: resources, composed pages, editable workspace, explicit OneWay progress binding, appearance, and optional native previews using an invisible offscreen presentation source");
    foreach (var result in results) Console.WriteLine("PASS " + result);
    Console.WriteLine("All core checks passed.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("FAIL " + error);
    Console.Error.WriteLine("Diagnostic test files retained at: " + testRoot);
    return 1;
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed record Counter(int Value);
sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Invoke(Action action) => action();
    public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
}
sealed class FakeClock(DateTimeOffset now) : IClock
{
    public long Milliseconds { get; private set; }
    public DateTimeOffset LocalNow { get; private set; } = now;
    public void Advance(double seconds)
    {
        Milliseconds += (long)(seconds * 1000);
        LocalNow = LocalNow.AddSeconds(seconds);
    }
}
