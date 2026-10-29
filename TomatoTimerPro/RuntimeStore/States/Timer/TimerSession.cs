using System.Text.Json.Serialization;

namespace TomatoTimerPro.RuntimeStore.States.Timer;

[JsonConverter(typeof(JsonStringEnumConverter<SessionStatus>))]
public enum SessionStatus { Running, Paused, Completed, Cancelled, Interrupted, EndedEarly }

public sealed record TimerSession
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string WorkspacePath { get; init; } = "";
    public string Title { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public double PlannedSeconds { get; init; }
    public double FocusedSeconds { get; init; }
    public SessionStatus Status { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public string? AlarmPath { get; init; }
    public DateOnly Day => DateOnly.FromDateTime(StartedAt.LocalDateTime);
    public string FileStem => $"{StartedAt.LocalDateTime:HHmmss}_{Id}";
}

public sealed record TimerState(TimerSession? Current = null);
