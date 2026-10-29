namespace TomatoTimerPro.RuntimeStore.States.Settings;

/// <summary>Timing knobs for navigation feedback. Values are persisted with app preferences.</summary>
public sealed record AnimationSettings
{
    public int PageTransitionMilliseconds { get; init; } = 340;
    public int SelectionSlideMilliseconds { get; init; } = 340;
    public int SelectionFadeMilliseconds { get; init; } = 220;
    public int NavigationCooldownMilliseconds { get; init; } = 420;

    public AnimationSettings Normalize() => this with
    {
        PageTransitionMilliseconds = Math.Clamp(PageTransitionMilliseconds, 160, 1000),
        SelectionSlideMilliseconds = Math.Clamp(SelectionSlideMilliseconds, 160, 1000),
        SelectionFadeMilliseconds = Math.Clamp(SelectionFadeMilliseconds, 100, 600),
        NavigationCooldownMilliseconds = Math.Clamp(NavigationCooldownMilliseconds, 180, 1200)
    };
}
