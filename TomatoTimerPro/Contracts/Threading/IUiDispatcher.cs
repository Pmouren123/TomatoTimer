namespace TomatoTimerPro.Contracts.Threading;

public interface IUiDispatcher
{
    void Invoke(Action action);
    Task InvokeAsync(Action action);
}
