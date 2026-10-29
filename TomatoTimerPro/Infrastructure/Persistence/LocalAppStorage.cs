using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TomatoTimerPro.Contracts.Storage;
using TomatoTimerPro.RuntimeStore.States.Settings;
using TomatoTimerPro.RuntimeStore.States.Timer;
using TomatoTimerPro.RuntimeStore.States.Planning;

namespace TomatoTimerPro.Infrastructure.Persistence;

public sealed class LocalAppStorage : IAppStorage, IPlanStorage, IAppDataLocation
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] RelocatableFiles =
        ["preferences.json", "plans.json", "active-session.json", "music-library.json", "default-alarm.wav"];
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly string? _locationMarkerDirectory;
    public string AppDataDirectory { get; private set; }

    public LocalAppStorage(string? appDataDirectory = null)
    {
        if (appDataDirectory is not null)
        {
            _locationMarkerDirectory = null;
            AppDataDirectory = Path.GetFullPath(appDataDirectory);
            return;
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var current = Path.Combine(local, "TomatoTimer");
        var legacy = Path.Combine(local, "TomatoTimerPro");
        // Keep existing settings, music indexes and recovery drafts usable after
        // the product rename; new installations use the TomatoTimer directory.
        _locationMarkerDirectory = Path.GetFullPath(!Directory.Exists(current) && Directory.Exists(legacy) ? legacy : current);
        AppDataDirectory = ReadConfiguredDirectory(_locationMarkerDirectory);
    }

    public async Task ChangeDataDirectoryAsync(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var target = Path.GetFullPath(directory);
        await _writes.WaitAsync();
        try
        {
            var source = Path.GetFullPath(AppDataDirectory);
            if (StringComparer.OrdinalIgnoreCase.Equals(source, target)) return;
            if (IsInside(target, source))
                throw new InvalidOperationException("\u65b0\u76ee\u5f55\u4e0d\u80fd\u4f4d\u4e8e\u5f53\u524d\u6570\u636e\u76ee\u5f55\u5185\u3002");
            Directory.CreateDirectory(target);
            foreach (var file in RelocatableFiles)
            {
                var from = Path.Combine(source, file);
                if (File.Exists(from)) File.Copy(from, Path.Combine(target, file), true);
            }
            CopyDirectory(Path.Combine(source, "Recovery"), Path.Combine(target, "Recovery"));
            await WriteLocationMarkerAsync(target.Equals(_locationMarkerDirectory, StringComparison.OrdinalIgnoreCase) ? null : target);
            AppDataDirectory = target;
        }
        finally { _writes.Release(); }
    }

    private static string ReadConfiguredDirectory(string fallback)
    {
        try
        {
            var marker = Path.Combine(fallback, "storage-location.txt");
            if (File.Exists(marker))
            {
                var configured = File.ReadAllText(marker).Trim();
                if (configured.Length > 0) return Path.GetFullPath(configured);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException) { }
        return fallback;
    }

    private async Task WriteLocationMarkerAsync(string? target)
    {
        if (_locationMarkerDirectory is null) return;
        Directory.CreateDirectory(_locationMarkerDirectory);
        var marker = Path.Combine(_locationMarkerDirectory, "storage-location.txt");
        if (target is null)
        {
            if (File.Exists(marker)) File.Delete(marker);
            return;
        }
        await AtomicWriteAsync(marker, target);
    }

    private static void CopyDirectory(string source, string target)
    {
        if (!Directory.Exists(source)) return;
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
    }

    private static bool IsInside(string candidate, string parent)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return relative == "." || (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    public async Task<AppPreferences> LoadPreferencesAsync()
    {
        var path = Path.Combine(AppDataDirectory, "preferences.json");
        if (!File.Exists(path)) return new();
        var state = await ReadJsonAsync<AppPreferences>(path) ?? new AppPreferences();
        return state with
        {
            RecentWorkspaces = state.RecentWorkspaces.IsDefault ? [] : state.RecentWorkspaces,
            MusicPaths = state.MusicPaths.IsDefault ? [] : state.MusicPaths,
            MusicVolume = double.IsFinite(state.MusicVolume) ? Math.Clamp(state.MusicVolume, 0, 1) : .5,
            AlarmVolume = double.IsFinite(state.AlarmVolume) ? Math.Clamp(state.AlarmVolume, 0, 1) : .8,
            DefaultDurationMinutes = state.DefaultDurationMinutes is >= .01 and <= 1440 ? state.DefaultDurationMinutes : 25,
            ScheduleRestMinutes = double.IsFinite(state.ScheduleRestMinutes) ? Math.Clamp(state.ScheduleRestMinutes, 0, 1440) : 1,
            BackgroundColor = state.BackgroundColor is not null && state.BackgroundColor != "#F6F5F2" && System.Text.RegularExpressions.Regex.IsMatch(state.BackgroundColor, "^#(?:[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$") ? state.BackgroundColor : "#FAF5E9",
            BackgroundOpacity = double.IsFinite(state.BackgroundOpacity) ? Math.Clamp(state.BackgroundOpacity, 0, 1) : .18,
            Animation = (state.Animation ?? new AnimationSettings()).Normalize()
        };
    }

    public Task SavePreferencesAsync(AppPreferences preferences) => WriteJsonAsync(
        Path.Combine(AppDataDirectory, "preferences.json"), preferences);

    public async Task<PlanState> LoadPlansAsync()
    {
        var path = Path.Combine(AppDataDirectory, "plans.json");
        if (!File.Exists(path)) return new();
        var json = await ReadJsonAsync<JsonElement>(path);
        var state = json.Deserialize<PlanState>(JsonOptions)
            ?? throw new InvalidDataException("计划文件为空，请检查 plans.json 后重试。");
        if (state.Entries.IsDefault || state.Entries.Any(p => p is null || string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Title) || !Enum.IsDefined(p.Status)
                || !double.IsFinite(p.DurationMinutes) || p.DurationMinutes is < .01 or > 1440)
            || state.Entries.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != state.Entries.Length)
            throw new InvalidDataException("计划文件内容无效，请保留 plans.json 并检查后重试。");
        // Earlier versions saved a checkbox. Migrate it without changing the source file until the next successful save.
        if (json.TryGetProperty("Entries", out var entries))
            state = state with { Entries = state.Entries.Select((plan, index) =>
                !entries[index].TryGetProperty("Status", out _) && entries[index].TryGetProperty("IsCompleted", out var completed)
                    && completed.ValueKind == JsonValueKind.True ? plan with { Status = PlanStatus.Completed } : plan).ToImmutableArray() };
        return state;
    }

    public Task SavePlansAsync(PlanState plans) => WriteJsonAsync(Path.Combine(AppDataDirectory, "plans.json"), plans);

    public string GetDayDirectory(string workspace, DateOnly day) => Path.Combine(
        Path.GetFullPath(workspace), "Logs", day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

    public async Task<string> PrepareDayAsync(string workspace, DateOnly day)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        var directory = GetDayDirectory(workspace, day);
        await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.Combine(directory, "Sessions"));
            var probe = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        });
        return directory;
    }

    public string GetSessionMetadataPath(TimerSession session) => Path.Combine(
        GetDayDirectory(session.WorkspacePath, session.Day), "Sessions", session.FileStem + ".json");

    public string GetReflectionPath(TimerSession session) => Path.Combine(
        GetDayDirectory(session.WorkspacePath, session.Day), "Sessions", session.FileStem + ".md");

    public Task SaveSessionAsync(TimerSession session) => WriteJsonAsync(GetSessionMetadataPath(session), session);

    public async Task DeleteSessionAsync(TimerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!Guid.TryParseExact(session.Id, "N", out _))
            throw new InvalidDataException("计时记录标识无效，已拒绝删除。");
        var directory = Path.GetFullPath(Path.Combine(GetDayDirectory(session.WorkspacePath, session.Day), "Sessions"));
        var metadata = ValidateDeletionPath(GetSessionMetadataPath(session), directory, session.FileStem + ".json");
        var legacyReflection = ValidateDeletionPath(GetReflectionPath(session), directory, session.FileStem + ".md");
        await _writes.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                // Preserve the daily journal and remove only the timing metadata.
                foreach (var original in new[] { legacyReflection, metadata })
                {
                    var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original.ToUpperInvariant())));
                    var recovery = Path.Combine(AppDataDirectory, "Recovery", id + ".json");
                    if (File.Exists(recovery)) File.Delete(recovery);
                }
                if (File.Exists(legacyReflection)) File.Delete(legacyReflection);
                if (File.Exists(metadata)) File.Delete(metadata);
            });
        }
        finally { _writes.Release(); }
    }

    private static string ValidateDeletionPath(string path, string directory, string expectedName)
    {
        var fullPath = Path.GetFullPath(path);
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(fullPath), directory)
            || !StringComparer.Ordinal.Equals(Path.GetFileName(fullPath), expectedName))
            throw new InvalidDataException("记录路径超出允许的会话目录，已拒绝删除。");
        return fullPath;
    }

    public async Task<IReadOnlyList<TimerSession>> LoadSessionsAsync(string workspace, DateOnly day)
    {
        var directory = Path.Combine(GetDayDirectory(workspace, day), "Sessions");
        if (!Directory.Exists(directory)) return [];
        var files = await Task.Run(() => Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly));
        var sessions = new List<TimerSession>();
        foreach (var path in files)
        {
            var session = await ReadJsonAsync<TimerSession>(path);
            if (session is not null) sessions.Add(session);
        }
        return sessions.OrderByDescending(s => s.StartedAt).ToArray();
    }

    public Task<IReadOnlyList<JournalEntry>> LoadJournalHistoryAsync(string workspace) => Task.Run<IReadOnlyList<JournalEntry>>(() =>
    {
        if (string.IsNullOrWhiteSpace(workspace)) return Array.Empty<JournalEntry>();
        var logs = Path.Combine(Path.GetFullPath(workspace), "Logs");
        if (!Directory.Exists(logs)) return Array.Empty<JournalEntry>();
        var entries = new List<JournalEntry>();
        foreach (var directory in Directory.EnumerateDirectories(logs, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(directory);
            if (!DateOnly.TryParseExact(name, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var day)) continue;
            var path = Path.Combine(directory, "Daily.md");
            if (File.Exists(path)) entries.Add(new JournalEntry(day, path));
        }
        return entries.OrderByDescending(entry => entry.Day).ToArray();
    });

    public async Task<DocumentSnapshot> ReadDocumentAsync(string path, string defaultText = "")
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) return new(path, defaultText, null);
        var bytes = await File.ReadAllBytesAsync(path);
        return new(path, Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'), Fingerprint(bytes));
    }

    public async Task SaveDocumentAsync(DocumentSnapshot original, string content, bool overwrite = false)
    {
        await _writes.WaitAsync();
        try
        {
            if (!overwrite)
            {
                var currentFingerprint = File.Exists(original.Path)
                    ? Fingerprint(await File.ReadAllBytesAsync(original.Path)) : null;
                if (!StringComparer.Ordinal.Equals(currentFingerprint, original.Fingerprint))
                    throw new DocumentConflictException(original.Path);
            }
            await AtomicWriteAsync(original.Path, content);
        }
        finally { _writes.Release(); }
    }

    public Task SaveRecoveryAsync(string originalPath, string content)
    {
        var absolutePath = Path.GetFullPath(originalPath);
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(absolutePath.ToUpperInvariant())));
        return WriteJsonAsync(Path.Combine(AppDataDirectory, "Recovery", id + ".json"),
            new RecoveryDraft(id, absolutePath, content, DateTimeOffset.Now));
    }

    public async Task<IReadOnlyList<RecoveryDraft>> LoadRecoveryDraftsAsync()
    {
        var directory = Path.Combine(AppDataDirectory, "Recovery");
        if (!Directory.Exists(directory)) return [];
        var files = await Task.Run(() => Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly));
        var drafts = new List<RecoveryDraft>();
        foreach (var file in files)
        {
            var draft = await ReadJsonAsync<RecoveryDraft>(file);
            if (draft is not null) drafts.Add(draft);
        }
        return drafts.OrderByDescending(draft => draft.SavedAt).ToArray();
    }

    public Task DeleteRecoveryAsync(string id)
    {
        if (id.Length != 64 || id.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Invalid recovery identifier.", nameof(id));
        return Task.Run(() => File.Delete(Path.Combine(AppDataDirectory, "Recovery", id + ".json")));
    }

    public async Task<TimerSession?> LoadActiveSessionAsync()
    {
        var path = Path.Combine(AppDataDirectory, "active-session.json");
        return File.Exists(path) ? await ReadJsonAsync<TimerSession>(path) : null;
    }

    public Task SaveActiveSessionAsync(TimerSession? session) => session is not null
        ? WriteJsonAsync(Path.Combine(AppDataDirectory, "active-session.json"), session)
        : DeleteActiveAsync();

    private async Task DeleteActiveAsync()
    {
        await _writes.WaitAsync();
        try { File.Delete(Path.Combine(AppDataDirectory, "active-session.json")); }
        finally { _writes.Release(); }
    }

    private async Task<T?> ReadJsonAsync<T>(string path)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"无法读取数据文件，请保留原文件后检查：{path}", exception);
        }
    }

    private async Task WriteJsonAsync<T>(string path, T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        await _writes.WaitAsync();
        try { await AtomicWriteAsync(path, json); }
        finally { _writes.Release(); }
    }

    private static async Task AtomicWriteAsync(string path, string content)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string Fingerprint(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
