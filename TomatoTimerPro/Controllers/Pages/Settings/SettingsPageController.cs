using TomatoTimerPro.Contracts.Audio;
using TomatoTimerPro.Contracts.Interaction;
using TomatoTimerPro.Contracts.Storage;
using TomatoTimerPro.Controllers.Components.Countdown;
using TomatoTimerPro.Infrastructure.Binding;
using TomatoTimerPro.Infrastructure.Commands;
using TomatoTimerPro.RuntimeStore.Abstractions;
using TomatoTimerPro.RuntimeStore.States.Settings;
using System.Text.RegularExpressions;

namespace TomatoTimerPro.Controllers.Pages.Settings;

public sealed class SettingsPageController : ObservableObject, IDisposable
{
    private readonly IRuntimeStore _store;
    private readonly IAppStorage _storage;
    private readonly IMusicService _music;
    private readonly IDisposable _subscription;
    private string _status = "";
    private string _durationStatus = "";
    private double _defaultDurationMinutes;
    public event Action? PreferencesChanged;
    public string DataDirectory { get; private set; }
    public string StatusMessage { get => _status; private set => SetProperty(ref _status, value); }
    public DurationInputController DefaultDuration { get; } = new();
    public string DurationStatus { get => _durationStatus; private set => SetProperty(ref _durationStatus, value); }
    public RelayCommand ApplyDefaultDurationCommand { get; }
    public bool CloseToTray { get => _store.Get<AppPreferences>().CloseToTray; set { _store.Update<AppPreferences>(p => p with { CloseToTray = value }); PreferencesChanged?.Invoke(); } }
    public double MusicVolume { get => _music.MusicVolume; set { _music.MusicVolume = value; OnPropertyChanged(); } }
    public double AlarmVolume { get => _music.AlarmVolume; set { _music.AlarmVolume = value; OnPropertyChanged(); } }
    public bool ResumeMusic { get => _music.ResumeMusic; set { _music.ResumeMusic = value; OnPropertyChanged(); } }
    public string BackgroundColor
    {
        get => _store.Get<AppPreferences>().BackgroundColor;
        set
        {
            var color = value?.Trim();
            if (color is null || !Regex.IsMatch(color, "^#(?:[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$"))
            {
                StatusMessage = "颜色格式有误，例如 #FAF5E9";
                return;
            }
            color = color.ToUpperInvariant();
            StatusMessage = "";
            if (StringComparer.Ordinal.Equals(_store.Get<AppPreferences>().BackgroundColor, color)) return;
            _store.Update<AppPreferences>(preferences => preferences with { BackgroundColor = color });
            PreferencesChanged?.Invoke();
        }
    }
    public string? BackgroundImagePath => _store.Get<AppPreferences>().BackgroundImagePath;
    public double BackgroundOpacity
    {
        get => _store.Get<AppPreferences>().BackgroundOpacity;
        set
        {
            if (!double.IsFinite(value)) { StatusMessage = "背景图片透明度必须是有效数值。"; return; }
            var opacity = Math.Clamp(value, 0, 1);
            if (_store.Get<AppPreferences>().BackgroundOpacity == opacity) return;
            _store.Update<AppPreferences>(preferences => preferences with { BackgroundOpacity = opacity });
            PreferencesChanged?.Invoke();
        }
    }
    public bool FloatingClockEnabled
    {
        get => _store.Get<AppPreferences>().FloatingClockEnabled;
        set
        {
            if (_store.Get<AppPreferences>().FloatingClockEnabled == value) return;
            _store.Update<AppPreferences>(preferences => preferences with { FloatingClockEnabled = value });
            PreferencesChanged?.Invoke();
            StatusMessage = "";
        }
    }
    public int PageTransitionMilliseconds { get => Animation.PageTransitionMilliseconds; set => SetAnimationTimings(value, SelectionSlideMilliseconds, SelectionFadeMilliseconds, NavigationCooldownMilliseconds); }
    public int SelectionSlideMilliseconds { get => Animation.SelectionSlideMilliseconds; set => SetAnimationTimings(PageTransitionMilliseconds, value, SelectionFadeMilliseconds, NavigationCooldownMilliseconds); }
    public int SelectionFadeMilliseconds { get => Animation.SelectionFadeMilliseconds; set => SetAnimationTimings(PageTransitionMilliseconds, SelectionSlideMilliseconds, value, NavigationCooldownMilliseconds); }
    public int NavigationCooldownMilliseconds { get => Animation.NavigationCooldownMilliseconds; set => SetAnimationTimings(PageTransitionMilliseconds, SelectionSlideMilliseconds, SelectionFadeMilliseconds, value); }
    private AnimationSettings Animation => _store.Get<AppPreferences>().Animation;

    public void SetAnimationTimings(int pageTransition, int selectionSlide, int selectionFade, int navigationCooldown)
    {
        var next = (Animation with
        {
            PageTransitionMilliseconds = pageTransition,
            SelectionSlideMilliseconds = selectionSlide,
            SelectionFadeMilliseconds = selectionFade,
            NavigationCooldownMilliseconds = navigationCooldown
        }).Normalize();
        if (next == Animation) return;
        _store.Update<AppPreferences>(preferences => preferences with { Animation = next });
        PreferencesChanged?.Invoke();
        OnPropertyChanged(nameof(PageTransitionMilliseconds));
        OnPropertyChanged(nameof(SelectionSlideMilliseconds));
        OnPropertyChanged(nameof(SelectionFadeMilliseconds));
        OnPropertyChanged(nameof(NavigationCooldownMilliseconds));
    }
    public RelayCommand OpenDataDirectoryCommand { get; }
    public AsyncRelayCommand ChooseDataDirectoryCommand { get; }
    public AsyncRelayCommand ChooseBackgroundImageCommand { get; }
    public RelayCommand ResetBackgroundCommand { get; }
    public SettingsPageController(IRuntimeStore store, IAppStorage storage, IMusicService music, IUserInteraction interaction)
    {
        _store = store; _storage = storage; _music = music; DataDirectory = storage.AppDataDirectory;
        _defaultDurationMinutes = store.Get<AppPreferences>().DefaultDurationMinutes;
        DefaultDuration.SetDuration(TimeSpan.FromMinutes(_defaultDurationMinutes));
        ApplyDefaultDurationCommand = new RelayCommand(() =>
        {
            if (!DefaultDuration.TryGetDuration(out var duration))
            { DurationStatus = DefaultDuration.ValidationMessage; return; }
            _store.Update<AppPreferences>(p => p with { DefaultDurationMinutes = duration.TotalMinutes });
            PreferencesChanged?.Invoke();
            DurationStatus = "默认专注时长已设置。";
        });
        ChooseBackgroundImageCommand = new AsyncRelayCommand(async () =>
        {
            var path = await interaction.PickImageFileAsync();
            if (string.IsNullOrWhiteSpace(path)) return;
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) { StatusMessage = "所选背景图片不存在，请重新选择。"; return; }
            _store.Update<AppPreferences>(preferences => preferences with { BackgroundImagePath = path });
            PreferencesChanged?.Invoke();
            StatusMessage = "";
        }, error => StatusMessage = "设置背景图片失败：" + error.Message);
        ResetBackgroundCommand = new RelayCommand(() =>
        {
            _store.Update<AppPreferences>(preferences => preferences with
            {
                BackgroundColor = "#FAF5E9", BackgroundImagePath = null, BackgroundOpacity = .18
            });
            PreferencesChanged?.Invoke();
            StatusMessage = "";
        });
        OpenDataDirectoryCommand = new RelayCommand(() =>
        {
            try { Directory.CreateDirectory(DataDirectory); interaction.OpenFolder(DataDirectory); }
            catch (Exception error) { StatusMessage = error.Message; }
        });
        ChooseDataDirectoryCommand = new AsyncRelayCommand(async () =>
        {
            var path = await interaction.PickFolderAsync("选择本地数据存储位置");
            if (string.IsNullOrWhiteSpace(path)) return;
            if (_storage is not IAppDataLocation movable)
            {
                StatusMessage = "\u5f53\u524d\u5b58\u50a8\u5b9e\u73b0\u4e0d\u652f\u6301\u66f4\u6362\u5b58\u50a8\u4f4d\u7f6e\u3002";
                return;
            }
            try
            {
                await movable.ChangeDataDirectoryAsync(path);
                if (_music is IAppDataLocation musicLocation)
                    await musicLocation.ChangeDataDirectoryAsync(_storage.AppDataDirectory);
                DataDirectory = _storage.AppDataDirectory;
                OnPropertyChanged(nameof(DataDirectory));
                PreferencesChanged?.Invoke();
                StatusMessage = "\u672c\u5730\u6570\u636e\u5df2\u79fb\u52a8\u5e76\u5207\u6362\u5230\u65b0\u76ee\u5f55\u3002";
            }
            catch (Exception error) { StatusMessage = "\u5207\u6362\u5b58\u50a8\u4f4d\u7f6e\u5931\u8d25\uff1a" + error.Message; }
        }, error => StatusMessage = "\u5207\u6362\u5b58\u50a8\u4f4d\u7f6e\u5931\u8d25\uff1a" + error.Message);
        _subscription = store.Subscribe<AppPreferences>(preferences =>
        {
            if (_defaultDurationMinutes != preferences.DefaultDurationMinutes)
            {
                _defaultDurationMinutes = preferences.DefaultDurationMinutes;
                DefaultDuration.SetDuration(TimeSpan.FromMinutes(_defaultDurationMinutes));
            }
            OnPropertyChanged(nameof(CloseToTray));
            OnPropertyChanged(nameof(BackgroundColor));
            OnPropertyChanged(nameof(BackgroundImagePath));
            OnPropertyChanged(nameof(BackgroundOpacity));
            OnPropertyChanged(nameof(FloatingClockEnabled));
            OnPropertyChanged(nameof(PageTransitionMilliseconds));
            OnPropertyChanged(nameof(SelectionSlideMilliseconds));
            OnPropertyChanged(nameof(SelectionFadeMilliseconds));
            OnPropertyChanged(nameof(NavigationCooldownMilliseconds));
        });
        _music.Changed += OnMusicChanged;
    }
    private void OnMusicChanged(object? sender, EventArgs e)
    { OnPropertyChanged(nameof(MusicVolume)); OnPropertyChanged(nameof(AlarmVolume)); OnPropertyChanged(nameof(ResumeMusic)); }
    public void Dispose() { _subscription.Dispose(); _music.Changed -= OnMusicChanged; }
}
