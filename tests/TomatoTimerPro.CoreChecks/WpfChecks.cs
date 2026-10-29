using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Shell;
using System.Windows.Threading;
using TomatoTimerPro.Bootstrap;
using TomatoTimerPro.Contracts.Interaction;
using TomatoTimerPro.Infrastructure.Threading;
using TomatoTimerPro.Views.Pages.Music;
using TomatoTimerPro.Views.Pages.Records;
using TomatoTimerPro.Views.Pages.Settings;
using TomatoTimerPro.Views.Pages.Timer;
using TomatoTimerPro.Views.Pages.Calendar;
using TomatoTimerPro.Views.Shell;
using TomatoTimerPro.Controllers.Pages.Settings;
using TomatoTimerPro.Controllers.Components.FloatingClock;
using TomatoTimerPro.Infrastructure.Desktop;
using TomatoTimerPro.RuntimeStore.States.Settings;
using TomatoTimerPro.Views.Components.FloatingClock;
using System.Windows.Media;
using System.Windows.Media.Imaging;

public static class WpfChecks
{
    public static async Task RunAsync(string testRoot)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await CheckOnDispatcherAsync(testRoot, dispatcher);
                }
                catch (Exception error) { failure = error; Console.Error.WriteLine("WPF checks failed: " + error); }
                finally
                {
                    // The test host did not call Application.Run. Shut down its
                    // resources explicitly, then stop our external dispatcher
                    // frame as well, after the queued application cleanup.
                    try
                    {
                        if (System.Windows.Application.Current is { } application)
                        {
                            application.Shutdown(failure is null ? 0 : 1);
                        }
                    }
                    catch (Exception error)
                    {
                        failure ??= error;
                    }
                    finally
                    {
                        if (!dispatcher.HasShutdownStarted)
                            dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    }
                }
            }));
            try { Dispatcher.Run(); }
            catch (Exception error) { failure ??= error; }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(null);
                // Completion follows Dispatcher.Run, not the last assertion:
                // WPF can still set the process exit code while it shuts down.
                if (failure is null) completion.TrySetResult();
                else completion.TrySetException(failure);
            }
        }) { IsBackground = true, Name = "TomatoTimerPro WPF smoke checks" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(45)); }
        finally
        {
            if (thread.IsAlive && !thread.Join(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("WPF smoke-check dispatcher thread did not terminate cleanly.");
        }
    }

    private static async Task CheckOnDispatcherAsync(string testRoot, Dispatcher dispatcher)
    {
        var source = PresentationTraceSources.DataBindingSource;
        var previousLevel = source.Switch.Level;
        using var listener = new BindingErrorListener();
        source.Listeners.Add(listener);
        source.Switch.Level = SourceLevels.Error;
        MainWindow? window = null;
        try
        {
            // Use the platform Application solely as the resource host. Loading
            // the compiled production App.xaml onto a derived type violates its
            // BAML type identity; constructing the real App would schedule its
            // production startup and create managers/tray/single-instance state.
            var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var resource in new[]
            {
                "Resources/Icons/AppIcon.xaml",
                "Resources/Icons/DoodleIcons.xaml",
                "Resources/Styles/ApplicationStyles.xaml"
            })
            {
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/TomatoTimer;component/" + resource, UriKind.Absolute)
                });
            }
            Check(application.StartupUri is null, "Isolated resource host does not start an application window");
            Check(application.Resources["AccentBrush"] is not null, "Application theme is merged");
            await ScrollBarChecks.RunOnDispatcherAsync(dispatcher);
            await MotionChecks.RunOnDispatcherAsync(dispatcher);
            await DialogChecks.RunOnDispatcherAsync(dispatcher);

            await using var composition = new ApplicationComposition(
                new WpfUiDispatcher(dispatcher), new SilentInteraction(), Path.Combine(testRoot, "WpfAppData"));
            window = new MainWindow
            {
                DataContext = composition.Shell, ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000, Opacity = 0
            };
            var chrome = WindowChrome.GetWindowChrome(window);
            Check(window.WindowStyle == WindowStyle.None && window.ResizeMode == ResizeMode.CanResize && !window.AllowsTransparency
                && chrome is not null && Math.Abs(chrome.CaptionHeight - 46) < .01 && !chrome.UseAeroCaptionButtons,
                "Shell replaces native title chrome while preserving resizing and a 46-pixel drag caption");
            // A real presentation source is necessary: measuring an unattached root misses
            // default TwoWay bindings that are attached only once the visual tree is loaded.
            window.Show();
            await composition.InitializeAsync();
            await LayoutAsync(window, dispatcher);
            Check(composition.Shell.CurrentPageId == "timer" && composition.Shell.CurrentPage is TimerPage,
                "Startup selects the timer page");
            WindowShapeChecks.AssertSquareCorners(window);
            window.WindowState = WindowState.Maximized;
            await LayoutAsync(window, dispatcher);
            window.WindowState = WindowState.Normal;
            await LayoutAsync(window, dispatcher);
            WindowShapeChecks.AssertSquareCorners(window);
            BindingChecks.AssertProgressBindingsAreOneWay(window, requireProgress: true);
            BindingChecks.AssertTimerTextIsVisible(window, composition.Timer.RemainingText);

            var timerView = (TimerPage)composition.Shell.CurrentPage!;
            var plannerCard = (FrameworkElement)timerView.FindName("PlannerCard");
            var clockCard = (FrameworkElement)timerView.FindName("ClockCard");
            var setup = (FrameworkElement)timerView.FindName("SetupCard");
            var emptyScheduleCard = (FrameworkElement)timerView.FindName("EmptyScheduleCard");
            Check(!plannerCard.IsVisible && clockCard.IsVisible && setup.IsVisible,
                "Leisure mode hides schedule plans and retains the independent timer");

            composition.Timer.SelectScheduleModeCommand.Execute(null);
            Check(!composition.Timer.HasRunnablePlans && !composition.Timer.StartCommand.CanExecute(null)
                && composition.Timer.ShowScheduleEmptyState,
                "Schedule mode cannot start without an unfinished plan containing a pomodoro");
            composition.Timer.StartCommand.Execute(null);
            Check(!composition.Countdown.IsActive && composition.Countdown.Current is null,
                "Disabled schedule start and its shortcut cannot create an unplanned timer");
            await LayoutAsync(window, dispatcher);
            Check(plannerCard.IsVisible && !clockCard.IsVisible && !setup.IsVisible && emptyScheduleCard.IsVisible,
                "Empty schedule replaces the timer and setup with an add-plan entry");
            await CapturePreviewAsync(window, dispatcher, "schedule-empty");
            composition.Timer.SelectLeisureModeCommand.Execute(null);
            Check(composition.Timer.StartCommand.CanExecute(null), "Leisure mode remains independent of schedule plans");

            Check(await composition.Pages.NavigateAsync("calendar"), "Calendar navigation succeeds");
            var planDay = DateOnly.FromDateTime(DateTime.Now);
            Check(await composition.Calendar.SaveDayAsync(planDay, new[]
            {
                new TomatoTimerPro.RuntimeStore.States.Planning.PlanEntry(Guid.NewGuid().ToString("N"), planDay, "整理今天的思路"),
                new TomatoTimerPro.RuntimeStore.States.Planning.PlanEntry(Guid.NewGuid().ToString("N"), planDay, "完成一段专注"),
                new TomatoTimerPro.RuntimeStore.States.Planning.PlanEntry(Guid.NewGuid().ToString("N"), planDay, "读几页喜欢的书", TomatoTimerPro.RuntimeStore.States.Planning.PlanStatus.OnHold)
            }) is null, "Calendar saves several plans for today");
            var firstPlan = composition.Store.Get<TomatoTimerPro.RuntimeStore.States.Planning.PlanState>().Entries[0];
            await composition.Calendar.ChangeStatusAsync(firstPlan.Id, TomatoTimerPro.RuntimeStore.States.Planning.PlanStatus.Completed);
            await LayoutAsync(window, dispatcher);
            Check(composition.Shell.CurrentPage is CalendarPage && composition.Timer.Planner == composition.Calendar,
                "Calendar and timer share one planner");
            Check(composition.Calendar.Days.Where(d => d.Day is not null).All(d => d.SelectCommand.CanExecute(null)),
                "Date buttons become available again after loading and saving");
            await PlanningWpfChecks.RunAsync(composition.Calendar,
                composition.Store.Get<TomatoTimerPro.RuntimeStore.States.Planning.PlanState>().Entries, dispatcher);
            await CapturePreviewAsync(window, dispatcher, "calendar");

            Check(await composition.Pages.NavigateAsync("records"), "Empty-workspace records navigation succeeds");
            await LayoutAsync(window, dispatcher);
            Check(composition.Shell.CurrentPage is RecordsPage && composition.Records is { CanEdit: false },
                "Records editor is disabled until a workspace document is loaded");

            Check(await composition.Pages.NavigateAsync("music"), "Music page initializes");
            await LayoutAsync(window, dispatcher);
            Check(composition.Shell.CurrentPage is MusicPage, "Music view is connected to the shell");
            await CapturePreviewAsync(window, dispatcher, "music");
            Check(await composition.Pages.NavigateAsync("settings"), "Settings page initializes");
            await LayoutAsync(window, dispatcher);
            Check(composition.Shell.CurrentPage is SettingsPage, "Settings view is connected to the shell");
            var settings = (SettingsPageController)((SettingsPage)composition.Shell.CurrentPage!).DataContext;
            settings.DefaultDuration.Hours = "1";
            settings.DefaultDuration.Minutes = "2";
            settings.DefaultDuration.Seconds = "3";
            settings.ApplyDefaultDurationCommand.Execute(null);
            Check(composition.Timer.Duration.TryGetDuration(out var selectedDuration) && selectedDuration.TotalSeconds == 3723,
                "Default hour/minute/second preference synchronizes to the timer without starting a session");
            Check(!composition.Countdown.IsActive, "Applying duration preferences does not create a focus session");
            settings.DefaultDuration.Seconds = "60";
            settings.ApplyDefaultDurationCommand.Execute(null);
            Check(Math.Abs(composition.Store.Get<AppPreferences>().DefaultDurationMinutes - 62.05) < .000001,
                "Invalid duration components cannot overwrite the saved default");
            settings.DefaultDuration.Seconds = "3";
            await CapturePreviewAsync(window, dispatcher, "settings");
            settings.BackgroundColor = "#F0E0D0";
            settings.BackgroundOpacity = .4;
            settings.FloatingClockEnabled = true;
            settings.BackgroundColor = "invalid";
            Check(composition.Store.Get<AppPreferences>().BackgroundColor == "#F0E0D0", "Invalid colors cannot replace the saved background");
            Check(composition.Store.Get<AppPreferences>().FloatingClockEnabled, "Floating preference updates shared state");
            var background = new WindowBackgroundService(window.BackgroundSurface);
            var imagePath = Path.Combine(testRoot, "background.png");
            var pixels = new byte[] { 30, 80, 170, 255 };
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(imagePath)) encoder.Save(stream);
            background.Apply(settings.BackgroundColor, imagePath, settings.BackgroundOpacity);
            Check(window.BackgroundSurface.Background is DrawingBrush, "Custom background is applied only to the inset canvas layer");
            await LayoutAsync(window, dispatcher);
            WindowShapeChecks.AssertSquareCorners(window);
            File.Delete(imagePath);
            Check(!File.Exists(imagePath), "Background image is not file-locked after decoding");
            await composition.FlushAsync();
            var savedAppearance = await composition.Storage.LoadPreferencesAsync();
            Check(Math.Abs(savedAppearance.DefaultDurationMinutes - 62.05) < .000001,
                "Hour/minute/second preferences persist using the compatible minute representation");
            var restoredDuration = new TomatoTimerPro.Controllers.Components.Countdown.DurationInputController();
            restoredDuration.SetMinutes(savedAppearance.DefaultDurationMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Check(restoredDuration.Hours == "1" && restoredDuration.Minutes == "2" && restoredDuration.Seconds == "3",
                "Saved default restores all three unit fields");
            Check(savedAppearance.BackgroundColor == "#F0E0D0" && savedAppearance.BackgroundOpacity == .4 && savedAppearance.FloatingClockEnabled,
                "Appearance and floating preferences survive persistence");
            using (var floating = new FloatingClockController(composition.Timer, () => { }, () => { }))
            {
                var clockWindow = new FloatingClockWindow { DataContext = floating };
                ((FrameworkElement)clockWindow.Content).Measure(new Size(256, 184));
                ((FrameworkElement)clockWindow.Content).Arrange(new Rect(0, 0, 256, 184));
                Check(floating.RemainingText == composition.Timer.RemainingText, "Floating clock shares the application timer");
                clockWindow.Close();
            }
            settings.FloatingClockEnabled = false;
            settings.ResetBackgroundCommand.Execute(null);
            Check(settings.BackgroundImagePath is null && settings.BackgroundColor == "#FAF5E9", "Reset restores the default background");
            background.Apply("#FAF5E9", null, .18);

            var workspace = Path.Combine(testRoot, "WpfWorkspace");
            await composition.Timer.SetWorkspaceAsync(workspace);
            Check(composition.Timer.WorkspacePath == workspace && Directory.Exists(workspace),
                "Timer prepares the workspace through the composed services");
            Check(await composition.Pages.NavigateAsync("records"), "Records page can be revisited");
            // Existing kept-alive pages retain context until the user explicitly switches it.
            Check(composition.Records is not null, "Records controller exists");
            Check(await composition.Records!.ChangeContextAsync(workspace, DateTime.Today),
                "Records controller accepts the prepared workspace");
            await LayoutAsync(window, dispatcher);
            Check(composition.Records.CanEdit && composition.Records.EditorText.Contains("今日收获"),
                "Workspace records load an editable daily template");
            await CapturePreviewAsync(window, dispatcher, "records");
            Check(await composition.Pages.NavigateAsync("timer"), "Timer page can be revisited");
            await LayoutAsync(window, dispatcher);
            BindingChecks.AssertProgressBindingsAreOneWay(window, requireProgress: true);
            BindingChecks.AssertTimerTextIsVisible(window, composition.Timer.RemainingText);
            await CapturePreviewAsync(window, dispatcher, "timer");
            Check(!plannerCard.IsVisible && clockCard.IsVisible,
                "Leisure mode keeps schedule plans hidden even when plans exist");
            var allocation = (FrameworkElement)timerView.FindName("DayAllocation");
            Check(!allocation.IsVisible, "Leisure mode hides the daily schedule allocation bar");
            composition.Timer.SelectScheduleModeCommand.Execute(null);
            window.Width = 960;
            await LayoutAsync(window, dispatcher);
            Check(System.Windows.Controls.Grid.GetRow(setup) == 1 && setup.ActualWidth > 300,
                "Narrow schedule windows move the setup card beneath the timer");
            WindowShapeChecks.AssertSquareCorners(window);
            BindingChecks.AssertTimerTextIsVisible(window, composition.Timer.RemainingText);
            await CapturePreviewAsync(window, dispatcher, "timer-compact");
            window.Width = 1160;
            await LayoutAsync(window, dispatcher);

            var focusPlan = firstPlan with { DurationMinutes = 37.5, Status = TomatoTimerPro.RuntimeStore.States.Planning.PlanStatus.Incomplete };
            var focusPlans = composition.Store.Get<TomatoTimerPro.RuntimeStore.States.Planning.PlanState>().Entries
                .Select(plan => plan.Id == focusPlan.Id ? focusPlan : plan).ToArray();
            Check(await composition.Calendar.SaveDayAsync(planDay, focusPlans) is null, "Schedule duration saves before focus");
            composition.Timer.SelectScheduleModeCommand.Execute(null);
            Check(composition.Timer.HasRunnablePlans && composition.Timer.StartCommand.CanExecute(null)
                && !composition.Timer.ShowScheduleEmptyState,
                "Adding an eligible plan enables schedule start immediately");
            await LayoutAsync(window, dispatcher);
            Check(plannerCard.IsVisible && clockCard.IsVisible && setup.IsVisible && !emptyScheduleCard.IsVisible,
                "A runnable schedule restores the timer and its setup");
            Check(allocation.IsVisible && composition.TimeAllocation.HasPlans
                && composition.TimeAllocation.PlannedSeconds > 0,
                "Schedule mode displays the daily proportional time allocation");
            await CapturePreviewAsync(window, dispatcher, "day-time-allocation");
            Check(await composition.Pages.NavigateAsync("calendar"), "Schedule focus starts from calendar");
            await composition.Calendar.StartFocusAsync(focusPlan.Id);
            var focusedSession = composition.Countdown.Current!;
            Check(composition.Shell.CurrentPageId == "timer" && composition.Countdown.IsRunning
                && focusedSession.Title == focusPlan.Title && focusedSession.PlannedSeconds == 2250,
                "The composed schedule entry opens the timer and starts its saved duration");
            await composition.Countdown.EndEarlyAsync();
            var dailyPath = Path.Combine(composition.Storage.GetDayDirectory(workspace, focusedSession.Day), "Daily.md");
            Check(!File.Exists(dailyPath) && !File.Exists(composition.Storage.GetReflectionPath(focusedSession)),
                "Schedule completion stores statistics without creating any reflection log");
            Check(await composition.Pages.NavigateAsync("records"), "Schedule statistics can be viewed independently of reflections");
            composition.Records!.SelectedSession = composition.Records.Sessions.Single(row => row.Session.Id == focusedSession.Id);
            using (var selectionTimeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                while (composition.Records.IsBusy || composition.Records.SelectedSession?.Session.Id != focusedSession.Id)
                    await Task.Delay(10, selectionTimeout.Token);
            }
            await LayoutAsync(window, dispatcher);
            Check(composition.Records.IsDailyMode && composition.Records.HasLogEditor
                && composition.Records.EditorText.Contains("今日收获") && !File.Exists(dailyPath),
                "Selecting timer statistics preserves the date journal and creates no log");
            await CapturePreviewAsync(window, dispatcher, "records-without-reflection");
            await composition.Records.AddReflectionAsync();
            Check(composition.Records.HasReflectionEditor && !composition.Records.IsDirty && !File.Exists(dailyPath),
                "Add reflection opens an empty draft without writing a file");
            composition.Records.EditorText = "这次完成了重要的一步。";
            Check(await composition.Records.SaveAsync() && (await File.ReadAllTextAsync(dailyPath)).Contains("重要的一步")
                && !File.Exists(composition.Storage.GetReflectionPath(focusedSession)),
                "Explicitly saved reflections append to the daily note without creating per-session logs");
            await LayoutAsync(window, dispatcher);
            await CapturePreviewAsync(window, dispatcher, "records-added-reflection");
            Check(await composition.Pages.NavigateAsync("calendar"), "Saved journal can be revisited from the calendar");
            await composition.Calendar.SelectDateAsync(planDay);
            await LayoutAsync(window, dispatcher);
            Check(composition.Calendar.SelectedHasJournal
                && composition.Calendar.Days.Single(day => day.Day == planDay).HasJournal
                && composition.Calendar.OpenSelectedJournalCommand.CanExecute(null),
                "Saved date journals are marked in the calendar and expose an open action");
            await CapturePreviewAsync(window, dispatcher, "calendar-journal");
            composition.Calendar.OpenSelectedJournalCommand.Execute(null);
            using (var journalTimeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                while (composition.Shell.CurrentPageId != "records" || composition.Records.IsBusy
                    || !composition.Records.IsDailyMode || !composition.Records.HistoryVisible)
                    await Task.Delay(10, journalTimeout.Token);
            }
            await LayoutAsync(window, dispatcher);
            Check(composition.Records.SelectedDate == planDay.ToDateTime(TimeOnly.MinValue)
                && composition.Records.EditorText.Contains("重要的一步")
                && composition.Records.History.Any(entry => entry.Day == planDay),
                "Calendar opens the matching date journal with history available");
            await CapturePreviewAsync(window, dispatcher, "journal-history");

            var automaticFirst = new TomatoTimerPro.RuntimeStore.States.Planning.PlanEntry(
                Guid.NewGuid().ToString("N"), planDay, "整理资料", DurationMinutes: .01);
            var automaticSecond = automaticFirst with { Id = Guid.NewGuid().ToString("N"), Title = "完成正文", DurationMinutes = 30 };
            var leisureHeld = automaticFirst with { Id = Guid.NewGuid().ToString("N"), Title = "留待有空时阅读",
                Status = TomatoTimerPro.RuntimeStore.States.Planning.PlanStatus.OnHold, DurationMinutes = 15 };
            var abandonedPlan = automaticFirst with { Id = Guid.NewGuid().ToString("N"), Title = "今天不再处理",
                Status = TomatoTimerPro.RuntimeStore.States.Planning.PlanStatus.Abandoned };
            Check(await composition.Calendar.SaveDayAsync(planDay, new[] { automaticFirst, automaticSecond, leisureHeld, abandonedPlan }) is null,
                "Schedule and bin plans can coexist");
            Check(await composition.Pages.NavigateAsync("timer"), "Automatic schedule returns to timer");
            composition.Timer.SelectScheduleModeCommand.Execute(null);
            composition.Timer.StartCommand.Execute(null);
            using (var startTimeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                while (!composition.Countdown.IsActive) await Task.Delay(10, startTimeout.Token);
            }
            using (var advanceTimeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8)))
            {
                while (composition.Countdown.Current?.Title != automaticSecond.Title) await Task.Delay(20, advanceTimeout.Token);
            }
            await LayoutAsync(window, dispatcher);
            Check(composition.Schedule.IsActive && composition.Schedule.PositionText == "2 / 2"
                && composition.Schedule.Entries[0].IsCompleted && composition.Schedule.Entries[1].IsCurrent
                && composition.Store.Get<TomatoTimerPro.RuntimeStore.States.Planning.PlanState>().Entries
                    .Single(plan => plan.Id == automaticFirst.Id).Status == TomatoTimerPro.RuntimeStore.States.Planning.PlanStatus.Completed,
                "Natural completion advances the composed automatic schedule and its visible pointer");
            await CapturePreviewAsync(window, dispatcher, "schedule-pointer");
            await composition.Countdown.EndEarlyAsync();
            Check(!composition.Schedule.IsActive, "Manual termination stops automatic progression");
            Check(await composition.Pages.NavigateAsync("calendar"), "Bins remain available after stopping the schedule");
            composition.Calendar.ShowOnHoldCommand.Execute(null);
            await LayoutAsync(window, dispatcher);
            await CapturePreviewAsync(window, dispatcher, "hold-box");
            composition.Calendar.ShowTrashCommand.Execute(null);
            await LayoutAsync(window, dispatcher);
            await CapturePreviewAsync(window, dispatcher, "trash-box");
            await composition.Calendar.RunLeisureAsync(leisureHeld.Id);
            Check(composition.Timer.IsLeisureMode && composition.Countdown.IsRunning
                && composition.Countdown.Current?.Title == leisureHeld.Title && !composition.Schedule.IsActive
                && composition.Calendar.OnHoldPlans.Any(plan => plan.Title == leisureHeld.Title),
                "A held plan runs independently in leisure mode and remains in the hold box");
            await composition.Countdown.EndEarlyAsync();

            Check(listener.Errors.Count == 0,
                "Pages have no WPF binding errors: " + string.Join(Environment.NewLine, listener.Errors));
            await composition.FlushAsync();
        }
        finally
        {
            window?.Close();
            source.Listeners.Remove(listener);
            source.Switch.Level = previousLevel;
        }
    }

    private static async Task LayoutAsync(MainWindow window, Dispatcher dispatcher)
    {
        await dispatcher.InvokeAsync(() =>
        {
            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(1160, 750));
            root.Arrange(new Rect(0, 0, 1160, 750));
            root.UpdateLayout();
        }, DispatcherPriority.Loaded);
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    }

    private static async Task CapturePreviewAsync(MainWindow window, Dispatcher dispatcher, string pageName)
    {
        var directory = Environment.GetEnvironmentVariable("TOMATOTIMER_PREVIEW_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory)) return;
        await Task.Delay(260); // Capture the settled layout after the short page transition.
        await dispatcher.InvokeAsync(() =>
        {
            Directory.CreateDirectory(directory);
            var root = (FrameworkElement)window.Content;
            var image = new RenderTargetBitmap(1160, 750, 96, 96, PixelFormats.Pbgra32);
            var canvas = new DrawingVisual();
            using (var drawing = canvas.RenderOpen())
            {
                var bounds = new Rect(0, 0, 1160, 750);
                // Rendering Window.Content alone omits Window.Background and
                // leaves transparent notebook areas looking dark in viewers.
                drawing.DrawRectangle(window.Background, null, bounds);
                drawing.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.Fill }, null, bounds);
            }
            image.Render(canvas);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            var path = Path.Combine(directory, pageName + ".png");
            using (var stream = File.Create(path)) encoder.Save(stream);
            Console.WriteLine("PREVIEW " + Path.GetFullPath(path));
        }, DispatcherPriority.Render);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class BindingErrorListener : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { }
        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType,
            int id, string? message)
        {
            if (eventType is TraceEventType.Error or TraceEventType.Critical) Errors.Add(message ?? "Unknown binding error");
        }
        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType,
            int id, string? format, params object?[]? args)
        {
            var message = format is null ? "Unknown binding error"
                : args is { Length: > 0 } ? string.Format(format, args) : format;
            TraceEvent(eventCache, source, eventType, id, message);
        }
    }

    private sealed class SilentInteraction : IUserInteraction
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
        public Task<string[]> PickAudioFilesAsync() => Task.FromResult(Array.Empty<string>());
        public Task<string?> PickSaveFileAsync(string suggestedName) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);
        public Task<SaveDecision> ResolveUnsavedAsync(string title, string message) => Task.FromResult(SaveDecision.Cancel);
        public void ShowMessage(string title, string message) => throw new InvalidOperationException(title + ": " + message);
        public void OpenFolder(string path) => throw new InvalidOperationException("Unexpected shell interaction: " + path);
    }
}
