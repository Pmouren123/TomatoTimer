using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TomatoTimerPro.Infrastructure.Desktop;

/// <summary>Composes only the inset background layer; the outer window remains transparent.</summary>
public sealed class WindowBackgroundService(Panel surface)
{
    private string? _lastImage;
    private BitmapImage? _cachedImage;
    private string _lastColor = "";
    private double _lastOpacity = -1;
    public void Apply(string color, string? imagePath, double opacity)
    {
        if (color == _lastColor && imagePath == _lastImage && opacity == _lastOpacity) return;
        var canvas = (Color)ColorConverter.ConvertFromString(color);
        if (!StringComparer.OrdinalIgnoreCase.Equals(imagePath, _lastImage))
        {
            _cachedImage = null;
            if (!string.IsNullOrEmpty(imagePath))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 1920;
                bitmap.UriSource = new Uri(imagePath, UriKind.Absolute); bitmap.EndInit(); bitmap.Freeze();
                _cachedImage = bitmap;
            }
        }
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRectangle(new SolidColorBrush(canvas), null, new Rect(0, 0, 1160, 790));
            if (_cachedImage is not null)
                context.DrawRectangle(new ImageBrush(_cachedImage) { Stretch = Stretch.UniformToFill, Opacity = Math.Clamp(opacity, 0, 1) }, null, new Rect(0, 0, 1160, 790));
        }
        drawing.Freeze();
        var brush = new DrawingBrush(drawing) { Stretch = Stretch.Fill }; brush.Freeze();
        surface.Background = brush;
        _lastColor = color; _lastImage = imagePath; _lastOpacity = opacity;
    }
}
