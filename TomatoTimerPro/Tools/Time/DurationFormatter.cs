namespace TomatoTimerPro.Tools.Time;

public static class DurationFormatter
{
    public static string Format(TimeSpan duration)
    {
        var seconds = Math.Max(0, (long)Math.Ceiling(duration.TotalSeconds));
        return seconds >= 3600
            ? $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}"
            : $"{seconds / 60:00}:{seconds % 60:00}";
    }
}
