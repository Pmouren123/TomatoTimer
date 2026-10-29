using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TomatoTimerPro.Views.Components.Sketch;

public static class ScrollBarChecks
{
    public static async Task RunOnDispatcherAsync(Dispatcher dispatcher)
    {
        var viewer = new InputScrollViewer
        {
            Content = new TextBlock { Text = string.Join("\n", Enumerable.Range(1, 150).Select(i => $"{i:00}  今天的计划：整理思路，完成一段专注，记录新的收获。")),
                Width = 650, FontSize = 17, LineHeight = 28 },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var editor = new TextBox { Text = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"第 {i} 条计划\n把想法记在这里。")),
            AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.Wrap };
        var list = new ListBox { ItemsSource = Enumerable.Range(1, 100).Select(i => $"计划 {i:00} · 留一点时间阅读").ToArray() };
        var combo = new ComboBox { ItemsSource = Enumerable.Range(1, 30).Select(i => $"第 {i} 项计划状态").ToArray(), SelectedIndex = 0, MaxDropDownHeight = 180 };
        var grid = new Grid { Background = new SolidColorBrush(Color.FromRgb(250, 245, 233)), Margin = new Thickness(18) };
        for (var i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var (title, column) in new[] { ("页面滚动 · 手绘轨道", 0), ("计划编辑 · 长文本", 1), ("计划列表 · 多项计划", 2) })
        {
            var label = new TextBlock { Text = title, FontSize = 19, Margin = new Thickness(8, 0, 8, 14) };
            Grid.SetColumn(label, column); grid.Children.Add(label);
        }
        var controls = new FrameworkElement[] { viewer, editor, list };
        for (var i = 0; i < controls.Length; i++)
        {
            controls[i].Margin = new Thickness(8); Grid.SetColumn(controls[i], i); Grid.SetRow(controls[i], 1); grid.Children.Add(controls[i]);
        }
        combo.Margin = new Thickness(8, 12, 8, 0); Grid.SetColumn(combo, 2); Grid.SetRow(combo, 2); grid.Children.Add(combo);
        var hint = new TextBlock { Text = "铅笔描边 / 纸色滑块 / 悬停和按下缓动", Margin = new Thickness(8, 20, 8, 0), FontSize = 15 };
        Grid.SetRow(hint, 2); Grid.SetColumnSpan(hint, 2); grid.Children.Add(hint);
        var window = new Window { Content = grid, Width = 960, Height = 540, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000, Opacity = 0 };
        try
        {
            window.Show(); await LayoutAsync(window, dispatcher);
            var vertical = AssertSketchBar(viewer, Orientation.Vertical);
            var horizontal = AssertSketchBar(viewer, Orientation.Horizontal);
            Check(vertical.ActualHeight > viewer.ActualHeight - 30, "Vertical scrollbar stretches to the content viewport instead of a fixed 14-pixel height");
            Check(horizontal.ActualWidth > viewer.ActualWidth - 30, "Horizontal scrollbar stretches across the viewport");
            AssertGripFits(vertical); AssertGripFits(horizontal);
            AssertSketchBar(editor, Orientation.Vertical);
            AssertSketchBar(list, Orientation.Vertical);
            var panel = Descendants<VirtualizingStackPanel>(list).First();
            Check(panel.Children.Count < 100, "List scrolling preserves virtualization");

            ScrollBar.LineDownCommand.Execute(null, vertical); await LayoutAsync(window, dispatcher);
            Check(viewer.VerticalOffset > 0, "Hand-drawn down arrow command scrolls content");
            var lineOffset = viewer.VerticalOffset;
            ScrollBar.PageDownCommand.Execute(null, vertical); await LayoutAsync(window, dispatcher);
            Check(viewer.VerticalOffset > lineOffset + 30, "Clicking the track pages down");
            viewer.ScrollToTop(); await LayoutAsync(window, dispatcher);
            viewer.Wheel(-120); await LayoutAsync(window, dispatcher);
            Check(viewer.VerticalOffset > 0, "Mouse wheel still scrolls the customized viewer");
            viewer.ScrollToTop(); await LayoutAsync(window, dispatcher);
            viewer.PageDownKey(); await LayoutAsync(window, dispatcher);
            Check(viewer.VerticalOffset > 0, "Page Down keyboard scrolling remains available");
            viewer.ScrollToTop(); await LayoutAsync(window, dispatcher);
            var track = (Track)vertical.Template.FindName("PART_Track", vertical);
            track.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            track.Thumb.RaiseEvent(new DragDeltaEventArgs(0, 35) { RoutedEvent = Thumb.DragDeltaEvent });
            track.Thumb.RaiseEvent(new DragCompletedEventArgs(0, 35, false) { RoutedEvent = Thumb.DragCompletedEvent });
            await LayoutAsync(window, dispatcher);
            Check(viewer.VerticalOffset > 0, "Dragging the thumb moves content downward");
            ScrollBar.LineRightCommand.Execute(null, horizontal); await LayoutAsync(window, dispatcher);
            Check(viewer.HorizontalOffset > 0, "Horizontal arrow command scrolls in the correct direction");

            viewer.Content = new Border { Height = 50000, Width = 650 };
            await LayoutAsync(window, dispatcher); AssertGripFits(vertical);
            viewer.ScrollToBottom(); await LayoutAsync(window, dispatcher); AssertGripFits(vertical);
            viewer.ScrollToRightEnd(); await LayoutAsync(window, dispatcher); AssertGripFits(horizontal);
            viewer.Content = new TextBlock { Text = "没有溢出时自动隐藏滚动条" };
            await LayoutAsync(window, dispatcher);
            Check(!vertical.IsVisible && !horizontal.IsVisible, "Auto scrollbars collapse when content fits");
            viewer.Content = new TextBlock { Text = string.Join("\n", Enumerable.Range(1, 150).Select(i => $"{i:00}  今天的计划：整理思路，完成一段专注。")), Width = 650, FontSize = 17, LineHeight = 28 };
            viewer.ScrollToTop(); viewer.ScrollToLeftEnd();
            await LayoutAsync(window, dispatcher);
            await CaptureAsync(grid, "handdrawn-scrollbars");
            combo.IsDropDownOpen = true; await LayoutAsync(window, dispatcher);
            var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
            AssertSketchBar(popup.Child, Orientation.Vertical);
            combo.IsDropDownOpen = false;
            Console.WriteLine("PASS Hand-drawn scrollbars: full-height vertical rail, complete grip, long content, editor/list/dropdown, virtualized list, line/page/drag/wheel/keyboard, horizontal and auto-hide");
        }
        finally { combo.IsDropDownOpen = false; window.Close(); }
    }

    private static ScrollBar AssertSketchBar(DependencyObject root, Orientation orientation)
    {
        var bar = Descendants<ScrollBar>(root).FirstOrDefault(s => s.Orientation == orientation && s.IsVisible);
        Check(bar is not null, $"{root.GetType().Name} has a visible {orientation} scrollbar");
        Check(bar!.Template.FindName("ScrollRail", bar) is SketchBorder, $"{root.GetType().Name} uses the hand-drawn {orientation} rail");
        return bar;
    }

    private static void AssertGripFits(ScrollBar bar)
    {
        var track = (Track)bar.Template.FindName("PART_Track", bar);
        var thumb = track.Thumb;
        var thumbBounds = thumb.TransformToAncestor(track).TransformBounds(new Rect(thumb.RenderSize));
        Check(thumbBounds.Top >= -0.1 && thumbBounds.Left >= -0.1 && thumbBounds.Bottom <= track.ActualHeight + 0.1 && thumbBounds.Right <= track.ActualWidth + 0.1,
            $"{bar.Orientation} thumb stays within the track at scroll limits: thumb {thumbBounds}, track {track.RenderSize}");
        var grip = (FrameworkElement)thumb.Template.FindName("Grip", thumb);
        var bounds = grip.TransformToAncestor(thumb).TransformBounds(new Rect(grip.RenderSize));
        Check(bounds.Top >= 2 && bounds.Left >= 1 && bounds.Bottom <= thumb.ActualHeight - 2 && bounds.Right <= thumb.ActualWidth - 1,
            $"Complete {bar.Orientation} grip fits inside its thumb: thumb {thumb.ActualWidth}x{thumb.ActualHeight}, grip {bounds}");
        Check(bar.Orientation == Orientation.Vertical ? thumb.ActualHeight >= 24 : thumb.ActualWidth >= 24, "Long content retains a usable minimum thumb length");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static async Task LayoutAsync(Window window, Dispatcher dispatcher)
    {
        window.UpdateLayout();
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        window.UpdateLayout();
    }

    private static async Task CaptureAsync(FrameworkElement root, string name)
    {
        var directory = Environment.GetEnvironmentVariable("TOMATOTIMER_PREVIEW_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory)) return;
        await Task.Delay(180); Directory.CreateDirectory(directory);
        var image = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(root), null, new Rect(root.RenderSize));
        image.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(stream);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class InputScrollViewer : ScrollViewer
    {
        public void Wheel(int delta) => OnMouseWheel(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
            { RoutedEvent = Mouse.MouseWheelEvent, Source = this });
        public void PageDownKey() => OnKeyDown(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), Environment.TickCount, Key.PageDown)
            { RoutedEvent = Keyboard.KeyDownEvent, Source = this });
    }
}
