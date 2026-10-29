using TomatoTimerPro.Infrastructure.Commands;

namespace TomatoTimerPro.Controllers.Components.Dialogs;

public enum SketchDialogMode { Information, Confirm, SaveChanges, EndTask }
public enum DialogChoice { Primary, Secondary, Cancel }

/// <summary>Reusable prompt state and intentions, independent of its WPF host.</summary>
public sealed class SketchDialogController
{
    public string Title { get; }
    public string Message { get; }
    public string PrimaryText { get; }
    public string SecondaryText { get; }
    public string CancelText { get; }
    public bool HasSecondary { get; }
    public bool HasCancel { get; }
    public RelayCommand PrimaryCommand { get; }
    public RelayCommand SecondaryCommand { get; }
    public RelayCommand CancelCommand { get; }
    public event Action<DialogChoice>? CloseRequested;

    public SketchDialogController(string title, string message, SketchDialogMode mode)
    {
        Title = title;
        Message = message;
        PrimaryText = mode switch
        {
            SketchDialogMode.SaveChanges => "保存修改",
            SketchDialogMode.EndTask => "结束专注",
            SketchDialogMode.Confirm => title.StartsWith("删除", StringComparison.Ordinal) ? "删除" : "确定",
            _ => "知道了"
        };
        SecondaryText = mode switch
        {
            SketchDialogMode.SaveChanges => "放弃修改",
            SketchDialogMode.EndTask => "删除记录",
            _ => ""
        };
        CancelText = mode switch
        {
            SketchDialogMode.SaveChanges => "继续编辑",
            SketchDialogMode.EndTask => "继续专注",
            _ => "取消"
        };
        HasSecondary = mode is SketchDialogMode.SaveChanges or SketchDialogMode.EndTask;
        HasCancel = mode != SketchDialogMode.Information;
        PrimaryCommand = new RelayCommand(() => CloseRequested?.Invoke(DialogChoice.Primary));
        SecondaryCommand = new RelayCommand(() => CloseRequested?.Invoke(DialogChoice.Secondary));
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(DialogChoice.Cancel));
    }
}
