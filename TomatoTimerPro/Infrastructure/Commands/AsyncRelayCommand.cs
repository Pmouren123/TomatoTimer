using System.Windows.Input;

namespace TomatoTimerPro.Infrastructure.Commands;

public sealed class AsyncRelayCommand(Func<Task> execute, Action<Exception>? onError = null,
    Func<bool>? canExecute = null) : ICommand
{
    private bool _running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true;
        NotifyCanExecuteChanged();
        try { await execute(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { onError?.Invoke(ex); }
        finally { _running = false; NotifyCanExecuteChanged(); }
    }
    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
