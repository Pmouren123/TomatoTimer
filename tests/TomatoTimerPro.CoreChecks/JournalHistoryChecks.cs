using System.IO;
using System.Windows.Input;
using TomatoTimerPro.Contracts.Interaction;
using TomatoTimerPro.Contracts.Storage;
using TomatoTimerPro.Controllers.Pages.Records;
using TomatoTimerPro.Infrastructure.Persistence;
using TomatoTimerPro.RuntimeStore.States.Timer;

public static class JournalHistoryChecks
{
    public static async Task RunAsync(string testRoot)
    {
        var root = Path.Combine(testRoot, "JournalHistory");
        var workspace = Path.Combine(root, "Workspace");
        var absent = Path.Combine(root, "Absent");
        var storage = new LocalAppStorage(Path.Combine(root, "AppData"));
        Check((await storage.LoadJournalHistoryAsync(absent)).Count == 0 && !Directory.Exists(absent),
            "History lookup of a missing workspace creates no directory");
        var oldDay = new DateOnly(2026, 10, 26);
        var newDay = oldDay.AddDays(2);
        var oldPath = Path.Combine(storage.GetDayDirectory(workspace, oldDay), "Daily.md");
        var newPath = Path.Combine(storage.GetDayDirectory(workspace, newDay), "Daily.md");
        await storage.SaveDocumentAsync(await storage.ReadDocumentAsync(oldPath), "Old independent date journal");
        await storage.SaveDocumentAsync(await storage.ReadDocumentAsync(newPath), "New independent date journal");
        await storage.PrepareDayAsync(workspace, oldDay.AddDays(1));
        await storage.SaveSessionAsync(new TimerSession
        {
            WorkspacePath = workspace, Title = "Timing without journal",
            StartedAt = new DateTimeOffset(oldDay.AddDays(3).ToDateTime(TimeOnly.MinValue), DateTimeOffset.Now.Offset),
            Status = SessionStatus.Completed, FocusedSeconds = 10, PlannedSeconds = 10
        });
        var invalid = Path.Combine(workspace, "Logs", "not-a-date", "Daily.md");
        await storage.SaveDocumentAsync(await storage.ReadDocumentAsync(invalid), "Ignore malformed folder");
        var nested = Path.Combine(workspace, "Logs", "archive", "2020-01-01", "Daily.md");
        await storage.SaveDocumentAsync(await storage.ReadDocumentAsync(nested), "Ignore recursive journal");
        var history = await storage.LoadJournalHistoryAsync(workspace);
        Check(history.Select(item => item.Day).SequenceEqual(new[] { newDay, oldDay })
            && history[0].Path == newPath && history.All(item => item.DateText.Contains(item.Day.Year.ToString())),
            "History lists only direct valid date folders containing Daily.md, newest first");

        var interaction = new Interaction();
        await using var records = new RecordsPageController(storage, interaction);
        await records.ChangeContextAsync(workspace, newDay.ToDateTime(TimeOnly.MinValue));
        Check(records.History.Count == 2 && records.SelectedHistory?.Day == newDay && records.Sessions.Count == 0
            && records.IsDailyMode && records.EditorText == "New independent date journal",
            "Date journals load and select in history without any timing record");
        records.EditorText = "Unsaved new-day prose";
        interaction.Decision = SaveDecision.Cancel;
        records.SelectedHistory = records.History.Single(item => item.Day == oldDay);
        await WaitAsync(() => !records.IsBusy);
        Check(records.SelectedHistory?.Day == newDay && records.SelectedDate == newDay.ToDateTime(TimeOnly.MinValue)
            && records.EditorText == "Unsaved new-day prose" && records.IsDirty,
            "Cancelling history navigation restores selection and preserves dirty date prose");
        interaction.Decision = SaveDecision.Discard;
        records.SelectedHistory = records.History.Single(item => item.Day == oldDay);
        await WaitAsync(() => !records.IsBusy);
        Check(records.SelectedHistory?.Day == oldDay && records.EditorText == "Old independent date journal"
            && records.IsDailyMode && !records.HasSelectedSession, "History selection explicitly opens the date journal");
        await RunCommandAsync(records.ShowSessionsCommand);
        Check(!records.HistoryVisible && records.SessionsVisible && records.EditorText == "Old independent date journal",
            "History/session tabs change only the sidebar");
        await RunCommandAsync(records.ShowHistoryCommand);
        Check(records.HistoryVisible && !records.SessionsVisible, "History tab is available independently of sessions");

        var freshDay = newDay.AddDays(4);
        await records.ChangeContextAsync(workspace, freshDay.ToDateTime(TimeOnly.MinValue));
        var saves = 0;
        records.JournalSaved += () => saves++;
        Check(await records.SaveAsync() && saves == 0 && records.History.Count == 2 && records.SelectedHistory is null,
            "An unchanged template does not create a history entry or emit a save event");
        records.EditorText = "Journal without a timer or plan";
        Check(await records.SaveAsync() && saves == 1 && records.History.First().Day == freshDay
            && records.SelectedHistory?.Day == freshDay && records.Sessions.Count == 0,
            "Saving date-only prose refreshes history and emits JournalSaved");
        var externalDay = freshDay.AddDays(1);
        var externalPath = Path.Combine(storage.GetDayDirectory(workspace, externalDay), "Daily.md");
        await storage.SaveDocumentAsync(await storage.ReadDocumentAsync(externalPath), "Written externally");
        await records.RefreshJournalHistoryAsync();
        Check(records.History.First().Day == externalDay && records.SelectedHistory?.Day == freshDay,
            "Public history refresh discovers external journals without changing the editor");
        File.Delete(externalPath);
        await records.EnterAsync();
        Check(records.History.All(item => item.Day != externalDay), "Entering records refreshes removed history files");
        var anotherDay = externalDay.AddDays(1);
        var anotherPath = Path.Combine(storage.GetDayDirectory(workspace, anotherDay), "Daily.md");
        await storage.SaveDocumentAsync(await storage.ReadDocumentAsync(anotherPath), "External refresh fixture");
        await RunCommandAsync(records.RefreshCommand);
        Check(records.History.First().Day == anotherDay, "Refresh command updates journal history");
        await records.ChangeContextAsync(absent, freshDay.ToDateTime(TimeOnly.MinValue));
        Check(records.History.Count == 0 && records.SelectedHistory is null, "Workspace changes replace history without stale entries");
    }

    private static async Task RunCommandAsync(ICommand command)
    { command.Execute(null); await WaitAsync(() => command.CanExecute(null)); }
    private static async Task WaitAsync(Func<bool> completed)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!completed()) await Task.Delay(10, timeout.Token);
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Interaction : IUserInteraction
    {
        public SaveDecision Decision { get; set; } = SaveDecision.Save;
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
        public Task<string[]> PickAudioFilesAsync() => Task.FromResult(Array.Empty<string>());
        public Task<string?> PickSaveFileAsync(string suggestedName) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
        public Task<SaveDecision> ResolveUnsavedAsync(string title, string message) => Task.FromResult(Decision);
        public void ShowMessage(string title, string message) => throw new InvalidOperationException(message);
        public void OpenFolder(string path) => throw new InvalidOperationException(path);
    }
}
