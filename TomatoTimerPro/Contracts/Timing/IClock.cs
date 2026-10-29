namespace TomatoTimerPro.Contracts.Timing;

public interface IClock
{
    long Milliseconds { get; }
    DateTimeOffset LocalNow { get; }
}

