using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace TomatoTimerPro.Views.Components.Sketch;

/// <summary>Shared visual feedback; animation never changes commands or business state.</summary>
public static class SketchMotion
{
    public static readonly DependencyProperty EnterProperty = DependencyProperty.RegisterAttached(
        "Enter", typeof(bool), typeof(SketchMotion), new PropertyMetadata(false, OnEnterChanged));
    public static void SetEnter(DependencyObject element, bool value) => element.SetValue(EnterProperty, value);
    public static bool GetEnter(DependencyObject element) => (bool)element.GetValue(EnterProperty);
    private static void OnEnterChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        if (source is not FrameworkElement element) return;
        element.Loaded -= OnLoaded;
        element.IsVisibleChanged -= OnVisibleChanged;
        if ((bool)args.NewValue) { element.Loaded += OnLoaded; element.IsVisibleChanged += OnVisibleChanged; }
    }
    private static void OnLoaded(object sender, RoutedEventArgs args)
    { if (sender is FrameworkElement element) Reveal(element); }
    private static void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
    { if ((bool)args.NewValue && sender is FrameworkElement { IsLoaded: true } element) Reveal(element); }

    public static readonly DependencyProperty ChangeProperty = DependencyProperty.RegisterAttached(
        "Change", typeof(object), typeof(SketchMotion), new PropertyMetadata(null, OnChange));
    public static void SetChange(DependencyObject element, object? value) => element.SetValue(ChangeProperty, value);
    public static object? GetChange(DependencyObject element) => element.GetValue(ChangeProperty);
    private static readonly DependencyProperty ChangeTokenProperty = DependencyProperty.RegisterAttached(
        "ChangeToken", typeof(long), typeof(SketchMotion), new PropertyMetadata(0L));
    private static void OnChange(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        if (source is not FrameworkElement { IsLoaded: true, IsVisible: true } element) return;
        var token = (long)element.GetValue(ChangeTokenProperty) + 1;
        element.SetValue(ChangeTokenProperty, token);
        element.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            if ((long)element.GetValue(ChangeTokenProperty) == token && element.IsVisible) Reveal(element, 10, .975);
        }));
    }

    public static readonly DependencyProperty FeedbackProperty = DependencyProperty.RegisterAttached(
        "Feedback", typeof(bool), typeof(SketchMotion), new PropertyMetadata(false, OnFeedbackChanged));
    public static void SetFeedback(DependencyObject element, bool value) => element.SetValue(FeedbackProperty, value);
    public static bool GetFeedback(DependencyObject element) => (bool)element.GetValue(FeedbackProperty);
    private static void OnFeedbackChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        if (source is not FrameworkElement element) return;
        element.MouseEnter -= OnFeedback; element.MouseLeave -= OnFeedback;
        element.GotKeyboardFocus -= OnFeedback; element.LostKeyboardFocus -= OnFeedback;
        element.PreviewMouseLeftButtonDown -= OnFeedback; element.PreviewMouseLeftButtonUp -= OnFeedback;
        if (!(bool)args.NewValue) return;
        element.MouseEnter += OnFeedback; element.MouseLeave += OnFeedback;
        element.GotKeyboardFocus += OnFeedback; element.LostKeyboardFocus += OnFeedback;
        element.PreviewMouseLeftButtonDown += OnFeedback; element.PreviewMouseLeftButtonUp += OnFeedback;
    }
    private static void OnFeedback(object sender, RoutedEventArgs args)
    {
        if (!SystemParameters.ClientAreaAnimation || sender is not FrameworkElement { IsEnabled: true } element) return;
        var pressed = args.RoutedEvent == UIElement.PreviewMouseLeftButtonDownEvent || args.RoutedEvent == Mouse.PreviewMouseDownEvent;
        var active = element.IsMouseOver || element.IsKeyboardFocusWithin;
        var motion = GetState(element);
        var scale = pressed ? .96 : active ? 1.018 : 1;
        var duration = pressed ? 100 : 280;
        Animate(motion.Scale, ScaleTransform.ScaleXProperty, scale, duration);
        Animate(motion.Scale, ScaleTransform.ScaleYProperty, scale, duration);
        Animate(motion.Offset, TranslateTransform.YProperty, pressed ? .7 : active ? -1.2 : 0, duration);
    }

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(MotionState), typeof(SketchMotion));
    private sealed class MotionState
    {
        public ScaleTransform Scale { get; } = new();
        public TranslateTransform Offset { get; } = new();
    }
    private static MotionState GetState(FrameworkElement element)
    {
        if (element.GetValue(StateProperty) is MotionState existing) return existing;
        var state = new MotionState();
        var transforms = new TransformGroup();
        if (element.RenderTransform is { } original && !original.Value.IsIdentity) transforms.Children.Add(original);
        transforms.Children.Add(state.Scale); transforms.Children.Add(state.Offset);
        element.SetCurrentValue(UIElement.RenderTransformProperty, transforms);
        element.SetCurrentValue(UIElement.RenderTransformOriginProperty, new Point(.5, .5));
        element.SetValue(StateProperty, state);
        return state;
    }
    private static void Animate(Animatable target, DependencyProperty property, double value, int milliseconds)
    {
        var current = (double)target.GetValue(property);
        target.SetCurrentValue(property, value);
        target.BeginAnimation(property, new DoubleAnimation(current, value, TimeSpan.FromMilliseconds(milliseconds))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
    }

    internal static void Reveal(FrameworkElement element, double distance = 20, double initialScale = .955, int delay = 0, int durationMilliseconds = 360)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var duration = new Duration(TimeSpan.FromMilliseconds(Math.Clamp(durationMilliseconds, 120, 1200)));
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var state = GetState(element);
        DoubleAnimation Motion(double from, double to) => new(from, to, duration)
        { EasingFunction = easing, FillBehavior = FillBehavior.Stop, BeginTime = TimeSpan.FromMilliseconds(delay) };
        var opacity = (double)element.GetAnimationBaseValue(UIElement.OpacityProperty);
        var fade = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(opacity, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + duration.TimeSpan.TotalMilliseconds))) { EasingFunction = easing });
        element.BeginAnimation(UIElement.OpacityProperty, fade);
        state.Offset.BeginAnimation(TranslateTransform.YProperty, Motion(distance, 0));
        var scaleIn = Motion(initialScale, 1);
        scaleIn.EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = .25 };
        state.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleIn);
        state.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleIn);
    }

    internal static void Move(FrameworkElement element, double x, double y)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var state = GetState(element);
        DoubleAnimation Offset(double from) => new(from, 0, TimeSpan.FromMilliseconds(320))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
        state.Offset.BeginAnimation(TranslateTransform.XProperty, Offset(x + state.Offset.X));
        state.Offset.BeginAnimation(TranslateTransform.YProperty, Offset(y + state.Offset.Y));
    }

    public static Task ExitAsync(FrameworkElement element)
    {
        if (!SystemParameters.ClientAreaAnimation || !element.IsLoaded) return Task.CompletedTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var motion = GetState(element);
        var fade = new DoubleAnimation(element.Opacity, 0, TimeSpan.FromMilliseconds(180))
        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) => completion.TrySetResult();
        Animate(motion.Scale, ScaleTransform.ScaleXProperty, .96, 180);
        Animate(motion.Scale, ScaleTransform.ScaleYProperty, .96, 180);
        Animate(motion.Offset, TranslateTransform.YProperty, 12, 180);
        element.BeginAnimation(UIElement.OpacityProperty, fade);
        return completion.Task;
    }
}

public sealed class AnimatedPageHost : ContentControl
{
    public static readonly DependencyProperty TransitionMillisecondsProperty = DependencyProperty.Register(
        nameof(TransitionMilliseconds), typeof(int), typeof(AnimatedPageHost),
        new FrameworkPropertyMetadata(340, FrameworkPropertyMetadataOptions.AffectsRender, OnTransitionChanged));
    public int TransitionMilliseconds
    {
        get => (int)GetValue(TransitionMillisecondsProperty);
        set => SetValue(TransitionMillisecondsProperty, value);
    }
    private readonly DispatcherTimer _transitionTimer = new();
    public AnimatedPageHost()
    {
        _transitionTimer.Tick += (_, _) =>
        {
            _transitionTimer.Stop();
            if (!IsLoaded) return;
            SetCurrentValue(UIElement.OpacityProperty, 1d);
            SketchMotion.Reveal(this, 12, .98, 0, Math.Clamp(TransitionMilliseconds, 160, 1000));
        };
        Loaded += (_, _) => QueueReveal();
    }
    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        QueueReveal();
    }
    private void QueueReveal()
    {
        if (!IsLoaded) return;
        SetCurrentValue(UIElement.OpacityProperty, 0d);
        _transitionTimer.Stop();
        _transitionTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(TransitionMilliseconds, 160, 1000));
        _transitionTimer.Start();
    }
    private static void OnTransitionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is AnimatedPageHost host && host.IsLoaded && host.Content is not null) host.QueueReveal();
    }
}
