using System.Collections.Immutable;

namespace TomatoTimerPro.RuntimeStore.States.Settings;

public sealed record AppPreferences
{
    public ImmutableArray<string> RecentWorkspaces { get; init; } = [];
    public string? LastWorkspace { get; init; }
    public bool CloseToTray { get; init; } = true;
    public double MusicVolume { get; init; } = .5;
    public double AlarmVolume { get; init; } = .8;
    public bool ResumeMusic { get; init; } = true;
    public bool HasSeenTrayHint { get; init; }
    public ImmutableArray<string> MusicPaths { get; init; } = [];
    public string? DefaultAlarmPath { get; init; }
    public string? DefaultStartAlarmPath { get; init; }
    public double ScheduleRestMinutes { get; init; } = 1;
    public double DefaultDurationMinutes { get; init; } = 25;
    public string BackgroundColor { get; init; } = "#FAF5E9";
    public string? BackgroundImagePath { get; init; }
    public double BackgroundOpacity { get; init; } = .18;
    public bool FloatingClockEnabled { get; init; }
    public AnimationSettings Animation { get; init; } = new();
}
