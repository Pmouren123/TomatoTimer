using System.Windows;
using System.Windows.Controls;

namespace TomatoTimerPro.Views.Components.Schedule;

/// <summary>Allocates the available width according to each item's duration.</summary>
public sealed class ProportionalPanel : Panel
{
    public static readonly DependencyProperty WeightProperty = DependencyProperty.RegisterAttached(
        "Weight", typeof(double), typeof(ProportionalPanel),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static double GetWeight(DependencyObject element) => (double)element.GetValue(WeightProperty);
    public static void SetWeight(DependencyObject element, double value) => element.SetValue(WeightProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = 0d;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var total = InternalChildren.Cast<UIElement>().Sum(Weight);
        var left = 0d;
        foreach (UIElement child in InternalChildren)
        {
            var width = total > 0 ? finalSize.Width * Weight(child) / total : 0;
            child.Arrange(new Rect(left, 0, width, finalSize.Height));
            left += width;
        }
        return finalSize;
    }

    private static double Weight(UIElement element)
    {
        var weight = GetWeight(element);
        return double.IsFinite(weight) && weight > 0 ? weight : 0;
    }
}
