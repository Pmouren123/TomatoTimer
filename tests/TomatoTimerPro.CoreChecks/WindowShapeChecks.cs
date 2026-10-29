using System.Windows;
using System.Windows.Controls;
using System.Windows.Shell;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TomatoTimerPro.Views.Shell;

public static class WindowShapeChecks
{
    public static void AssertSquareCorners(MainWindow window)
    {
        if (window.AllowsTransparency || WindowChrome.GetWindowChrome(window)?.CornerRadius != new CornerRadius(0))
            throw new InvalidOperationException("The main window must be opaque with square window chrome.");
        var root = (FrameworkElement)window.Content;
        var surface = window.BackgroundSurface;
        if (root is not Border { CornerRadius.TopLeft: 0, CornerRadius.TopRight: 0, CornerRadius.BottomLeft: 0, CornerRadius.BottomRight: 0 }
            || surface.Clip is not null || !surface.ClipToBounds)
            throw new InvalidOperationException("The outer frame and background must use rectangular bounds without rounded clipping.");
        var width = (int)Math.Ceiling(root.ActualWidth);
        var height = (int)Math.Ceiling(root.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        byte Alpha(int x, int y) => pixels[(y * width + x) * 4 + 3];
        foreach (var point in new[] { (0, 0), (width - 1, 0), (0, height - 1), (width - 1, height - 1) })
        {
            var index = (point.Item2 * width + point.Item1) * 4;
            var color = ((SolidColorBrush)((Border)root).BorderBrush).Color;
            if (Alpha(point.Item1, point.Item2) != 255 || pixels[index] != color.B || pixels[index + 1] != color.G || pixels[index + 2] != color.R)
                throw new InvalidOperationException("All four square corner pixels must be completely opaque.");
        }
        if (Alpha(width / 2, height / 2) < 250)
            throw new InvalidOperationException("The inner canvas must remain opaque and readable.");
    }
}
