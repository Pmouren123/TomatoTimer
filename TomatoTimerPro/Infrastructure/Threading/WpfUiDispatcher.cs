using System.Windows.Threading;
using TomatoTimerPro.Contracts.Threading;

namespace TomatoTimerPro.Infrastructure.Threading;

public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Invoke(Action action)
    {
        if (dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }
    public Task InvokeAsync(Action action) => dispatcher.InvokeAsync(action).Task;
}
