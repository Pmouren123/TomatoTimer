using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Threading;
using TomatoTimerPro.Controllers.Components.Planning;
using TomatoTimerPro.Views.Components.Sketch;

namespace TomatoTimerPro.Views.Dialogs;

public partial class PlanEditorWindow : Window
{
    private bool _allowClose;
    private bool _closing;
    private bool _exitRunning;
    public PlanEditorWindow() => InitializeComponent();
    public async void CloseApproved()
    {
        if (_exitRunning || _allowClose) return;
        _exitRunning = true;
        if (Content is FrameworkElement content)
        {
            content.IsHitTestVisible = false;
            await SketchMotion.ExitAsync(content);
        }
        _allowClose = true; Close();
    }
    private void OnPlanRowLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Grid row && row.Children.OfType<TextBox>().FirstOrDefault() is { Text.Length: 0 } input)
        {
            input.Focus();
            input.BringIntoView();
        }
    }
    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing || _exitRunning || DataContext is not PlanEditorController controller) return;
        _closing = true;
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            await controller.RequestCloseAsync();
        }
        finally { _closing = false; }
    }
}
