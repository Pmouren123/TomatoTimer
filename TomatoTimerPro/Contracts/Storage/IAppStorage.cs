using TomatoTimerPro.RuntimeStore.States.Settings;
using TomatoTimerPro.RuntimeStore.States.Timer;

namespace TomatoTimerPro.Contracts.Storage;

public sealed record DocumentSnapshot(string Path, string Content, string? Fingerprint);
public sealed record RecoveryDraft(string Id, string OriginalPath, string Content, DateTimeOffset SavedAt);
public sealed record JournalEntry(DateOnly Day, string Path)
{
    public string DateText => Day.ToString("yyyy年MM月dd日", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));
}
public sealed class DocumentConflictException(string path) : IOException($"文件已被其他程序修改，请重新加载或另存为：{path}");

/// <summary>Optional capability for services whose application data root can be changed while running.</summary>
public interface IAppDataLocation
{
    Task ChangeDataDirectoryAsync(string directory);
}

public interface IAppStorage
{
    string AppDataDirectory { get; }
    Task<AppPreferences> LoadPreferencesAsync();
    Task SavePreferencesAsync(AppPreferences preferences);
    string GetDayDirectory(string workspace, DateOnly day);
    Task<string> PrepareDayAsync(string workspace, DateOnly day);
    string GetSessionMetadataPath(TimerSession session);
    string GetReflectionPath(TimerSession session);
    Task SaveSessionAsync(TimerSession session);
    Task DeleteSessionAsync(TimerSession session);
    Task<IReadOnlyList<TimerSession>> LoadSessionsAsync(string workspace, DateOnly day);
    Task<IReadOnlyList<JournalEntry>> LoadJournalHistoryAsync(string workspace)
        => Task.FromResult<IReadOnlyList<JournalEntry>>(Array.Empty<JournalEntry>());
    Task<DocumentSnapshot> ReadDocumentAsync(string path, string defaultText = "");
    Task SaveDocumentAsync(DocumentSnapshot original, string content, bool overwrite = false);
    Task SaveRecoveryAsync(string originalPath, string content);
    Task<IReadOnlyList<RecoveryDraft>> LoadRecoveryDraftsAsync();
    Task DeleteRecoveryAsync(string id);
    Task<TimerSession?> LoadActiveSessionAsync();
    Task SaveActiveSessionAsync(TimerSession? session);
}
