using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Media3D;

public static class BindingChecks
{
    /// <summary>Checks loaded visuals and logical content: templates often attach
    /// bindings only once a real presentation source has been created.</summary>
    public static void AssertProgressBindingsAreOneWay(DependencyObject root, bool requireProgress = false)
    {
        var count = 0;
        foreach (var element in Descendants(root))
        {
            if (element is not ProgressBar progress) continue;
            var binding = BindingOperations.GetBinding(progress, RangeBase.ValueProperty);
            if (binding?.Path?.Path != "Progress") continue;
            count++;
            if (binding.Mode != BindingMode.OneWay)
                throw new InvalidOperationException("Read-only timer Progress must bind explicitly with Mode=OneWay.");
            var expression = BindingOperations.GetBindingExpression(progress, RangeBase.ValueProperty);
            if (expression?.HasError == true)
                throw new InvalidOperationException("Loaded Progress binding reported a WPF binding error.");
        }
        if (requireProgress && count == 0)
            throw new InvalidOperationException("Expected a loaded timer ProgressBar binding in the visual/logical tree.");
    }

    public static void AssertTimerTextIsVisible(DependencyObject root, string expected)
    {
        var displays = Descendants(root).OfType<TextBlock>()
            .Where(text => BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path?.Path == "RemainingText")
            .ToList();
        if (displays.Count == 0 || string.IsNullOrWhiteSpace(expected))
            throw new InvalidOperationException("Expected a nonempty loaded timer display.");
        foreach (var display in displays)
        {
            if (display.Text != expected || display.ActualWidth <= 0 || display.ActualHeight <= 0 || !display.IsVisible)
                throw new InvalidOperationException("Timer digits are not bound and laid out visibly: " + display.Text);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var seen = new HashSet<DependencyObject>();
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count != 0)
        {
            var element = pending.Pop();
            if (!seen.Add(element)) continue;
            yield return element;
            if (element is Visual or Visual3D)
            {
                for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
                    pending.Push(VisualTreeHelper.GetChild(element, index));
            }
            foreach (var child in LogicalTreeHelper.GetChildren(element))
                if (child is DependencyObject logicalChild) pending.Push(logicalChild);
        }
    }
}
