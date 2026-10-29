using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace TomatoTimerPro.Views.Components.Sketch;

/// <summary>Opens the same accessible context menu with a normal button click.</summary>
public static class SketchMenu
{
    public static readonly DependencyProperty OpenOnClickProperty = DependencyProperty.RegisterAttached(
        "OpenOnClick", typeof(bool), typeof(SketchMenu), new PropertyMetadata(false, OnChanged));

    public static bool GetOpenOnClick(DependencyObject element) => (bool)element.GetValue(OpenOnClickProperty);
    public static void SetOpenOnClick(DependencyObject element, bool value) => element.SetValue(OpenOnClickProperty, value);

    private static void OnChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not Button button) return;
        if ((bool)args.NewValue) button.Click += OnClick;
        else button.Click -= OnClick;
    }

    private static void OnClick(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        args.Handled = true;
    }
}
