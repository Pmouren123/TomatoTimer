using System.Windows;
using System.Windows.Input;
using TomatoTimerPro.Controllers.Components.Dialogs;

namespace TomatoTimerPro.Views.Dialogs;

public partial class SketchDialogWindow : Window
{
    public DialogChoice Result { get; set; } = DialogChoice.Cancel;
    public SketchDialogWindow() => InitializeComponent();
    // Complete the modal loop synchronously. Cancelling Closing to await an
    // animation can leave ShowDialog running with all input disabled.

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
