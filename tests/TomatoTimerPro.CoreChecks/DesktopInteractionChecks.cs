using System.Diagnostics;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TomatoTimerPro.Bootstrap;
using TomatoTimerPro.Contracts.Interaction;
using TomatoTimerPro.Controllers.Components.Dialogs;
using TomatoTimerPro.Infrastructure.Desktop;
using TomatoTimerPro.Infrastructure.Threading;
using TomatoTimerPro.Managers.Application;
using TomatoTimerPro.Views.Components.FloatingClock;
using TomatoTimerPro.Views.Dialogs;
using TomatoTimerPro.Views.Shell;

public static class DesktopInteractionChecks
{
    public static Task RunAsync(string root)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    foreach (var resource in new[] { "Resources/Icons/AppIcon.xaml", "Resources/Icons/DoodleIcons.xaml", "Resources/Styles/ApplicationStyles.xaml" })
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary
                        { Source = new Uri("pack://application:,,,/TomatoTimer;component/" + resource) });
                    CheckPrompts(app, dispatcher);
                    await CheckFocusWindowsAsync(app, dispatcher, root);
                }
                catch (Exception error) { failure = error; }
                finally { app.Shutdown(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
            if (failure is null) completion.TrySetResult();
            else completion.TrySetException(failure);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }

    private static void CheckPrompts(System.Windows.Application app, Dispatcher dispatcher)
    {
        var owner = new Window { Width = 100, Height = 100, Left = -30000, Top = -30000, ShowInTaskbar = false, ShowActivated = false };
        owner.Show();
        var interaction = new WpfUserInteraction(() => owner);
        try
        {
            foreach (var action in new[] { "删除", "取消", "native-close" })
            {
                var clock = Stopwatch.StartNew();
                var invoked = false;
                Exception? failure = null;
                var timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromMilliseconds(40) };
                timer.Tick += (_, _) =>
                {
                    var dialog = app.Windows.OfType<SketchDialogWindow>().SingleOrDefault();
                    if (dialog is null) return;
                    if (clock.Elapsed > TimeSpan.FromSeconds(3))
                    {
                        failure = new TimeoutException("Modal prompt did not close after " + action);
                        timer.Stop();
                        dialog.Close();
                        return;
                    }
                    if (invoked) return;
                    invoked = true;
                    try
                    {
                        var buttons = Descendants<Button>(dialog).Where(button => button.IsVisible).ToArray();
                        Check(buttons.Length == 2, "Deletion prompt exposes only Delete and Cancel");
                        if (action == "native-close") dialog.Close();
                        else
                        {
                            var button = buttons.Single(button => Equals(button.Content, action));
                            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                        }
                    }
                    catch (Exception error) { failure = error; timer.Stop(); dialog.Close(); }
                };
                timer.Start();
                bool result;
                try { result = interaction.ConfirmAsync("删除本次记录", "确认删除本次记录？").GetAwaiter().GetResult(); }
                finally { timer.Stop(); }
                if (failure is not null) throw failure;
                Check(invoked && result == (action == "删除"), "Modal result matches the clicked button");
                Check(app.Windows.OfType<SketchDialogWindow>().All(window => !window.IsVisible), "Prompt is fully closed");
                Check(owner.IsEnabled && owner.IsVisible, "Modal owner is enabled after the prompt closes");
            }
        }
        finally { owner.Close(); }
    }

    private static async Task CheckFocusWindowsAsync(System.Windows.Application app, Dispatcher dispatcher, string root)
    {
        var interaction = new FocusInteraction();
        var composition = new ApplicationComposition(new WpfUiDispatcher(dispatcher), interaction, System.IO.Path.Combine(root, "DesktopData"));
        var window = new MainWindow { Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
        var manager = new ApplicationMgr(app, window, composition, interaction);
        try
        {
            await manager.StartAsync();
            await composition.Timer.SetWorkspaceAsync(System.IO.Path.Combine(root, "DesktopWorkspace"));
            composition.Timer.SelectLeisureModeCommand.Execute(null);
            composition.Timer.DurationMinutes = "25";
            composition.Timer.SelectedAlarm = new("Silent", "__silent__");
            composition.Timer.SelectedStartAlarm = new("Silent", "__silent__");
            bool FloatingVisible() => app.Windows.OfType<FloatingClockWindow>().Any(clock => clock.IsVisible);
            async Task StartAsync()
            {
                composition.Timer.StartCommand.Execute(null);
                await WaitAsync(() => composition.Countdown.IsActive && !window.IsVisible && FloatingVisible());
            }
            await StartAsync();
            interaction.Choice = SaveDecision.Cancel;
            await composition.Timer.EndTaskAsync();
            Check(composition.Countdown.IsActive && !window.IsVisible && FloatingVisible(), "Cancelling the end prompt keeps focus windows unchanged");
            interaction.Choice = SaveDecision.Save;
            await composition.Timer.EndTaskAsync();
            Check(!composition.Countdown.IsActive && window.IsVisible && !FloatingVisible(), "Manual end restores main window and hides clock");
            await WaitAsync(() => composition.Timer.StartCommand.CanExecute(null));
            await StartAsync();
            await composition.Timer.DeleteCurrentRecordAsync();
            Check(composition.Countdown.Current is null && window.IsVisible && !FloatingVisible(), "Deleting active record restores main window and hides clock");
            await WaitAsync(() => composition.Timer.StartCommand.CanExecute(null));
            composition.Timer.DurationMinutes = "0.01";
            await StartAsync();
            await WaitAsync(() => !composition.Countdown.IsActive);
            Check(window.IsVisible && !FloatingVisible(), "Natural completion restores main window and hides clock");
            Check(interaction.Errors.Count == 0, string.Join("; ", interaction.Errors));
        }
        finally { await manager.DisposeAsync(); window.Close(); }
    }

    private static async Task WaitAsync(Func<bool> ready)
    {
        var deadline = Stopwatch.StartNew();
        while (!ready())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Desktop state transition timed out");
            await Task.Delay(25);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class FocusInteraction : IUserInteraction
    {
        public SaveDecision Choice { get; set; } = SaveDecision.Save;
        public List<string> Errors { get; } = [];
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
        public Task<string[]> PickAudioFilesAsync() => Task.FromResult(Array.Empty<string>());
        public Task<string?> PickSaveFileAsync(string suggestedName) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
        public Task<SaveDecision> ResolveUnsavedAsync(string title, string message) => Task.FromResult(SaveDecision.Cancel);
        public Task<SaveDecision> ChooseTaskEndAsync(string taskTitle) => Task.FromResult(Choice);
        public void ShowMessage(string title, string message) => Errors.Add(title + ": " + message);
        public void OpenFolder(string path) { }
    }
}
