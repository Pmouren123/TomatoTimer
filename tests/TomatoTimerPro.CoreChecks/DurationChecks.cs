using System.IO;
using TomatoTimerPro.Contracts.Interaction;
using TomatoTimerPro.Contracts.Navigation;
using TomatoTimerPro.Contracts.Threading;
using TomatoTimerPro.Contracts.Timing;
using TomatoTimerPro.Controllers.Components.Countdown;
using TomatoTimerPro.Controllers.Pages.Timer;
using TomatoTimerPro.Infrastructure.Desktop;
using TomatoTimerPro.Infrastructure.Persistence;
using TomatoTimerPro.RuntimeStore.Implementation;

public static class DurationChecks
{
    public static async Task RunAsync(string testRoot)
    {
        var input = new DurationInputController { Hours = "1", Minutes = "2", Seconds = "3" };
        Check(input.TryGetDuration(out var value) && value.TotalSeconds == 3723, "Hour, minute and second fields combine exactly");
        input.Hours = "24"; input.Minutes = "0"; input.Seconds = "0";
        Check(input.TryGetDuration(out value) && value.TotalHours == 24, "24-hour upper bound is accepted");
        input.Seconds = "1";
        Check(!input.TryGetDuration(out _), "More than 24 hours is rejected");
        input.Hours = "0"; input.Minutes = "60"; input.Seconds = "0";
        Check(!input.TryGetDuration(out _), "Minute components cannot reach 60");
        input.Minutes = "0"; input.Seconds = "60";
        Check(!input.TryGetDuration(out _), "Second components cannot reach 60");
        input.Seconds = "0";
        Check(!input.TryGetDuration(out _), "Zero duration is rejected");
        input.Seconds = "NaN";
        Check(!input.TryGetDuration(out _), "Non-finite input is rejected");
        input.SetMinutes("0.01");
        Check(input.TryGetDuration(out value) && value.TotalSeconds == .6 && input.Seconds == "0.6", "Legacy fractional-minute settings retain subsecond precision");
        input.SetMinutes("90.5");
        Check(input.Hours == "1" && input.Minutes == "30" && input.Seconds == "30", "Legacy long durations convert to separate units");

        var storage = new LocalAppStorage(Path.Combine(testRoot, "DurationData"));
        using var store = new RuntimeStore();
        var clock = new Clock();
        var interaction = new Interaction();
        await using var countdown = new CountdownController(store, storage, new Dispatcher(), clock);
        await using var timer = new TimerPageController(countdown, storage, interaction, new Navigator());
        timer.RestoreWorkspacePath(Path.Combine(testRoot, "DurationWorkspace"));
        timer.Duration.Hours = "1"; timer.Duration.Minutes = "2"; timer.Duration.Seconds = "3";
        Check(timer.RemainingText == "01:02:03", "Idle clock previews the full selected duration");
        timer.StartCommand.Execute(null);
        await WaitForAsync(() => countdown.IsActive);
        Check(countdown.Current?.PlannedSeconds == 3723 && timer.RemainingText == "01:02:03", "The actual session uses hours and seconds");
        timer.Duration.Seconds = "10";
        Check(countdown.Current?.PlannedSeconds == 3723, "Editing the next duration cannot change an active countdown");
        await countdown.DeleteCurrentRecordAsync();
        timer.Duration.Hours = "0"; timer.Duration.Minutes = "0"; timer.Duration.Seconds = "1";
        var completions = 0;
        countdown.Completed += _ => completions++;
        timer.StartCommand.Execute(null);
        await WaitForAsync(() => countdown.IsActive);
        Check(countdown.Current?.PlannedSeconds == 1, "One-second sessions are supported");
        clock.Advance(1);
        await countdown.TickAsync();
        Check(completions == 1 && timer.RemainingText == "00:00", "A second-based session finishes exactly once");

        var balloon = NativeTrayIcon.CreateBalloonPayload(new IntPtr(123), new IntPtr(456), "专注完成", "本次专注已完成");
        Check(balloon.Icon == new IntPtr(456) && balloon.BalloonIcon == new IntPtr(456) && balloon.InfoFlags == 0x24,
            "Completion notification uses the application icon via NIIF_USER and hBalloonIcon");
        Check(balloon.InfoTitle == "专注完成" && balloon.Flags == 0x10 && balloon.Timeout == 5000,
            "Completion notification retains its title, message and lifetime");
        var longBalloon = NativeTrayIcon.CreateBalloonPayload(IntPtr.Zero, new IntPtr(456), new string('字', 100), new string('字', 300));
        Check(longBalloon.InfoTitle.Length == 63 && longBalloon.Info.Length == 255, "Native notification payload respects Windows buffer limits");
        Check(balloon.Size == (IntPtr.Size == 8 ? 976u : 956u), "Native notification structure matches the current process architecture");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Clock : IClock
    {
        public long Milliseconds { get; private set; }
        public DateTimeOffset LocalNow { get; private set; } = new(2026, 10, 29, 12, 0, 0, TimeSpan.FromHours(8));
        public void Advance(int seconds) { Milliseconds += seconds * 1000L; LocalNow = LocalNow.AddSeconds(seconds); }
    }
    private sealed class Dispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
    }
    private sealed class Navigator : IPageNavigator
    { public Task<bool> NavigateAsync(string pageId) => Task.FromResult(true); }
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
