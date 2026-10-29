namespace TomatoTimerPro.RuntimeStore.Abstractions;

public interface IRuntimeStore
{
    T Get<T>() where T : class;
    void Set<T>(T state) where T : class;
    T Update<T>(Func<T, T> update) where T : class;
    IDisposable Subscribe<T>(Action<T> handler) where T : class;
}
