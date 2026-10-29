using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TomatoTimerPro.Views.Components.Sketch;

/// <summary>Animates generated rows after collection changes without changing panel layout or virtualization.</summary>
public static class SketchListMotion
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SketchListMotion), new PropertyMetadata(false, OnEnabledChanged));
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(SketchListMotion));
    private static void OnEnabledChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        if (source is not ItemsControl items) return;
        if (items.GetValue(StateProperty) is State previous) previous.Dispose();
        items.ClearValue(StateProperty);
        if ((bool)args.NewValue) items.SetValue(StateProperty, new State(items));
    }
    private sealed class State : IDisposable
    {
        private readonly ItemsControl _items;
        private readonly Dictionary<object, Point> _positions = new(ReferenceEqualityComparer.Instance);
        private readonly INotifyCollectionChanged _collection;
        private bool _pending = true;
        private bool _active;
        public State(ItemsControl items)
        {
            _items = items; _collection = (INotifyCollectionChanged)items.Items;
            items.Loaded += OnLoaded; items.Unloaded += OnUnloaded;
            if (items.IsLoaded) Attach();
        }
        private void OnLoaded(object sender, RoutedEventArgs args) => Attach();
        private void Attach()
        {
            if (_active) return; _active = true; _pending = true;
            _items.LayoutUpdated += OnLayout; _collection.CollectionChanged += OnCollectionChanged;
        }
        private void OnUnloaded(object sender, RoutedEventArgs args) => Detach();
        private void Detach()
        {
            _items.LayoutUpdated -= OnLayout; _collection.CollectionChanged -= OnCollectionChanged;
            _positions.Clear(); _active = false;
        }
        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => _pending = true;
        private void OnLayout(object? sender, EventArgs args)
        {
            var panel = FindPanel(_items); if (panel is null) return;
            var next = new Dictionary<object, Point>(ReferenceEqualityComparer.Instance);
            var index = 0;
            foreach (UIElement child in panel.Children)
            {
                if (child is not FrameworkElement row || row.DataContext is not { } item) continue;
                // Layout offsets exclude active render transforms, so repeated changes never accumulate drift.
                var point = VisualTreeHelper.GetOffset(row); var position = new Point(point.X, point.Y);
                next[item] = position;
                if (SystemParameters.ClientAreaAnimation)
                {
                    if (_positions.TryGetValue(item, out var previous))
                    {
                        if ((_pending || panel is not VirtualizingPanel) && (previous - position).Length > .5)
                            SketchMotion.Move(row, previous.X - position.X, previous.Y - position.Y);
                    }
                    else if (_pending) SketchMotion.Reveal(row, 22, .95, Math.Min(index * 25, 125));
                }
                index++;
            }
            _positions.Clear(); foreach (var pair in next) _positions.Add(pair.Key, pair.Value);
            _pending = false;
        }
        public void Dispose() { Detach(); _items.Loaded -= OnLoaded; _items.Unloaded -= OnUnloaded; }
    }
    private static Panel? FindPanel(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Panel { IsItemsHost: true } panel) return panel;
            if (child is not ItemsControl && FindPanel(child) is { } nested) return nested;
        }
        return null;
    }
}
