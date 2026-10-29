using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using TomatoTimerPro.Contracts.Interaction;
using TomatoTimerPro.Controllers.Components.Dialogs;
using TomatoTimerPro.Controllers.Components.Planning;
using TomatoTimerPro.RuntimeStore.States.Planning;
using TomatoTimerPro.Views.Dialogs;

namespace TomatoTimerPro.Infrastructure.Desktop;

public sealed class WpfUserInteraction(Func<Window?> owner) : IUserInteraction
{
    public Task<string?> PickFolderAsync()
    {
        var dialog = new OpenFolderDialog { Title = "选择或新建工作区文件夹", Multiselect = false };
        return Task.FromResult(dialog.ShowDialog(owner()) == true ? dialog.FolderName : null);
    }
    public Task<string?> PickFolderAsync(string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        return Task.FromResult(dialog.ShowDialog(owner()) == true ? dialog.FolderName : null);
    }
    public Task<string[]> PickAudioFilesAsync()
    {
        var dialog = new OpenFileDialog { Title = "导入本地音乐", Multiselect = true, Filter = "音频文件|*.mp3;*.wav;*.wma;*.m4a;*.aac|所有文件|*.*" };
        return Task.FromResult(dialog.ShowDialog(owner()) == true ? dialog.FileNames : []);
    }
    public Task<string?> PickImageFileAsync()
    {
        var dialog = new OpenFileDialog { Title = "选择背景图片", Multiselect = false, Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp|所有文件|*.*" };
        return Task.FromResult(dialog.ShowDialog(owner()) == true ? dialog.FileName : null);
    }
    public Task<string?> PickSaveFileAsync(string suggestedName)
    {
        var dialog = new SaveFileDialog { Title = "另存为", FileName = suggestedName, Filter = "Markdown 日志|*.md|所有文件|*.*", DefaultExt = ".md" };
        return Task.FromResult(dialog.ShowDialog(owner()) == true ? dialog.FileName : null);
    }
    public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(
        ShowPrompt(title, message, SketchDialogMode.Confirm) == DialogChoice.Primary);
    public Task<SaveDecision> ResolveUnsavedAsync(string title, string message) => Task.FromResult(
        ShowPrompt(title, message, SketchDialogMode.SaveChanges) switch
        { DialogChoice.Primary => SaveDecision.Save, DialogChoice.Secondary => SaveDecision.Discard, _ => SaveDecision.Cancel });
    public Task<SaveDecision> ChooseTaskEndAsync(string taskTitle) => Task.FromResult(
        ShowPrompt("结束当前任务", $"当前任务：{taskTitle}\n\n提前结束会保留实际专注时间。\n放弃并删除会停止当前任务，并在再次确认后删除本次记录；当日日志和其他记录保留。", SketchDialogMode.EndTask) switch
        { DialogChoice.Primary => SaveDecision.Save, DialogChoice.Secondary => SaveDecision.Discard, _ => SaveDecision.Cancel });
    public void ShowMessage(string title, string message) => ShowPrompt(title, message, SketchDialogMode.Information);

    public Task EditPlansAsync(DateOnly day, IReadOnlyList<PlanEntry> plans, bool addNew,
        Func<IReadOnlyList<PlanEntry>, Task<string?>> save)
    {
        var currentOwner = owner();
        var dispatcher = currentOwner?.Dispatcher ?? System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            return dispatcher.InvokeAsync(() => EditPlansAsync(day, plans, addNew, save)).Task.Unwrap();
        using var controller = new PlanEditorController(day, plans, addNew, this, save);
        var dialog = new PlanEditorWindow { DataContext = controller };
        if (currentOwner is { IsVisible: true }) dialog.Owner = currentOwner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        controller.CloseRequested += dialog.CloseApproved;
        try { dialog.ShowDialog(); }
        finally { controller.CloseRequested -= dialog.CloseApproved; }
        return Task.CompletedTask;
    }

    private DialogChoice ShowPrompt(string title, string message, SketchDialogMode mode)
    {
        var currentOwner = owner();
        var dispatcher = currentOwner?.Dispatcher ?? System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            return dispatcher.Invoke(() => ShowPrompt(title, message, mode));
        currentOwner = System.Windows.Application.Current?.Windows.OfType<Window>()
            .LastOrDefault(window => window is PlanEditorWindow && window.IsVisible) ?? currentOwner;
        var controller = new SketchDialogController(title, message, mode);
        var dialog = new SketchDialogWindow { DataContext = controller };
        if (currentOwner is { IsVisible: true }) dialog.Owner = currentOwner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var closing = false;
        void Close(DialogChoice choice)
        {
            if (closing) return;
            closing = true;
            dialog.Result = choice;
            dialog.DialogResult = choice == DialogChoice.Primary;
        }
        controller.CloseRequested += Close;
        try { dialog.ShowDialog(); return dialog.Result; }
        finally { controller.CloseRequested -= Close; }
    }
    public void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("目录不存在，请先选择工作区或创建日志。");
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
