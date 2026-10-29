namespace TomatoTimerPro.Contracts.Lifecycle;

public interface IPageLifecycle : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken token);
    Task EnterAsync(CancellationToken token);
    Task<bool> CanLeaveAsync(CancellationToken token);
    Task LeaveAsync(CancellationToken token);
}
