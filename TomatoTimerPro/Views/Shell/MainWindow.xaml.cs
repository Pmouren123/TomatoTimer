using System.Windows;
using System.Windows.Controls;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TomatoTimerPro.Controllers.Shell;

namespace TomatoTimerPro.Views.Shell;

public partial class MainWindow : Window
{
    private INotifyPropertyChanged? _controller;
    private bool _markerPlaced;
    private readonly Dictionary<Button, SolidColorBrush> _navBackgrounds = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => QueueSelectionMarker();
    }

    public Panel BackgroundSurface => BackgroundLayer;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_controller is not null) _controller.PropertyChanged -= OnControllerPropertyChanged;
        _controller = e.NewValue as INotifyPropertyChanged;
        if (_controller is not null) _controller.PropertyChanged += OnControllerPropertyChanged;
        QueueSelectionMarker();
    }

    private void OnControllerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowController.CurrentPageId)) QueueSelectionMarker();
    }

    private void QueueSelectionMarker()
    {
        if (!IsLoaded) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(MoveSelectionMarker));
    }

    private void MoveSelectionMarker()
    {
        var page = (DataContext as MainWindowController)?.CurrentPageId;
        var target = page switch
        {
            "timer" => NavTimerButton,
            "calendar" => NavCalendarButton,
            "records" => NavRecordsButton,
            "music" => NavMusicButton,
            "settings" => NavSettingsButton,
            _ => null
        };
        if (target is null || target.ActualHeight <= 0 || NavSelectionMarker.RenderTransform is not TranslateTransform offset) return;
        var point = target.TranslatePoint(new Point(0, 0), NavSelectionLayer);
        var height = Math.Max(28, Math.Min(42, target.ActualHeight - 16));
        var destination = point.Y + (target.ActualHeight - height) / 2;
        var animation = (DataContext as MainWindowController)?.Animation ?? new TomatoTimerPro.RuntimeStore.States.Settings.AnimationSettings();
        AnimateNavigationBackgrounds(page, animation.SelectionFadeMilliseconds);
        NavSelectionMarker.Height = height;
        var current = offset.Y;
        offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(current, destination, TimeSpan.FromMilliseconds(_markerPlaced ? animation.SelectionSlideMilliseconds : 0))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        });
        if (!_markerPlaced)
        {
            _markerPlaced = true;
            NavSelectionMarker.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(animation.SelectionFadeMilliseconds))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    private void AnimateNavigationBackgrounds(string? page, int durationMilliseconds)
    {
        var colors = new Dictionary<Button, Color>
        {
            [NavTimerButton] = (Color)ColorConverter.ConvertFromString("#EDC4AC"),
            [NavCalendarButton] = (Color)ColorConverter.ConvertFromString("#F2DEA0"),
            [NavRecordsButton] = (Color)ColorConverter.ConvertFromString("#EADBA6"),
            [NavMusicButton] = (Color)ColorConverter.ConvertFromString("#CED9BF"),
            [NavSettingsButton] = (Color)ColorConverter.ConvertFromString("#E2D3BD")
        };
        var selected = colors.FirstOrDefault(pair =>
            (pair.Key == NavTimerButton && page == "timer")
            || (pair.Key == NavCalendarButton && page == "calendar")
            || (pair.Key == NavRecordsButton && page == "records")
            || (pair.Key == NavMusicButton && page == "music")
            || (pair.Key == NavSettingsButton && page == "settings"));
        foreach (var pair in colors)
        {
            if (pair.Key == selected.Key)
            {
                // A page change can raise more than one render notification. Keep the
                // current brush so a duplicate notification cannot restart the fade
                // from transparent and create a visible flash.
                if (_navBackgrounds.ContainsKey(pair.Key)) continue;
                var brush = new SolidColorBrush(Color.FromArgb(0, pair.Value.R, pair.Value.G, pair.Value.B));
                _navBackgrounds[pair.Key] = brush;
                pair.Key.Background = brush;
                brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(brush.Color,
                    Color.FromArgb(255, pair.Value.R, pair.Value.G, pair.Value.B),
                    TimeSpan.FromMilliseconds(Math.Clamp(durationMilliseconds, 100, 600)))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
            else if (_navBackgrounds.Remove(pair.Key, out var oldBrush))
            {
                var fading = oldBrush;
                var fade = new ColorAnimation
                {
                    From = fading.Color,
                    To = Color.FromArgb(0, fading.Color.R, fading.Color.G, fading.Color.B),
                    Duration = TimeSpan.FromMilliseconds(Math.Clamp(durationMilliseconds, 100, 600)),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                };
                fade.Completed += (_, _) =>
                {
                    if (!_navBackgrounds.ContainsKey(pair.Key)) pair.Key.ClearValue(Button.BackgroundProperty);
                };
                fading.BeginAnimation(SolidColorBrush.ColorProperty, fade);
            }
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Windows 11 otherwise adds its own rounded corners to an opaque HWND.
        var squareCorners = 1; // DWMWCP_DONOTROUND
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref squareCorners, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
