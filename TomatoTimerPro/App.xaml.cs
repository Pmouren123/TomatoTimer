using System.Windows;
using TomatoTimerPro.Bootstrap;
using TomatoTimerPro.Infrastructure.Desktop;
using TomatoTimerPro.Infrastructure.Threading;
using TomatoTimerPro.Managers.Application;
using TomatoTimerPro.Views.Shell;

namespace TomatoTimerPro;

public partial class App : System.Windows.Application
{
    private SingleInstanceService? _singleInstance;
    private ApplicationMgr? _manager;
    private bool _reportingError;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            if (_reportingError) return;
            _reportingError = true;
            try { new WpfUserInteraction(() => MainWindow).ShowMessage("操作未完成", args.Exception.Message); }
            finally { _reportingError = false; }
        };
        try
        {
            _singleInstance = new SingleInstanceService();
            if (!_singleInstance.IsPrimary)
            {
                var activation = await _singleInstance.ActivateExistingAsync();
                if (activation.Status != ActivationStatus.Activated)
                {
                    var existingPath = activation.ExistingProcessPath ?? "无法读取路径（旧实例不支持版本识别）";
                    var existingProcess = activation.ExistingProcessId is { } process ? $"\n现有进程：{process}" : "";
                    var reason = activation.Status == ActivationStatus.VersionMismatch
                        ? "已有另一个构建版本正在运行，本次运行没有打开新版本。"
                        : activation.Message ?? "无法确认当前运行实例的构建版本。";
                    new WpfUserInteraction(() => null).ShowMessage("请先退出正在运行的旧实例",
                        reason + "\n\n请从系统托盘退出已有实例，再重新 Run。软件不会自动结束旧进程。"
                        + "\n\n已有程序：\n" + existingPath + existingProcess
                        + "\n\n本次程序：\n" + _singleInstance.ProcessPath);
                }
                _singleInstance.Dispose();
                _singleInstance = null;
                Shutdown(0);
                return;
            }
            var window = new MainWindow();
            var interaction = new WpfUserInteraction(() => window);
            var dispatcher = new WpfUiDispatcher(Dispatcher);
            var composition = new ApplicationComposition(dispatcher, interaction);
            _manager = new ApplicationMgr(this, window, composition, interaction);
            _singleInstance.ActivationRequested += () => Dispatcher.InvokeAsync(() => _manager.ShowWindow());
            _singleInstance.StartListening();
            await _manager.StartAsync();
        }
        catch (Exception error)
        {
            new WpfUserInteraction(() => MainWindow).ShowMessage("TomatoTimer 启动失败", error.ToString());
            if (_manager is not null) await _manager.DisposeAsync();
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
