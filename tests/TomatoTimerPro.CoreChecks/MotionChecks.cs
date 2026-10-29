using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TomatoTimerPro.Controllers.Components.Dialogs;
using TomatoTimerPro.Infrastructure.Desktop;
using TomatoTimerPro.Views.Components.Sketch;
using TomatoTimerPro.Views.Dialogs;

public static class MotionChecks
{
    public static async Task RunOnDispatcherAsync(Dispatcher dispatcher)
    {
        if (!SystemParameters.ClientAreaAnimation)
        { Console.WriteLine("SKIP Motion phases: Windows client-area animations are disabled"); return; }
        var button = new Button { Content = "保存计划", Width = 140, Margin = new Thickness(12) };
        var input = new TextBox { Text = "记录今天的计划", Margin = new Thickness(12) };
        var viewer = new ScrollViewer { Height = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border { Height = 4000, Background = Brushes.Beige } };
        var entries = new ObservableCollection<object> { new Row("第一项"), new Row("第二项"), new Row("第三项") };
        var list = new ItemsControl { ItemsSource = entries, Height = 150 };
        list.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><TextBlock Height='38' Text='{Binding Title}' /></DataTemplate>");
        var reveal = new Border { Width = 160, Height = 30, Opacity = .45, Background = Brushes.Tan };
        SketchMotion.SetEnter(reveal, true);
        var panel = new StackPanel(); panel.Children.Add(button); panel.Children.Add(input); panel.Children.Add(reveal);
        panel.Children.Add(viewer); panel.Children.Add(list);
        var window = new Window { Content = panel, Width = 450, Height = 640, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000, Opacity = 0 };
        try
        {
            window.Show(); await LayoutAsync(window, dispatcher); await Task.Delay(520);
            var chrome = (FrameworkElement)button.Template.FindName("Chrome", button);
            VisualStateManager.GoToState(button, "Pressed", true); await Task.Delay(140);
            Check(chrome.RenderTransform.Value.M11 < .96, "Button press has a clearly visible compression");
            VisualStateManager.GoToState(button, "MouseOver", true); await Task.Delay(380);
            Check(chrome.RenderTransform.Value.M11 > 1.025, "Button release eases to a visible hover lift");
            for (var i = 0; i < 5; i++) { VisualStateManager.GoToState(button, "Pressed", true); VisualStateManager.GoToState(button, "Normal", true); }
            await Task.Delay(300); Check(Math.Abs(chrome.RenderTransform.Value.M11 - 1) < .001, "Rapid button transitions settle without scale drift");
            Check(Math.Abs(reveal.Opacity - .45) < .001 && reveal.RenderTransform.Value.IsIdentity,
                "Reveal restores original opacity and transform after settling");
            input.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
            Check(input.RenderTransform is TransformGroup, "Input hover uses shared easing feedback");

            Wheel(viewer, -120); await Task.Delay(80); await LayoutAsync(window, dispatcher);
            var inFlight = viewer.VerticalOffset; Check(inFlight > 0 && inFlight < 90, "Wheel content moves through intermediate offsets");
            await Task.Delay(310); await LayoutAsync(window, dispatcher);
            var firstTarget = viewer.VerticalOffset; Check(firstTarget > inFlight, "Wheel decelerates to its target");
            Wheel(viewer, -120); Wheel(viewer, -120); await Task.Delay(380); await LayoutAsync(window, dispatcher);
            Check(Math.Abs(viewer.VerticalOffset - firstTarget * 3) < 1, "Rapid wheel events accumulate distance without dropping input");
            Wheel(viewer, -120); Wheel(viewer, 120); await Task.Delay(380); await LayoutAsync(window, dispatcher);
            Check(Math.Abs(viewer.VerticalOffset - firstTarget * 3) < 1, "Immediate wheel reversal cancels the pending distance");
            Wheel(viewer, -120); await Task.Delay(50);
            viewer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = viewer });
            viewer.ScrollToVerticalOffset(420); await Task.Delay(380); await LayoutAsync(window, dispatcher);
            Check(Math.Abs(viewer.VerticalOffset - 420) < 1, "Direct scrollbar input cancels wheel easing");

            var originalSecond = entries[1]; entries.RemoveAt(0); await LayoutAsync(window, dispatcher);
            var second = (FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(originalSecond);
            await Task.Delay(60); Check(second.RenderTransform.Value.OffsetY > 0, "Removing a row eases the remaining row into its new position");
            entries.Insert(0, new Row("新计划")); await LayoutAsync(window, dispatcher);
            var added = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(0);
            await Task.Delay(60); Check(added.Opacity < 1, "New plans fade and lift into place");
            entries.RemoveAt(0); entries.Add(new Row("另一项")); await LayoutAsync(window, dispatcher);
            await Task.Delay(540);
            foreach (var entry in entries)
            {
                var row = (FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(entry);
                Check(Math.Abs(row.Opacity - 1) < .001 && row.RenderTransform.Value.IsIdentity, "Rapid list edits settle without opacity or position drift");
            }
            await CheckModalCloseAsync(window, dispatcher);
            Console.WriteLine("PASS Motion: visible press/release, rapid transitions, original opacity, smooth accumulated/reversed wheel, direct-input cancellation, list add/remove and single-result modal close");
        }
        finally { window.Close(); }
    }

    private static async Task CheckModalCloseAsync(Window owner, Dispatcher dispatcher)
    {
        var interaction = new WpfUserInteraction(() => owner);
        Exception? failure = null; var observedClosing = false;
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                var dialog = Application.Current.Windows.OfType<SketchDialogWindow>().Single();
                var controller = (SketchDialogController)dialog.DataContext;
                controller.PrimaryCommand.Execute(null); controller.CancelCommand.Execute(null);
                observedClosing = dialog.IsVisible && !((FrameworkElement)dialog.Content).IsHitTestVisible;
            }
            catch (Exception error) { failure = error; foreach (var dialog in Application.Current.Windows.OfType<SketchDialogWindow>().ToArray()) dialog.Close(); }
        };
        timer.Start();
        try
        {
            var accepted = await interaction.ConfirmAsync("确认操作", "保留原有操作结果，同时完成关闭缓动。");
            if (failure is not null) throw failure;
            Check(accepted && observedClosing, "Modal closing eases out and repeated choices preserve the first result");
        }
        finally { timer.Stop(); }
    }
    private static void Wheel(ScrollViewer viewer, int delta) => viewer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = viewer });
    private static async Task LayoutAsync(Window window, Dispatcher dispatcher)
    { window.UpdateLayout(); await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); window.UpdateLayout(); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed record Row(string Title);
}
