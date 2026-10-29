using System.IO;
using System.Windows.Input;
using TomatoTimerPro.Contracts.Audio;
using TomatoTimerPro.Contracts.Interaction;
using TomatoTimerPro.Contracts.Navigation;
using TomatoTimerPro.Contracts.Threading;
using TomatoTimerPro.Controllers.Components.Countdown;
using TomatoTimerPro.Controllers.Components.FloatingClock;
using TomatoTimerPro.Controllers.Pages.Timer;
using TomatoTimerPro.Infrastructure.Persistence;
using TomatoTimerPro.RuntimeStore.Implementation;

public static class AudioInteractionChecks
{
    public static async Task RunAsync(string testRoot)
    {
        var storage = new LocalAppStorage(Path.Combine(testRoot, "AudioInteractionData"));
        using var store = new RuntimeStore();
        await using var countdown = new CountdownController(store, storage, new ImmediateDispatcher());
        await using var timer = new TimerPageController(countdown, storage, new SilentInteraction(), new SilentNavigator());
        using var audio = new FakeMusicService();
        using var floating = new FloatingClockController(timer, () => { }, () => { }, audio);
        Check(!floating.IsAlarmPlaying && !floating.StopAlarmCommand.CanExecute(null),
            "Floating clock cannot stop an alarm that is not playing");
        floating.StopAlarmCommand.Execute(null);
        Check(audio.StopCalls == 0, "Disabled stop command does not call audio adapter");
        audio.SetAlarmPlaying(true);
        Check(floating.IsAlarmPlaying && floating.StopAlarmCommand.CanExecute(null),
            "An alarm started elsewhere becomes stoppable from floating clock");
        floating.StopAlarmCommand.Execute(null);
        Check(audio.StopCalls == 1 && !audio.IsAlarmPlaying && !floating.IsAlarmPlaying
            && !floating.StopAlarmCommand.CanExecute(null), "Floating clock stops the shared alarm and reflects its final state");
        Check(audio.IsPlaying, "Stopping a reminder does not directly stop background music");

        timer.PreviewAlarmRequested += path => audio.PlayAlarmAsync(path, path == "__silent__");
        var chosen = Path.Combine(testRoot, "UserChosenAlarm.mp3");
        timer.SelectedAlarm = new AlarmChoice("Selected track", chosen);
        await RunCommandAsync(timer.PreviewAlarmCommand);
        Check(audio.LastAlarmPath == chosen && !audio.LastSilent && audio.PlayAlarmCalls == 1,
            "Timer preview sends the current selected file through the audio boundary");
        Check(!countdown.IsActive && countdown.Current is null,
            "Previewing the selected alarm does not create or start a focus session");
        Check(floating.IsAlarmPlaying, "A timer-page preview can also be stopped from floating clock");
        floating.StopAlarmCommand.Execute(null);
        Check(audio.StopCalls == 2 && !audio.IsAlarmPlaying, "Floating stop works for previews as well as completion alarms");

        timer.SelectedAlarm = new AlarmChoice("Silent", "__silent__");
        await RunCommandAsync(timer.PreviewAlarmCommand);
        Check(audio.LastAlarmPath == "__silent__" && audio.LastSilent && !audio.IsAlarmPlaying,
            "Preview respects the explicitly selected silent alarm option");
        timer.SelectedAlarm = new AlarmChoice("Default", null);
        await RunCommandAsync(timer.PreviewAlarmCommand);
        Check(audio.LastAlarmPath is null && !audio.LastSilent,
            "Default-alarm preview forwards the default selection instead of an old track");
        audio.SetAlarmPlaying(false);
        var updatesAfterDisposal = 0;
        floating.PropertyChanged += (_, _) => updatesAfterDisposal++;
        floating.Dispose();
        audio.SetAlarmPlaying(true);
        Check(updatesAfterDisposal == 0, "Disposed floating controller stops observing shared audio state");
    }

    private static async Task RunCommandAsync(ICommand command)
    {
        command.Execute(null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!command.CanExecute(null)) await Task.Delay(10, timeout.Token);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Audio interaction check failed: " + message);
    }
    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
    }
    private sealed class SilentNavigator : IPageNavigator
    {
        public Task<bool> NavigateAsync(string pageId) => Task.FromResult(true);
    }
    private sealed class SilentInteraction : IUserInteraction
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
        public Task<string[]> PickAudioFilesAsync() => Task.FromResult(Array.Empty<string>());
        public Task<string?> PickSaveFileAsync(string suggestedName) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
        public Task<SaveDecision> ResolveUnsavedAsync(string title, string message) => Task.FromResult(SaveDecision.Cancel);
        public void ShowMessage(string title, string message) { }
        public void OpenFolder(string path) { }
    }
    private sealed class FakeMusicService : IMusicService
    {
        public IReadOnlyList<MusicTrack> Tracks => Array.Empty<MusicTrack>();
        public MusicTrack? CurrentTrack => null;
        public bool IsPlaying => true;
        public bool IsAlarmPlaying { get; private set; }
        public double PositionSeconds => 0;
        public double DurationSeconds => 0;
        public double MusicVolume { get; set; } = .5;
        public double AlarmVolume { get; set; } = .8;
        public bool ResumeMusic { get; set; } = true;
        public string StatusMessage => "";
        public string? LastAlarmPath { get; private set; }
        public bool LastSilent { get; private set; }
        public int StopCalls { get; private set; }
        public int PlayAlarmCalls { get; private set; }
        public event EventHandler? Changed;
        public event EventHandler? LibraryChanged { add { } remove { } }
        public Task InitializeAsync() => Task.CompletedTask;
        public Task ImportAsync(IEnumerable<string> paths) => Task.CompletedTask;
        public Task RemoveAsync(Guid id) => Task.CompletedTask;
        public Task RelinkAsync(Guid id, string path) => Task.CompletedTask;
        public void Play(Guid? trackId = null) { }
        public void Pause() { }
        public void Stop() { }
        public void Previous() { }
        public void Next() { }
        public void Seek(double seconds) { }
        public Task PlayAlarmAsync(string? path, bool silent = false)
        {
            LastAlarmPath = path;
            LastSilent = silent;
            PlayAlarmCalls++;
            SetAlarmPlaying(!silent);
            return Task.CompletedTask;
        }
        public void SetAlarmPlaying(bool playing)
        {
            IsAlarmPlaying = playing;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        public void StopAlarm() { StopCalls++; SetAlarmPlaying(false); }
        public void Dispose() => Changed = null;
    }
}
