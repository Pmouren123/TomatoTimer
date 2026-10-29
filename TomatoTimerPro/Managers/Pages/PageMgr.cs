using TomatoTimerPro.Contracts.Navigation;

namespace TomatoTimerPro.Managers.Pages;

public sealed class PageMgr : IPageNavigator, IAsyncDisposable
{
    private sealed record Registration(Func<PageScope> Factory, bool KeepAlive);
    private readonly Dictionary<string, Registration> _registrations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PageScope> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    public string CurrentId { get; private set; } = "";
    public PageScope? Current { get; private set; }
    public event Action? Changed;

    public void Register(string id, Func<PageScope> factory, bool keepAlive = false) =>
        _registrations.Add(id, new Registration(factory, keepAlive));

    public async Task<bool> NavigateAsync(string pageId)
    {
        // Respond immediately to the first click. While a transition or leave
        // confirmation is running, ignore additional clicks instead of queueing
        // them and making the page feel delayed.
        if (!await _gate.WaitAsync(0)) return false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (pageId == CurrentId) return true;
            if (!_registrations.TryGetValue(pageId, out var registration)) throw new ArgumentException("页面未注册", nameof(pageId));
            if (Current is not null && !await Current.Lifecycle.CanLeaveAsync(CancellationToken.None)) return false;
            var created = !_cache.TryGetValue(pageId, out var target);
            target ??= registration.Factory();
            try
            {
                if (!target.Initialized)
                {
                    await target.Lifecycle.InitializeAsync(CancellationToken.None);
                    target.Initialized = true;
                }
                await target.Lifecycle.EnterAsync(CancellationToken.None);
            }
            catch
            {
                if (created) await target.DisposeAsync();
                throw;
            }
            var previous = Current;
            var previousId = CurrentId;
            if (previous is not null) await previous.Lifecycle.LeaveAsync(CancellationToken.None);
            Current = target;
            CurrentId = pageId;
            if (registration.KeepAlive) _cache[pageId] = target;
            if (previous is not null && !_cache.ContainsKey(previousId)) await previous.DisposeAsync();
            Changed?.Invoke();
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> CanCloseAsync()
    {
        await _gate.WaitAsync();
        try { return Current is null || await Current.Lifecycle.CanLeaveAsync(CancellationToken.None); }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (Current is not null) await Current.Lifecycle.LeaveAsync(CancellationToken.None);
            var scopes = _cache.Values.ToHashSet();
            if (Current is not null) scopes.Add(Current);
            foreach (var scope in scopes) await scope.DisposeAsync();
            _cache.Clear();
            Current = null;
        }
        finally { _gate.Release(); }
    }
}
