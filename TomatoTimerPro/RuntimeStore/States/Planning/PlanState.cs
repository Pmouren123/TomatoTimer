using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace TomatoTimerPro.RuntimeStore.States.Planning;

[JsonConverter(typeof(JsonStringEnumConverter<PlanStatus>))]
public enum PlanStatus { Incomplete, Completed, OnHold, Abandoned }

public sealed record PlanEntry(string Id, DateOnly Day, string Title, PlanStatus Status = PlanStatus.Incomplete,
    double DurationMinutes = 25, bool HasPomodoro = true, DateOnly? AbandonedOn = null);
public sealed record PlanState
{
    public ImmutableArray<PlanEntry> Entries { get; init; } = [];
}
