using System.Windows;
using System.Windows.Input;

namespace TomatoTimerPro.Views.Components.FloatingClock;

public partial class FloatingClockWindow : Window
{
    public FloatingClockWindow() => InitializeComponent();

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
