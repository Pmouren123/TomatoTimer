using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TomatoTimerPro.Contracts.Interaction;
using TomatoTimerPro.Controllers.Components.Planning;
using TomatoTimerPro.Controllers.Pages.Calendar;
using TomatoTimerPro.RuntimeStore.States.Planning;
using TomatoTimerPro.Views.Dialogs;

public static class PlanningWpfChecks
{
    public static async Task RunAsync(CalendarPageController calendar, IReadOnlyList<PlanEntry> plans, Dispatcher dispatcher)
    {
        var day = plans[0].Day;
        using var editor = new PlanEditorController(day, plans, true, new Interaction(), p => calendar.SaveDayAsync(day, p));
        var window = new PlanEditorWindow
        {
            DataContext = editor, ShowActivated = false, ShowInTaskbar = false, Opacity = 0,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000
        };
        editor.CloseRequested += window.CloseApproved;
        try
        {
            window.Show();
            await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            Check(editor.Plans.Count == 4, "Independent plan editor supports multiple plans and appends a new row");
            editor.Plans[^1].Title = "给明天留一点灵感";
            Check(!editor.Plans[^1].HasPomodoro && editor.Plans[^1].AddPomodoroCommand.CanExecute(null),
                "A plain plan exposes an explicit add-pomodoro action");
            editor.Plans[^1].AddPomodoroCommand.Execute(null);
            foreach (var (command, minutes) in new[]
            {
                (editor.Plans[^1].ShortFocusCommand, "15"),
                (editor.Plans[^1].StandardFocusCommand, "25"),
                (editor.Plans[^1].LongFocusCommand, "45")
            })
            {
                command.Execute(null);
                Check(editor.Plans[^1].DurationMinutes == minutes && editor.IsDirty,
                    "Focus presets update the editable draft: " + minutes);
            }
            editor.Plans[^1].DurationMinutes = "40";
            editor.Plans[1].DeleteCommand.Execute(null);
            await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            Check(Descendants(window).OfType<TextBox>().Any(input =>
                System.Windows.Automation.AutomationProperties.GetName(input) == "专注时长（分钟）"
                && input.Text == "40" && input.IsVisible && input.ActualWidth >= 60),
                "The plan's attached pomodoro shows a visible editable duration");
            var combo = Descendants(window).OfType<ComboBox>().First();
            var selection = (ContentPresenter)combo.Template.FindName("SelectionContent", combo);
            var label = Descendants(selection).OfType<TextBlock>().Single(t => t.Text == "已完成");
            var bounds = label.TransformToAncestor(combo).TransformBounds(new Rect(label.RenderSize));
            Check(bounds.Left >= 0 && bounds.Right <= combo.ActualWidth - 27 && bounds.Top >= 0 && bounds.Bottom <= combo.ActualHeight,
                "Selected dropdown text fits the control without overlapping the arrow");

            combo.IsDropDownOpen = true;
            await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            await Task.Delay(260);
            var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
            var options = Descendants(popup.Child).OfType<ComboBoxItem>().ToArray();
            Check(options.Length == PlanStatusChoice.All.Count && PlanStatusChoice.All.Select(choice => choice.Label).All(expected =>
                options.Any(option => Descendants(option).OfType<TextBlock>().Any(text => text.Text == expected))),
                "Dropdown displays all configured Chinese status labels");
            foreach (var option in options)
            {
                foreach (var text in Descendants(option).OfType<TextBlock>())
                {
                    var rect = text.TransformToAncestor(option).TransformBounds(new Rect(text.RenderSize));
                    Check(rect.Left >= 0 && rect.Top >= 0 && rect.Right <= option.ActualWidth + 1 && rect.Bottom <= option.ActualHeight + 1,
                        "Expanded dropdown labels are not clipped by their containers");
                }
            }
            Capture((FrameworkElement)popup.Child, "plan-status-dropdown");
            combo.IsDropDownOpen = false;
            await Task.Delay(260);
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            var saveButton = Descendants(window).OfType<Button>().Single(button => button.Command == editor.SaveCommand);
            Check(saveButton.IsVisible && saveButton.ActualWidth >= 126,
                "Save remains available at the minimum editor size");
            var durationInput = Descendants(window).OfType<TextBox>().Last(input =>
                System.Windows.Automation.AutomationProperties.GetName(input) == "专注时长（分钟）");
            durationInput.BringIntoView();
            await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            Capture((FrameworkElement)window.Content, "plan-editor-compact");
            window.Width = 760;
            window.Height = 700;
            ((ScrollViewer)window.FindName("PlanScroll")).ScrollToTop();
            await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            Capture((FrameworkElement)window.Content, "plan-editor");
            Check(await editor.SaveAsync(), "Dedicated edit panel saves and closes with multiple independent rows");
            using (var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                while (window.IsVisible) await Task.Delay(10, closeTimeout.Token);
            Check(!window.IsVisible && calendar.TodayPlans.Count == 2 && calendar.OnHoldPlans.Count == 1
                && calendar.TodayPlans.Any(p => p.HasPomodoro && p.DurationMinutes == 40),
                "Saving keeps the attached pomodoro duration and routes held plans into the hold box");
        }
        finally
        {
            editor.CloseRequested -= window.CloseApproved;
            if (window.IsVisible) window.CloseApproved();
        }
        await CheckCardActionsAsync(day, dispatcher);
    }

    private static async Task CheckCardActionsAsync(DateOnly day, Dispatcher dispatcher)
    {
        var edits = 0;
        var deletes = 0;
        var item = new PlanListItem(new PlanEntry("card-preview", day,
            "整理读书笔记，把今天想到的小点子写下来", DurationMinutes: 1440),
            (_, _) => { edits++; return Task.CompletedTask; },
            _ => { deletes++; return Task.CompletedTask; },
            (_, _) => Task.CompletedTask, error => throw error, _ => Task.CompletedTask, () => true);
        var card = new ContentControl
        {
            Content = item,
            ContentTemplate = (DataTemplate)System.Windows.Application.Current.FindResource("PlanListTemplate"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        var host = new Window
        {
            Content = card, Width = 226, SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false, Opacity = 0,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000
        };
        try
        {
            host.Show();
            await dispatcher.InvokeAsync(host.UpdateLayout, DispatcherPriority.Loaded);
            var duration = Descendants(card).OfType<TextBlock>().Single(text => text.Name == "PlanDurationText");
            var durationBounds = duration.TransformToAncestor(card).TransformBounds(new Rect(duration.RenderSize));
            Check(durationBounds.Right <= card.ActualWidth && durationBounds.Left >= 106,
                "The largest focus duration fits beside status in a narrow timer card");
            var more = Descendants(card).OfType<Button>().Single(button => button.ContextMenu is not null);
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await dispatcher.InvokeAsync(host.UpdateLayout, DispatcherPriority.Loaded);
            var menu = more.ContextMenu!;
            Check(menu.IsOpen && ReferenceEquals(menu.DataContext, item),
                "A normal click opens the plan actions menu with the correct plan");
            var actions = menu.Items.OfType<MenuItem>().ToArray();
            Check(ReferenceEquals(actions[0].Command, item.EditCommand)
                && ReferenceEquals(actions[1].Command, item.DeleteCommand),
                "Menu actions bind to the selected plan's edit and delete commands");
            actions[0].Command.Execute(null);
            actions[1].Command.Execute(null);
            Check(edits == 1 && deletes == 1, "Menu commands forward to the corresponding plan actions");
            menu.IsOpen = false;
            await Task.Delay(260);
            Capture(card, "plan-card-narrow");
        }
        finally { host.Close(); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static void Capture(FrameworkElement root, string name)
    {
        var directory = Environment.GetEnvironmentVariable("TOMATOTIMER_PREVIEW_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var image = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        var path = Path.Combine(directory, name + ".png");
        using (var stream = File.Create(path)) encoder.Save(stream);
        Console.WriteLine("PREVIEW " + Path.GetFullPath(path));
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
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
