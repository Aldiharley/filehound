using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileHound.App.Services;

namespace FileHound.App.ViewModels;

public enum AppPage { Dashboard, Search, Drives, Settings }

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IndexManager _manager;
    private readonly DispatcherTimer _toastTimer;

    /// <param name="userName">What to call the user; see <see cref="UserNames.Resolve"/>.</param>
    public MainViewModel(IndexManager manager, bool isElevated, string? userName = null)
    {
        _manager = manager;
        IsElevated = isElevated;
        _userName = userName ?? UserNames.Resolve(null);
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        _toastTimer.Tick += (_, _) => { IsToastVisible = false; _toastTimer.Stop(); };
        _manager.StateChanged += (_, _) => Application.Current?.Dispatcher.InvokeAsync(UpdateStatus);
        _manager.IndexChanged += (_, _) => Application.Current?.Dispatcher.InvokeAsync(UpdateStatus);
    }

    public required DashboardViewModel Dashboard { get; init; }
    public required SearchViewModel Search { get; init; }
    public required DrivesViewModel Drives { get; init; }
    public required SettingsViewModel Settings { get; init; }

    public bool IsElevated { get; }

    /// <summary>Shown in the sidebar greeting; changes when the user picks a name in Settings.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SidebarGreeting))] private string _userName;
    public string SidebarGreeting => $"Hi, {UserName}!";

    [ObservableProperty] private AppPage _currentPage = AppPage.Dashboard;
    [ObservableProperty] private object? _currentView;
    [ObservableProperty] private string _pageTitle = "Dashboard";
    [ObservableProperty] private string _statusText = "Starting…";
    [ObservableProperty] private string _statusKind = "busy"; // busy | ready | warn
    [ObservableProperty] private string _toastText = "";
    [ObservableProperty] private bool _isToastVisible;
    [ObservableProperty] private string _headerQuery = "";

    /// <summary>Raised when the search box should take focus (hotkey, Try it button, page switch).</summary>
    public event EventHandler? FocusSearchRequested;

    /// <summary>Raised when the user asks for Turbo indexing (relaunch elevated).</summary>
    public event EventHandler? TurboRequested;

    partial void OnCurrentPageChanged(AppPage value)
    {
        CurrentView = value switch
        {
            AppPage.Search => Search,
            AppPage.Drives => Drives,
            AppPage.Settings => Settings,
            _ => Dashboard,
        };
        PageTitle = value switch { AppPage.Search => "Search", AppPage.Drives => "Drives", AppPage.Settings => "Settings", _ => "Dashboard" };
        if (value == AppPage.Dashboard) Dashboard.Activate();
        if (value == AppPage.Drives) Drives.Refresh();
        if (value == AppPage.Search) FocusSearchRequested?.Invoke(this, EventArgs.Empty);
    }

    partial void OnHeaderQueryChanged(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        BeginSearch(value);
        HeaderQuery = "";
    }

    public void Initialize()
    {
        OnCurrentPageChanged(CurrentPage);
        UpdateStatus();
    }

    [RelayCommand]
    private void Navigate(AppPage page) => CurrentPage = page;

    public void BeginSearch(string text)
    {
        Search.QueryText = text;
        CurrentPage = AppPage.Search;
        FocusSearchRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void FocusSearch()
    {
        CurrentPage = AppPage.Search;
        FocusSearchRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void EnableTurbo() => TurboRequested?.Invoke(this, EventArgs.Empty);

    public void ShowToast(string text)
    {
        ToastText = text;
        IsToastVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void UpdateStatus()
    {
        var drives = _manager.Drives;
        var busy = drives.Where(d => d.Status is DriveStatus.Loading or DriveStatus.Scanning).ToList();
        int offline = drives.Count(d => d.Status == DriveStatus.Offline);
        int errors = drives.Count(d => d.Status == DriveStatus.Error);
        if (busy.Count > 0)
        {
            StatusKind = "busy";
            StatusText = $"Indexing… {busy.Average(d => d.Progress):P0}";
        }
        else if (offline + errors > 0)
        {
            StatusKind = "warn";
            StatusText = offline > 0 ? $"{offline} drive{(offline == 1 ? "" : "s")} offline" : $"{errors} drive{(errors == 1 ? "" : "s")} need attention";
        }
        else
        {
            StatusKind = "ready";
            StatusText = $"{Formatting.Count(_manager.Volumes.Sum(v => (long)v.LiveCount))} items";
        }
    }
}
