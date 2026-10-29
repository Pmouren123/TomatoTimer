using System.Windows.Controls;
using System.Windows;
using System.ComponentModel;
using TomatoTimerPro.Controllers.Pages.Timer;

namespace TomatoTimerPro.Views.Pages.Timer;

public partial class TimerPage : UserControl
{
    private TimerPageController? _controller;
    private bool _layoutQueued;
    private double _lastLayoutWidth = -1;
    private bool? _lastSchedule;
    private bool? _lastEmpty;

    public TimerPage()
    {
        InitializeComponent();
        Loaded += (_, _) => ObserveController();
        Unloaded += (_, _) => StopObserving();
        DataContextChanged += (_, _) => { if (IsLoaded) ObserveController(); };
    }

    private void ObserveController()
    {
        StopObserving();
        _controller = DataContext as TimerPageController;
        if (_controller is not null) _controller.PropertyChanged += OnControllerChanged;
        UpdateLayoutForMode();
    }

    private void StopObserving()
    {
        if (_controller is not null) _controller.PropertyChanged -= OnControllerChanged;
        _controller = null;
    }

    private void OnControllerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TimerPageController.IsScheduleMode) or nameof(TimerPageController.ShowScheduleEmptyState))
            UpdateLayoutForMode();
    }

    private void OnTimerLayoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_layoutQueued) return;
        _layoutQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
        {
            _layoutQueued = false;
            UpdateLayoutForMode();
        }));
    }

    private void UpdateLayoutForMode()
    {
        var schedule = _controller?.IsScheduleMode == true;
        var empty = _controller?.ShowScheduleEmptyState == true;
        if (_lastLayoutWidth >= 0 && _lastSchedule == schedule && _lastEmpty == empty
            && Math.Abs(_lastLayoutWidth - TimerLayout.ActualWidth) < 2) return;
        _lastLayoutWidth = TimerLayout.ActualWidth;
        _lastSchedule = schedule;
        _lastEmpty = empty;
        PlannerColumn.Width = new GridLength(schedule ? 260 : 0);
        PlannerGap.Width = new GridLength(schedule ? 16 : 0);
        PlannerCard.Visibility = schedule ? Visibility.Visible : Visibility.Collapsed;
        SetupCard.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        var compact = TimerLayout.ActualWidth < (schedule ? 900 : 620);
        SetupGap.Width = new GridLength(compact || empty ? 0 : 16);
        SetupColumn.Width = new GridLength(compact || empty ? 0 : 240);
        Grid.SetColumn(SetupCard, compact ? 2 : 4);
        Grid.SetRow(SetupCard, compact ? 1 : 0);
        SetupCard.Margin = compact ? new Thickness(0, 16, 0, 0) : new Thickness(0);
    }
}
