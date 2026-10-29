using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace TomatoTimerPro.Views.Components.Sketch;

/// <summary>Eases wheel input while direct thumb dragging and caret scrolling stay immediate.</summary>
public static class SketchScrollMotion
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SketchScrollMotion), new PropertyMetadata(false, OnEnabledChanged));
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    private static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached(
        "Offset", typeof(double), typeof(SketchScrollMotion), new PropertyMetadata(0d, OnOffsetChanged));
    private static readonly DependencyProperty TargetProperty = DependencyProperty.RegisterAttached(
        "Target", typeof(double), typeof(SketchScrollMotion), new PropertyMetadata(double.NaN));
    private static readonly DependencyProperty IsAnimatingProperty = DependencyProperty.RegisterAttached(
        "IsAnimating", typeof(bool), typeof(SketchScrollMotion), new PropertyMetadata(false));

    private static void OnEnabledChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        if (source is not ScrollViewer viewer) return;
        viewer.PreviewMouseWheel -= OnWheel; viewer.PreviewMouseDown -= OnDirectInput;
        viewer.PreviewKeyDown -= OnDirectInput; viewer.Unloaded -= OnUnloaded;
        if ((bool)args.NewValue)
        {
            viewer.PreviewMouseWheel += OnWheel; viewer.PreviewMouseDown += OnDirectInput;
            viewer.PreviewKeyDown += OnDirectInput; viewer.Unloaded += OnUnloaded;
        }
        else Stop(viewer);
    }
    private static void OnOffsetChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    { if (source is ScrollViewer viewer && (bool)viewer.GetValue(IsAnimatingProperty)) viewer.ScrollToVerticalOffset((double)args.NewValue); }
    private static void OnDirectInput(object sender, InputEventArgs args) { if (sender is ScrollViewer viewer) Stop(viewer); }
    private static void OnUnloaded(object sender, RoutedEventArgs args) { if (sender is ScrollViewer viewer) Stop(viewer); }
    private static void Stop(ScrollViewer viewer)
    {
        viewer.SetValue(IsAnimatingProperty, false);
        viewer.BeginAnimation(OffsetProperty, null); viewer.SetValue(TargetProperty, double.NaN);
    }
    private static void OnWheel(object sender, MouseWheelEventArgs args)
    {
        if (args.Handled || !SystemParameters.ClientAreaAnimation || sender is not ScrollViewer viewer
            || args.Delta == 0 || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        // Preview events tunnel from outer to inner viewers. Only the innermost
        // scrollable host under the pointer owns this wheel event.
        for (var node = args.OriginalSource as DependencyObject; node is not null && node != viewer; node = Parent(node))
            if (node is ScrollViewer inner && CanScroll(inner, args.Delta)) return;
        if (!CanScroll(viewer, args.Delta)) return;
        var previous = (double)viewer.GetValue(TargetProperty);
        var origin = double.IsNaN(previous) ? viewer.VerticalOffset : previous;
        var pixelScroll = !viewer.CanContentScroll || FindItemsHost(viewer) is { } items && VirtualizingPanel.GetScrollUnit(items) == ScrollUnit.Pixel;
        var lines = SystemParameters.WheelScrollLines;
        if (lines == 0) return;
        var step = lines < 0 ? viewer.ViewportHeight : Math.Max(1, lines) * (pixelScroll ? 30 : 1);
        var target = Math.Clamp(origin - args.Delta / 120d * step, 0, viewer.ScrollableHeight);
        if (Math.Abs(target - viewer.VerticalOffset) < .01)
        { Stop(viewer); viewer.ScrollToVerticalOffset(target); args.Handled = true; return; }
        var from = viewer.VerticalOffset;
        Stop(viewer); viewer.SetValue(OffsetProperty, from);
        viewer.SetValue(TargetProperty, target); viewer.SetValue(IsAnimatingProperty, true);
        var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(300))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
        animation.Completed += (_, _) =>
        {
            if ((double)viewer.GetValue(TargetProperty) != target) return;
            viewer.ScrollToVerticalOffset(target); Stop(viewer);
        };
        viewer.BeginAnimation(OffsetProperty, animation, HandoffBehavior.SnapshotAndReplace); args.Handled = true;
    }
    private static bool CanScroll(ScrollViewer viewer, int delta)
    {
        var target = (double)viewer.GetValue(TargetProperty);
        var offset = double.IsNaN(target) ? viewer.VerticalOffset : target;
        return viewer.ScrollableHeight > 0 && (delta < 0 ? offset < viewer.ScrollableHeight - .1 : offset > .1);
    }
    private static ItemsControl? FindItemsHost(DependencyObject viewer)
    {
        for (var node = Parent(viewer); node is not null; node = Parent(node)) if (node is ItemsControl items) return items;
        return null;
    }
    private static DependencyObject? Parent(DependencyObject node) => node is System.Windows.Media.Visual
        ? System.Windows.Media.VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
}
