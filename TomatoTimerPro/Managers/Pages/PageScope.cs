using TomatoTimerPro.Contracts.Lifecycle;

namespace TomatoTimerPro.Managers.Pages;

public sealed class PageScope(object view, IPageLifecycle lifecycle) : IAsyncDisposable
{
    public object View { get; } = view;
    public IPageLifecycle Lifecycle { get; } = lifecycle;
    public bool Initialized { get; set; }
    public ValueTask DisposeAsync() => Lifecycle.DisposeAsync();
}

public sealed class PageLifecycle(
    Func<Task>? initialize = null, Func<Task>? enter = null, Func<Task<bool>>? canLeave = null,
    Func<Task>? leave = null, Func<ValueTask>? dispose = null) : IPageLifecycle
{
    public Task InitializeAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return initialize?.Invoke() ?? Task.CompletedTask; }
    public Task EnterAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return enter?.Invoke() ?? Task.CompletedTask; }
    public Task<bool> CanLeaveAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return canLeave?.Invoke() ?? Task.FromResult(true); }
    public Task LeaveAsync(CancellationToken token) => leave?.Invoke() ?? Task.CompletedTask;
    public ValueTask DisposeAsync() => dispose?.Invoke() ?? ValueTask.CompletedTask;
}
