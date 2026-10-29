using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TomatoTimerPro.Views.Components.Sketch;

/// <summary>A lightweight ink outline; the stable curves never jitter during layout or animation.</summary>
public sealed class SketchBorder : Border
{
    protected override void OnRender(DrawingContext context)
    {
        var thickness = Math.Max(Math.Max(BorderThickness.Left, BorderThickness.Right), Math.Max(BorderThickness.Top, BorderThickness.Bottom));
        var inset = Math.Max(.8, thickness / 2 + .4);
        var width = ActualWidth - inset * 2;
        var height = ActualHeight - inset * 2;
        if (width <= 0 || height <= 0) return;
        var radius = Math.Min(Math.Min(width, height) / 2,
            Math.Max(4, (CornerRadius.TopLeft + CornerRadius.TopRight + CornerRadius.BottomLeft + CornerRadius.BottomRight) / 4));
        var outline = Outline(inset, inset, width, height, radius);
        var pen = thickness > 0 && BorderBrush is not null ? new Pen(BorderBrush, thickness)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round } : null;
        context.DrawGeometry(Background, pen, outline);
        if (pen is not null && width > 35 && height > 20)
        {
            context.PushOpacity(.2);
            var echo = new Pen(BorderBrush, Math.Max(.55, thickness * .55))
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            context.DrawGeometry(null, echo, Outline(inset + 1.2, inset + .8, width - 1.8, height - 1.3, radius));
            context.Pop();
        }
    }

    private static Geometry Outline(double x, double y, double w, double h, double r)
    {
        var geometry = new StreamGeometry();
        using (var ink = geometry.Open())
        {
            ink.BeginFigure(new Point(x + r, y + .4), true, true);
            ink.BezierTo(new(x + w * .36, y - .3), new(x + w * .7, y + 1), new(x + w - r, y), true, false);
            ink.QuadraticBezierTo(new(x + w - .4, y - .2), new(x + w, y + r), true, false);
            ink.BezierTo(new(x + w + .5, y + h * .36), new(x + w - .7, y + h * .72), new(x + w, y + h - r), true, false);
            ink.QuadraticBezierTo(new(x + w + .2, y + h - .1), new(x + w - r, y + h), true, false);
            ink.BezierTo(new(x + w * .65, y + h + .4), new(x + w * .28, y + h - .8), new(x + r, y + h), true, false);
            ink.QuadraticBezierTo(new(x + .2, y + h + .1), new(x, y + h - r), true, false);
            ink.BezierTo(new(x + .8, y + h * .65), new(x - .4, y + h * .3), new(x, y + r), true, false);
            ink.QuadraticBezierTo(new(x - .2, y + .3), new(x + r, y + .4), true, false);
        }
        geometry.Freeze();
        return geometry;
    }
}
