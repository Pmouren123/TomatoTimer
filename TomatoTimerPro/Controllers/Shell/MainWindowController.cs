using TomatoTimerPro.Infrastructure.Binding;
using TomatoTimerPro.Infrastructure.Commands;
using TomatoTimerPro.Contracts.Navigation;
using TomatoTimerPro.Contracts.Interaction;
using TomatoTimerPro.Controllers.Pages.Timer;
using System.ComponentModel;
using TomatoTimerPro.RuntimeStore.Abstractions;
using TomatoTimerPro.RuntimeStore.States.Settings;

namespace TomatoTimerPro.Controllers.Shell;

public sealed class MainWindowController : ObservableObject, IDisposable
{
    private readonly TimerPageController _timer;
    private readonly IPageNavigator _navigator;
    private readonly IRuntimeStore? _store;
    private readonly IDisposable? _preferencesSubscription;
    private AnimationSettings _animation = new();
    private readonly object _navigationSync = new();
    private bool _navigationInFlight;
    private DateTime _lastNavigation = DateTime.MinValue;
    private object? _currentPage;
    private string _pageId = "";
    private string _workspace = "尚未选择工作区";
    private string _status = "准备好，开始今天的一次专注。";
    public object? CurrentPage { get => _currentPage; set => SetProperty(ref _currentPage, value); }
    public string CurrentPageId { get => _pageId; set => SetProperty(ref _pageId, value); }
    public string WorkspaceName { get => _workspace; set => SetProperty(ref _workspace, value); }
    public string StatusMessage { get => _status; set => SetProperty(ref _status, value); }
    public AsyncRelayCommand NavigateTimerCommand { get; }
    public AsyncRelayCommand NavigateCalendarCommand { get; }
    public AsyncRelayCommand NavigateRecordsCommand { get; }
    public AsyncRelayCommand NavigateMusicCommand { get; }
    public AsyncRelayCommand NavigateSettingsCommand { get; }
    public AsyncRelayCommand SelectWorkspaceCommand { get; }
    public AnimationSettings Animation => _animation;
    public bool HasActiveTask => _timer.IsActive;
    public string TaskSummary => $"{_timer.CurrentTaskTitle} · {_timer.StatusText} · {_timer.RemainingText}";
    public AsyncRelayCommand EndTaskCommand => _timer.EndTaskCommand;
    public MainWindowController(IPageNavigator navigator, TimerPageController timer, IUserInteraction? interaction = null, IRuntimeStore? store = null)
    {
        _timer = timer; _navigator = navigator; _store = store;
        if (store is not null) _animation = store.Get<AppPreferences>().Animation.Normalize();
        _preferencesSubscription = store?.Subscribe<AppPreferences>(preferences =>
        {
            var next = preferences.Animation.Normalize();
            if (_animation == next) return;
            _animation = next;
            OnPropertyChanged(nameof(Animation));
        });
        timer.PropertyChanged += OnTaskChanged;
        void Error(Exception error) { StatusMessage = error.Message; interaction?.ShowMessage("页面操作未完成", error.Message); }
        NavigateTimerCommand = new AsyncRelayCommand(async () => { await NavigateFromTabAsync("timer"); }, Error);
        NavigateCalendarCommand = new AsyncRelayCommand(async () => { await NavigateFromTabAsync("calendar"); }, Error);
        NavigateRecordsCommand = new AsyncRelayCommand(async () => { await NavigateFromTabAsync("records"); }, Error);
        NavigateMusicCommand = new AsyncRelayCommand(async () => { await NavigateFromTabAsync("music"); }, Error);
        NavigateSettingsCommand = new AsyncRelayCommand(async () => { await NavigateFromTabAsync("settings"); }, Error);
        SelectWorkspaceCommand = new AsyncRelayCommand(timer.SelectWorkspaceAsync, Error);
    }
    private async Task NavigateFromTabAsync(string pageId)
    {
        lock (_navigationSync)
        {
            var cooldown = Animation.NavigationCooldownMilliseconds;
            if (_navigationInFlight || DateTime.UtcNow - _lastNavigation < TimeSpan.FromMilliseconds(cooldown)) return;
            _navigationInFlight = true;
        }
        try
        {
            if (await _navigator.NavigateAsync(pageId))
                lock (_navigationSync) _lastNavigation = DateTime.UtcNow;
        }
        finally { lock (_navigationSync) _navigationInFlight = false; }
    }
    private void OnTaskChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(TimerPageController.IsActive) or nameof(TimerPageController.StatusText) or nameof(TimerPageController.RemainingText) or nameof(TimerPageController.CurrentTaskTitle))
        { OnPropertyChanged(nameof(HasActiveTask)); OnPropertyChanged(nameof(TaskSummary)); }
    }
    public void Dispose()
    {
        _timer.PropertyChanged -= OnTaskChanged;
        _preferencesSubscription?.Dispose();
    }
}
