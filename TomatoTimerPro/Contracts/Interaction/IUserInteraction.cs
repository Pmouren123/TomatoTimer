using TomatoTimerPro.RuntimeStore.States.Planning;

namespace TomatoTimerPro.Contracts.Interaction;

public enum SaveDecision { Save, Discard, Cancel }

public interface IUserInteraction
{
    Task<string?> PickFolderAsync();
    Task<string?> PickFolderAsync(string title) => PickFolderAsync();
    Task<string[]> PickAudioFilesAsync();
    Task<string?> PickImageFileAsync() => Task.FromResult<string?>(null);
    Task<string?> PickSaveFileAsync(string suggestedName);
    Task<bool> ConfirmAsync(string title, string message);
    Task<SaveDecision> ResolveUnsavedAsync(string title, string message);
    Task<SaveDecision> ChooseTaskEndAsync(string taskTitle) => Task.FromResult(SaveDecision.Cancel);
    Task EditPlansAsync(DateOnly day, IReadOnlyList<PlanEntry> plans, bool addNew,
        Func<IReadOnlyList<PlanEntry>, Task<string?>> save) => throw new NotSupportedException("当前交互环境不支持计划编辑面板。");
    void ShowMessage(string title, string message);
    void OpenFolder(string path);
}
