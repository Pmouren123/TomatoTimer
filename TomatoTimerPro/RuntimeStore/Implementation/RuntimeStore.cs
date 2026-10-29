using TomatoTimerPro.RuntimeStore.Abstractions;

namespace TomatoTimerPro.RuntimeStore.Implementation;

/// <summary>Typed, process-local state. Registered values must be immutable snapshots.</summary>
public sealed class RuntimeStore : IRuntimeStore, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Type, object> _states = [];
    private readonly Dictionary<Type, List<Delegate>> _subscriptions = [];
    private bool _disposed;

    public T Get<T>() where T : class
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _states.TryGetValue(typeof(T), out var state)
                ? (T)state
                : throw new KeyNotFoundException($"State {typeof(T).Name} has not been registered.");
        }
    }

    public void Set<T>(T state) where T : class
    {
        ArgumentNullException.ThrowIfNull(state);
        Action<T>[] handlers;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _states[typeof(T)] = state;
            handlers = GetHandlers<T>();
        }
        Notify(handlers, state);
    }

    public T Update<T>(Func<T, T> update) where T : class
    {
        ArgumentNullException.ThrowIfNull(update);
        T next;
        Action<T>[] handlers;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_states.TryGetValue(typeof(T), out var current))
                throw new KeyNotFoundException($"State {typeof(T).Name} has not been registered.");
            next = update((T)current) ?? throw new InvalidOperationException("A state update cannot return null.");
            _states[typeof(T)] = next;
            handlers = GetHandlers<T>();
        }
        Notify(handlers, next);
        return next;
    }

    public IDisposable Subscribe<T>(Action<T> handler) where T : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_subscriptions.TryGetValue(typeof(T), out var handlers))
                _subscriptions[typeof(T)] = handlers = [];
            handlers.Add(handler);
        }
        return new Subscription(() =>
        {
            lock (_gate)
                if (_subscriptions.TryGetValue(typeof(T), out var handlers)) handlers.Remove(handler);
        });
    }

    private Action<T>[] GetHandlers<T>() => _subscriptions.TryGetValue(typeof(T), out var handlers)
        ? handlers.Cast<Action<T>>().ToArray() : [];

    private static void Notify<T>(IEnumerable<Action<T>> handlers, T state)
    {
        List<Exception>? errors = null;
        foreach (var handler in handlers)
        {
            try { handler(state); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        if (errors is not null) throw new AggregateException("State was committed, but a subscriber failed.", errors);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _subscriptions.Clear();
            _states.Clear();
        }
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;
        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
