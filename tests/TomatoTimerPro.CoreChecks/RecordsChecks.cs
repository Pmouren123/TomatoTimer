using System.IO;
using System.Windows.Input;
using TomatoTimerPro.Contracts.Interaction;
using TomatoTimerPro.Contracts.Storage;
using TomatoTimerPro.Controllers.Pages.Records;
using TomatoTimerPro.Infrastructure.Persistence;
using TomatoTimerPro.RuntimeStore.States.Settings;
using TomatoTimerPro.RuntimeStore.States.Timer;
using TomatoTimerPro.Tools.Markdown;

public static class RecordsChecks
{
    public static async Task RunAsync(string testRoot)
    {
        var inner = new LocalAppStorage(Path.Combine(testRoot, "RecordsData"));
        var storage = new ControlledStorage(inner);
        var interaction = new FakeInteraction();
        await using var records = new RecordsPageController(storage, interaction);
        var first = Path.Combine(testRoot, "RecordsFirst");
        var second = Path.Combine(testRoot, "RecordsSecond");
        var date = new DateTime(2026, 10, 29);
        var day = DateOnly.FromDateTime(date);
        var dailyPath = Path.Combine(inner.GetDayDirectory(first, day), "Daily.md");
        await records.ChangeContextAsync(first, date);
        Check(await records.SaveAsync() && !File.Exists(dailyPath), "Opening and saving an unchanged daily template creates no file");
        records.EditorText = "Unsaved local text";
        interaction.Decision = SaveDecision.Cancel;
        Check(!await records.ChangeContextAsync(second, date), "Cancel rejects context change");
        Check(records.WorkspacePath == first && records.EditorText == "Unsaved local text", "Cancelled navigation preserves context and text");
        interaction.Decision = SaveDecision.Save;
        storage.FailNextRows = true;
        var failed = false;
        try { await records.ChangeContextAsync(second, date); }
        catch (IOException) { failed = true; }
        Check(failed && records.WorkspacePath == first && records.EditorText == "Unsaved local text", "Failed target load preserves original labels and editor");
        Check(!records.IsBusy && records.CanEdit, "Failed navigation restores editability");

        var session = new TimerSession
        {
            WorkspacePath = first, Title = "Reflection selection", StartedAt = new DateTimeOffset(date.AddHours(12), DateTimeOffset.Now.Offset),
            Status = SessionStatus.Completed, PlannedSeconds = 300, FocusedSeconds = 300
        };
        await inner.SaveSessionAsync(session);
        await records.EnterAsync();
        records.SelectedSession = records.Sessions.Single(row => row.Session.Id == session.Id);
        await WaitAsync(() => !records.IsBusy && records.SelectedSession?.Session.Id == session.Id);
        Check(records.HasReflectionEditor && records.NeedsReflection && !records.IsDirty && records.EditorText == "Unsaved local text"
            && records.IsDailyMode && !records.IsReflectionMode && records.HasSelectedSession,
            "Selecting timing statistics preserves the date journal and offers optional reflection editing");
        Check(records.SelectedSession!.HasReflectionText == "", "Unwritten optional reflections are not labelled as pending work");
        Check(await records.SaveAsync() && await File.ReadAllTextAsync(dailyPath) == "Unsaved local text"
            && !File.Exists(inner.GetReflectionPath(session)), "Saving a summary never creates reflection content or a session Markdown file");

        await records.AddReflectionAsync();
        Check(records.HasReflectionEditor && !records.NeedsReflection && records.EditorText == "", "Explicit add opens an empty optional fragment editor");
        await records.EnterAsync();
        await RunCommandAsync(records.RefreshCommand);
        Check(records.HasReflectionEditor && records.EditorText == "", "Re-enter and refresh preserve explicitly opened empty editor");
        Check(await records.SaveAsync() && await File.ReadAllTextAsync(dailyPath) == "Unsaved local text", "Empty explicit reflection does not write the daily log");
        records.EditorText = "My reflection";
        await records.EnterAsync();
        Check(records.EditorText == "My reflection" && records.IsDirty, "Re-enter preserves unsaved reflection fragment");
        Check(await records.SaveAsync(), "Reflection saves successfully");
        var savedDaily = await File.ReadAllTextAsync(dailyPath);
        Check(savedDaily.Contains("Unsaved local text") && DailyReflection.Read(savedDaily, session.Id) == "My reflection"
            && !File.Exists(inner.GetReflectionPath(session)), "Explicit reflection appends to Daily.md while preserving prose and never creating session Markdown");
        Check(records.SelectedSession?.Session.Id == session.Id && records.SelectedSession.HasReflection,
            "Saving refreshes reflection state without losing selected session");
        var independentSelection = session with { Id = Guid.NewGuid().ToString("N"), Title = "Statistics only" };
        await inner.SaveSessionAsync(independentSelection);
        await records.EnterAsync();
        records.SelectedSession = records.Sessions.Single(row => row.Session.Id == independentSelection.Id);
        Check(records.IsReflectionMode && records.EditorText == "My reflection" && records.CanOpenReflection,
            "Statistics selection cannot replace an explicitly opened reflection editor");
        records.EditorText = "Stable editor identity";
        Check(await records.SaveAsync()
            && DailyReflection.Read(await File.ReadAllTextAsync(dailyPath), session.Id) == "Stable editor identity"
            && DailyReflection.Read(await File.ReadAllTextAsync(dailyPath), independentSelection.Id) is null,
            "Reflection save targets the editor session rather than whichever statistics row is selected");
        records.SelectedSession = records.Sessions.Single(row => row.Session.Id == session.Id);
        records.EditorText = "My reflection";
        Check(await records.SaveAsync(), "Restore fixture reflection for conflict checks");
        savedDaily = await File.ReadAllTextAsync(dailyPath);

        records.EditorText = "Conflicting local reflection";
        await File.WriteAllTextAsync(dailyPath, savedDaily + "\nExternal daily note\n");
        Check(!await records.SaveAsync(), "Conflicting external daily edit is not overwritten");
        Check(records.EditorText == "Conflicting local reflection", "Conflict preserves unsaved fragment editor content");
        await records.EnterAsync();
        Check(records.RecoveryDrafts.Count > 0, "Persisted recovery is offered in records UI");
        Check(records.RecoveryDrafts[0].OriginalPath == dailyPath && records.RecoveryDrafts[0].Content.Contains("Unsaved local text")
            && DailyReflection.Read(records.RecoveryDrafts[0].Content, session.Id) == "Conflicting local reflection",
            "Reflection recovery stores the complete composed daily target rather than a detached fragment");
        interaction.Decision = SaveDecision.Discard;
        await RunCommandAsync(records.RestoreDraftCommand);
        Check(records.EditorText.Contains("Conflicting local reflection") && records.IsDirty, "Daily recovery restores into editable unsaved document");
        Check(await records.SaveAsync(), "User can save a deliberately restored daily draft");
        Check((await inner.LoadRecoveryDraftsAsync()).Count == 0, "Successfully saved restoration resolves its draft");

        var copy = Path.Combine(testRoot, "ReflectionCopy.md");
        interaction.SavePath = copy;
        records.EditorText = "Copy without changing original";
        await RunCommandAsync(records.SaveAsCommand);
        Check(await File.ReadAllTextAsync(copy) == "Copy without changing original" && records.IsDirty,
            "Save as writes a copy without silently marking original edits saved");
        interaction.Decision = SaveDecision.Discard;
        await records.OpenReflectionAsync(session);
        Check(records.EditorText == "Conflicting local reflection", "Opening an existing daily reflection edits only its own fragment");

        var other = session with { Id = Guid.NewGuid().ToString("N"), Title = "Other reflection" };
        await inner.SaveSessionAsync(other);
        await records.OpenReflectionAsync(other);
        records.EditorText = "Other session reflection";
        Check(await records.SaveAsync(), "Another session can append an independent daily reflection");
        await inner.DeleteSessionAsync(session);
        await records.ForgetDeletedSessionAsync(session.Id);
        var afterDelete = await File.ReadAllTextAsync(dailyPath);
        Check(afterDelete.Contains("Unsaved local text") && DailyReflection.Read(afterDelete, session.Id) is null
            && DailyReflection.Read(afterDelete, other.Id) == "Other session reflection",
            "Deleting an appended reflection preserves daily prose and another session reflection");

        var legacy = session with { Id = Guid.NewGuid().ToString("N"), Title = "Legacy reflection" };
        await inner.SaveSessionAsync(legacy);
        var legacyPath = inner.GetReflectionPath(legacy);
        await inner.SaveDocumentAsync(await inner.ReadDocumentAsync(legacyPath), "Existing legacy reflection");
        await records.OpenReflectionAsync(legacy);
        Check(records.EditorText == "Existing legacy reflection", "Existing session Markdown remains readable without migration");
        records.EditorText = "Updated legacy reflection";
        Check(await records.SaveAsync() && await File.ReadAllTextAsync(legacyPath) == "Updated legacy reflection",
            "Existing legacy reflection updates in place without overwriting daily content");
        Check(await File.ReadAllTextAsync(dailyPath) == afterDelete, "Legacy editing never migrates or overwrites the daily log");
        await RunCommandAsync(records.EditDailyCommand);
        Check(records.IsDailyMode && records.HasSelectedSession && !records.IsReflectionMode,
            "Return to date journal preserves the statistics selection");
        records.EditorText += "\nUnsaved date-only journal prose\n";
        var dailyDraft = records.EditorText;
        var prompts = interaction.UnsavedPrompts;
        records.SelectedSession = records.Sessions.Single(row => row.Session.Id == other.Id);
        Check(records.EditorText == dailyDraft && records.IsDirty && interaction.UnsavedPrompts == prompts
            && records.IsDailyMode, "Selecting statistics never prompts or replaces an unsaved date journal");
        await records.DeleteSelectedRecordAsync();
        Check(records.IsDailyMode && records.EditorText.Contains("Unsaved date-only journal prose") && records.IsDirty
            && DailyReflection.Read(records.EditorText, other.Id) is null,
            "Deleting selected timing statistics preserves unsaved date prose while removing only the associated fragment");
        Check(await records.SaveAsync() && (await File.ReadAllTextAsync(dailyPath)).Contains("Unsaved date-only journal prose")
            && DailyReflection.Read(await File.ReadAllTextAsync(dailyPath), other.Id) is null,
            "Saving a journal after timing deletion preserves prose without resurrecting the deleted reflection");

        storage.BlockNextDocument = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var change = records.ChangeContextAsync(second, date);
        await storage.DocumentReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(records.IsBusy && !records.CanEdit, "Entire asynchronous context load disables editing");
        Check(records.WorkspacePath == first, "Workspace label is unchanged until target is fully loaded");
        storage.BlockNextDocument.SetResult();
        await change;
        Check(records.WorkspacePath == second && !records.IsBusy, "Loaded context commits atomically and restores editing");

        var removed = session with { Id = Guid.NewGuid().ToString("N"), WorkspacePath = second, Title = "Delete cached reflection" };
        await inner.SaveSessionAsync(removed);
        await records.OpenReflectionAsync(removed);
        var secondDaily = Path.Combine(inner.GetDayDirectory(second, day), "Daily.md");
        Check(await records.SaveAsync() && !File.Exists(secondDaily) && !File.Exists(inner.GetReflectionPath(removed)),
            "Explicit add followed by empty save creates neither Daily.md nor standalone session reflection");
        records.EditorText = "Do not recreate this deleted reflection";
        await inner.DeleteSessionAsync(removed);
        await records.ForgetDeletedSessionAsync(removed.Id);
        Check(records.SelectedSession is null && !records.IsDirty && !records.EditorText.Contains("Do not recreate"),
            "Confirmed deletion removes cached unsaved fragment without trapping navigation");
        Check(await records.SaveAsync() && !File.Exists(inner.GetReflectionPath(removed)) && !File.Exists(secondDaily),
            "Saving after deletion cannot recreate a reflection or write an unchanged daily template");
    }

    private static async Task WaitAsync(Func<bool> completed)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!completed()) await Task.Delay(10, timeout.Token);
    }
    private static async Task RunCommandAsync(ICommand command)
    {
        command.Execute(null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!command.CanExecute(null)) await Task.Delay(10, timeout.Token);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeInteraction : IUserInteraction
    {
        public SaveDecision Decision { get; set; } = SaveDecision.Save;
        public string? SavePath { get; set; }
        public int UnsavedPrompts { get; private set; }
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
        public Task<string[]> PickAudioFilesAsync() => Task.FromResult(Array.Empty<string>());
        public Task<string?> PickSaveFileAsync(string suggestedName) => Task.FromResult(SavePath);
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
        public Task<SaveDecision> ResolveUnsavedAsync(string title, string message)
        { UnsavedPrompts++; return Task.FromResult(Decision); }
        public void ShowMessage(string title, string message) { }
        public void OpenFolder(string path) { }
    }

    private sealed class ControlledStorage(IAppStorage inner) : IAppStorage
    {
        public bool FailNextRows { get; set; }
        public TaskCompletionSource? BlockNextDocument { get; set; }
        public TaskCompletionSource DocumentReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string AppDataDirectory => inner.AppDataDirectory;
        public Task<AppPreferences> LoadPreferencesAsync() => inner.LoadPreferencesAsync();
        public Task SavePreferencesAsync(AppPreferences preferences) => inner.SavePreferencesAsync(preferences);
        public string GetDayDirectory(string workspace, DateOnly day) => inner.GetDayDirectory(workspace, day);
        public Task<string> PrepareDayAsync(string workspace, DateOnly day) => inner.PrepareDayAsync(workspace, day);
        public string GetSessionMetadataPath(TimerSession session) => inner.GetSessionMetadataPath(session);
        public string GetReflectionPath(TimerSession session) => inner.GetReflectionPath(session);
        public Task SaveSessionAsync(TimerSession session) => inner.SaveSessionAsync(session);
        public Task DeleteSessionAsync(TimerSession session) => inner.DeleteSessionAsync(session);
        public Task<IReadOnlyList<TimerSession>> LoadSessionsAsync(string workspace, DateOnly day)
        {
            if (FailNextRows) { FailNextRows = false; throw new IOException("Simulated target-read failure"); }
            return inner.LoadSessionsAsync(workspace, day);
        }
        public async Task<DocumentSnapshot> ReadDocumentAsync(string path, string defaultText = "")
        {
            if (BlockNextDocument is not null)
            {
                DocumentReadStarted.TrySetResult();
                await BlockNextDocument.Task;
                BlockNextDocument = null;
            }
            return await inner.ReadDocumentAsync(path, defaultText);
        }
        public Task SaveDocumentAsync(DocumentSnapshot original, string content, bool overwrite = false) => inner.SaveDocumentAsync(original, content, overwrite);
        public Task SaveRecoveryAsync(string originalPath, string content) => inner.SaveRecoveryAsync(originalPath, content);
        public Task<IReadOnlyList<RecoveryDraft>> LoadRecoveryDraftsAsync() => inner.LoadRecoveryDraftsAsync();
        public Task DeleteRecoveryAsync(string id) => inner.DeleteRecoveryAsync(id);
        public Task<TimerSession?> LoadActiveSessionAsync() => inner.LoadActiveSessionAsync();
        public Task SaveActiveSessionAsync(TimerSession? session) => inner.SaveActiveSessionAsync(session);
    }
}

