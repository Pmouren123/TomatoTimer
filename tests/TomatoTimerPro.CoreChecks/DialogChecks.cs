using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TomatoTimerPro.Controllers.Components.Dialogs;
using TomatoTimerPro.Views.Dialogs;

public static class DialogChecks
{
    /// <summary>Creates the three actual dialog views on the WPF smoke-check
    /// dispatcher without starting a modal loop or presenting anything onscreen.</summary>
    public static async Task RunOnDispatcherAsync(Dispatcher dispatcher)
    {
        foreach (var mode in Enum.GetValues<SketchDialogMode>())
        {
            var controller = new SketchDialogController("保存今天的收获", "当前日志有未保存的修改。\n请保存这次专注留下的收获，或继续编辑。", mode);
            var choice = DialogChoice.Cancel;
            controller.CloseRequested += result => choice = result;
            controller.PrimaryCommand.Execute(null);
            Check(choice == DialogChoice.Primary, "Primary dialog action is explicit");
            controller.SecondaryCommand.Execute(null);
            Check(choice == DialogChoice.Secondary, "Secondary dialog action is explicit");
            controller.CancelCommand.Execute(null);
            Check(choice == DialogChoice.Cancel, "Dialog cancellation preserves cancel semantics");
            Check(controller.HasSecondary == (mode is SketchDialogMode.SaveChanges or SketchDialogMode.EndTask), "Save and end-task prompts show a discard action");
            Check(controller.HasCancel == (mode != SketchDialogMode.Information), "Confirm and save prompts expose cancellation");
            var window = new SketchDialogWindow
            {
                DataContext = controller,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -30000,
                Top = -30000,
                Opacity = 0
            };
            try
            {
                Check(window.WindowStyle == WindowStyle.None && window.AllowsTransparency && !window.ShowInTaskbar,
                    "Prompts use a transparent custom window rather than native message chrome");
                // Let SizeToContent and the real presentation source determine
                // height. Measuring only an unattached content root previously
                // used the minimum height and clipped the action row.
                window.Show();
                await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                var root = (FrameworkElement)window.Content;
                var size = new Size(root.ActualWidth, root.ActualHeight);
                Check(root.ActualHeight > 0, "Custom dialog view lays out all content");
                var directory = Environment.GetEnvironmentVariable("TOMATOTIMER_PREVIEW_DIRECTORY");
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    await Task.Delay(260);
                    Directory.CreateDirectory(directory);
                    var image = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
                    image.Render(root);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    var path = Path.Combine(directory, "dialog-" + mode.ToString().ToLowerInvariant() + ".png");
                    using (var stream = File.Create(path)) encoder.Save(stream);
                    Console.WriteLine("PREVIEW " + Path.GetFullPath(path));
                }
            }
            finally { window.Close(); }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
