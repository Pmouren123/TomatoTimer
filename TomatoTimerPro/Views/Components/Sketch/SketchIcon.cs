using System.Windows;
using System.Windows.Media;

namespace TomatoTimerPro.Views.Components.Sketch;

/// <summary>Native vector presenter for the pinned MIT Oreo Doodle icons, with matching original clock controls.</summary>
public sealed class SketchIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(SketchIcon),
        new FrameworkPropertyMetadata("timer", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(SketchIcon),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(67, 59, 52)), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(SketchIcon),
        new FrameworkPropertyMetadata(3.2, FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    private static readonly IReadOnlyDictionary<string, Geometry> Custom = new Dictionary<string, Geometry>(StringComparer.OrdinalIgnoreCase)
    {
        ["timer"] = Frozen("M24,7 C35,6 42,15 41,26 C40,37 32,42 23,41 C12,42 6,34 7,24 C6,14 14,7 24,7 M23,15 C23,19 24,24 24,25 L31,29 M19,3 L29,3 M24,3 L24,7 M36,8 L40,12"),
        ["pause"] = Frozen("M16,10 L15,38 M32,10 L33,38"),
        ["calendar"] = Frozen("M8,12 L40,11 L41,41 L7,40 Z M8,20 L40,19 M16,6 L16,15 M32,5 L32,14 M14,27 L19,27 M27,27 L33,27 M14,34 L19,34 M27,34 L33,34"),
        ["back"] = Frozen("M29,10 L15,24 L30,38"),
        ["forward"] = Frozen("M18,10 L32,24 L17,38"),
        ["stop"] = Frozen("M12,12 L36,11 L37,36 L12,37 Z"),
        ["maximize"] = Frozen("M10,11 L38,10 L37,38 L11,37 Z"),
        ["restore"] = Frozen("M17,10 L38,11 L37,31 L31,31 M10,17 L31,17 L31,38 L10,37 Z"),
        ["tomato"] = Frozen("M21,14 C9,8 3,21 8,34 C12,44 36,44 41,31 C46,18 35,9 26,14 M24,16 C24,11 20,7 16,6 M23,12 C27,4 34,6 36,7 C34,12 30,15 24,14 M24,22 L24,30 L30,33")
        , ["undo"] = Frozen("M38,15 C31,9 19,9 13,16 C7,23 10,34 18,38 C26,42 36,38 39,30 M13,16 L12,7 M13,16 L22,15")
        , ["redo"] = Frozen("M10,15 C17,9 29,9 35,16 C41,23 38,34 30,38 C22,42 12,38 9,30 M35,16 L36,7 M35,16 L26,15")
        , ["bold"] = Frozen("M15,9 L15,39 M15,9 C34,7 36,20 17,23 M17,23 C38,20 38,41 15,39")
        , ["italic"] = Frozen("M28,9 L18,39 M20,9 L34,9 M12,39 L27,39")
        , ["underline"] = Frozen("M14,10 L14,25 C14,34 21,38 28,35 C32,33 34,29 34,24 L34,10 M10,41 C18,40 30,40 38,41")
        , ["strike"] = Frozen("M13,10 L13,28 C13,36 22,39 29,35 M35,10 L35,26 C35,29 34,31 32,33 M9,24 C18,23 30,23 39,24")
        , ["heading"] = Frozen("M10,11 L10,37 M38,11 L38,37 M10,24 L38,24 M10,11 L10,37 M10,11 L10,37")
        , ["bullet"] = Frozen("M10,13 L14,13 M10,24 L14,24 M10,35 L14,35 M20,13 C26,12 32,12 38,13 M20,24 C26,23 32,23 38,24 M20,35 C26,34 32,34 38,35")
        , ["number"] = Frozen("M10,13 L14,13 M10,24 L14,24 M10,35 L14,35 M20,13 C26,12 32,12 38,13 M20,24 C26,23 32,23 38,24 M20,35 C26,34 32,34 38,35")
        , ["quote"] = Frozen("M12,18 C12,12 17,9 22,10 L22,17 C17,17 16,20 16,24 L22,24 L22,38 L10,38 L10,27 C10,23 11,20 12,18 M34,18 C34,12 39,9 44,10 L44,17 C39,17 38,20 38,24 L44,24 L44,38 L32,38 L32,27 C32,23 33,20 34,18")
        , ["code"] = Frozen("M18,13 L9,24 L18,35 M30,13 L39,24 L30,35 M27,10 L21,38")
        , ["link"] = Frozen("M20,30 L16,34 C12,38 7,34 10,29 L15,24 C18,21 22,21 25,24 M28,18 L32,14 C36,10 41,14 38,19 L33,24 C30,27 26,27 23,24 M17,28 L32,17")
        , ["rule"] = Frozen("M9,16 C18,15 30,15 39,16 M9,24 C18,23 30,23 39,24 M9,32 C18,31 30,31 39,32")
        , ["eye"] = Frozen("M8,24 C14,12 34,12 40,24 C34,36 14,36 8,24 M24,18 C28,18 30,20 30,24 C30,28 28,30 24,30 C20,30 18,28 18,24 C18,20 20,18 24,18")
        , ["sidebar"] = Frozen("M10,10 L38,10 L38,38 L10,38 Z M20,10 L20,38 M27,17 L34,17 M27,24 L34,24 M27,31 L34,31")
        , ["check"] = Frozen("M10,25 L19,34 L38,14")
    };
    private static Geometry Frozen(string path) { var geometry = Geometry.Parse(path); geometry.Freeze(); return geometry; }
    protected override Size MeasureOverride(Size availableSize) => new(double.IsNaN(Width) ? 24 : Width, double.IsNaN(Height) ? 24 : Height);
    protected override void OnRender(DrawingContext context)
    {
        var geometry = Custom.TryGetValue(Kind ?? "timer", out var custom) ? custom : TryFindResource("Doodle." + Kind) as Geometry;
        if (geometry is null) return;
        var scale = Math.Min(ActualWidth, ActualHeight) / 48d;
        if (scale <= 0) return;
        context.PushTransform(new TranslateTransform((ActualWidth - 48 * scale) / 2, (ActualHeight - 48 * scale) / 2));
        context.PushTransform(new ScaleTransform(scale, scale));
        context.DrawGeometry(null, new Pen(Stroke, Math.Max(.5, StrokeThickness))
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, geometry);
        context.Pop(); context.Pop();
    }
}
