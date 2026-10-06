using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileHound.App.Services;
using FileHound.Core.Stats;

namespace FileHound.App.ViewModels;

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly IndexManager _manager;
    private readonly AppSettings _settings;
    private readonly Action _saveSettings;
    private readonly Action<string> _beginSearch;
    private readonly ShellService _shell;
    private readonly Dictionary<char, DriveItem> _drives = [];
    private int _refreshQueued;
    private DateTime _lastStats;

    public DashboardViewModel(IndexManager manager, AppSettings settings, Action saveSettings, Action<string> beginSearch, ShellService shell)
    {
        _manager = manager;
        _settings = settings;
        _saveSettings = saveSettings;
        _beginSearch = beginSearch;
        _shell = shell;
        _manager.StateChanged += (_, _) => QueueRefresh();
        _manager.IndexChanged += (_, _) => QueueRefresh();
    }

    public ObservableCollection<DriveItem> Drives { get; } = [];
    public ObservableCollection<string> RecentSearches { get; } = [];
    public ObservableCollection<CategorySlice> Slices { get; } = [];
    public ObservableCollection<ResultItem> Largest { get; } = [];

    [ObservableProperty] private string _greeting = "";
    [ObservableProperty] private string _filesText = "—";
    [ObservableProperty] private string _foldersText = "—";
    [ObservableProperty] private string _drivesText = "—";
    [ObservableProperty] private string _drivesCaption = "";
    [ObservableProperty] private string _filesCaption = "";
    [ObservableProperty] private string _foldersCaption = "";
    [ObservableProperty] private string _indexModeText = "—";
    [ObservableProperty] private string _indexCaption = "";
    [ObservableProperty] private bool _isMeasuring = true;
    [ObservableProperty] private string _heroQuery = "";
    [ObservableProperty] private string _hotkeyText = "";

    public bool HasRecent => RecentSearches.Count > 0;

    public void Activate()
    {
        var h = DateTime.Now.Hour;
        Greeting = h < 5 ? "Up late, night owl?" : h < 12 ? "Good morning!" : h < 18 ? "Good afternoon!" : "Good evening!";
        HotkeyText = _settings.Hotkey.Replace("+", " + ");
        RecentSearches.Clear();
        foreach (var q in _settings.RecentSearches.Take(5)) RecentSearches.Add(q);
        OnPropertyChanged(nameof(HasRecent));
        _lastStats = default;
        QueueRefresh();
    }

    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(300);
            Volatile.Write(ref _refreshQueued, 0);
            var states = _manager.Drives;
            IndexStatistics? stats = null;
            // Full statistics are an O(n) pass; refresh them at most every 3 s.
            if (DateTime.UtcNow - _lastStats > TimeSpan.FromSeconds(3))
            {
                _lastStats = DateTime.UtcNow;
                stats = IndexStatistics.Compute(_manager.Volumes, 5);
            }
            IReadOnlyList<ResultItem>? largest = stats?.Largest.Select(l => ResultItem.From(l.Volume, l.Entry, null, DateTime.UtcNow)).ToList();
            Application.Current?.Dispatcher.InvokeAsync(() => Apply(states, stats, largest));
        });
    }

    private void Apply(IReadOnlyList<DriveState> states, IndexStatistics? stats, IReadOnlyList<ResultItem>? largest)
    {
        foreach (var s in states)
        {
            if (!_drives.TryGetValue(s.Drive.Letter, out var item))
            {
                item = new DriveItem(s.Drive.Letter);
                _drives[s.Drive.Letter] = item;
                Drives.Add(item);
            }
            item.Update(s);
        }
        int ntfs = states.Count(s => s.Drive.IsNtfs);
        DrivesText = states.Count(s => s.Status != DriveStatus.Offline).ToString();
        DrivesCaption = $"{ntfs} NTFS · {states.Count - ntfs} other";
        bool turbo = states.Count > 0 && states.Where(s => s.Drive.IsNtfs).All(s => s.Mode == IndexMode.Turbo) && ntfs > 0;
        IndexModeText = turbo ? "Turbo" : "Standard";
        var busy = states.Where(s => s.Status is DriveStatus.Scanning or DriveStatus.Loading or DriveStatus.FillingDetails).ToList();
        IndexCaption = busy.Count > 0 ? $"indexing {string.Join(", ", busy.Select(b => b.Drive.Letter + ":"))}"
            : states.Max(s => s.LastIndexed) is { } last ? $"updated {Formatting.Modified(last.UtcTicks, DateTime.UtcNow)}" : "starting…";
        IsMeasuring = states.Any(s => !s.MetadataComplete);

        if (stats is null) return;
        FilesText = Formatting.Count(stats.Files);
        FoldersText = Formatting.Count(stats.Folders);
        FilesCaption = $"{Formatting.Size(stats.TotalBytes)} in total";
        FoldersCaption = $"across {DrivesText} drives";

        Slices.Clear();
        long total = stats.Categories.Sum(c => c.Count);
        // Named categories first (largest first), then everything else folded into a single "Other" slice.
        var named = stats.Categories.Where(c => c.Category != FileCategory.Other).Take(6).ToList();
        foreach (var c in named)
            Slices.Add(new CategorySlice(Theme.LabelFor(c.Category), c.Count, total == 0 ? 0 : (double)c.Count / total, Theme.ForCategory(c.Category)));
        long other = total - named.Sum(c => c.Count);
        if (other > 0) Slices.Add(new CategorySlice("Other", other, (double)other / total, Theme.Brush("OtherCategory")));

        Largest.Clear();
        if (largest is not null) foreach (var l in largest) Largest.Add(l);
    }

    [RelayCommand]
    private void Search(string? text)
    {
        text ??= HeroQuery;
        HeroQuery = "";
        _beginSearch(text ?? "");
    }

    [RelayCommand]
    private void ClearRecent()
    {
        _settings.RecentSearches.Clear();
        _saveSettings();
        RecentSearches.Clear();
        OnPropertyChanged(nameof(HasRecent));
    }

    [RelayCommand]
    private void OpenFile(ResultItem? item)
    {
        if (item is null) return;
        try { _shell.Open(item.FullPath); }
        catch (InvalidOperationException ex) { Log.Warn(ex.Message); }
    }

    [RelayCommand]
    private void RevealFile(ResultItem? item)
    {
        if (item is null) return;
        _shell.Reveal(item.FullPath);
    }
}
