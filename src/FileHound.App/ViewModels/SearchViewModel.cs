using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileHound.App.Services;

namespace FileHound.App.ViewModels;

public sealed partial class SearchViewModel : ObservableObject
{
    private readonly SearchService _search;
    private readonly ShellService _shell;
    private readonly IndexManager _manager;
    private readonly AppSettings _settings;
    private readonly Action _saveSettings;
    private readonly Action<string> _toast;
    private FileHound.Core.Index.FileCategory? _category;
    private SortMode _sort = SortMode.Relevance;
    private DateTime _lastLiveRefresh;

    public SearchViewModel(SearchService search, ShellService shell, IndexManager manager, AppSettings settings, Action saveSettings, Action<string> toast)
    {
        _search = search;
        _shell = shell;
        _manager = manager;
        _settings = settings;
        _saveSettings = saveSettings;
        _toast = toast;
        _fuzzy = settings.Fuzzy;

        Chips =
        [
            new("All", null, Theme.Brush("Peach"), OnChip, selected: true),
            new("Folders", FileCategory.Folder, Theme.ForCategory(FileCategory.Folder), OnChip),
            new("Documents", FileCategory.Document, Theme.ForCategory(FileCategory.Document), OnChip),
            new("Images", FileCategory.Image, Theme.ForCategory(FileCategory.Image), OnChip),
            new("Video", FileCategory.Video, Theme.ForCategory(FileCategory.Video), OnChip),
            new("Audio", FileCategory.Audio, Theme.ForCategory(FileCategory.Audio), OnChip),
            new("Archives", FileCategory.Archive, Theme.ForCategory(FileCategory.Archive), OnChip),
            new("Apps", FileCategory.App, Theme.ForCategory(FileCategory.App), OnChip),
            new("Code", FileCategory.Code, Theme.ForCategory(FileCategory.Code), OnChip),
        ];
        SortChips =
        [
            new("Best match", SortMode.Relevance, Theme.Brush("Peach"), OnSort, selected: true),
            new("Name", SortMode.Name, Theme.Brush("Mint"), OnSort),
            new("Size", SortMode.Size, Theme.Brush("Butter"), OnSort),
            new("Modified", SortMode.Modified, Theme.Brush("Sky"), OnSort),
        ];
        _manager.IndexChanged += (_, _) => Application.Current?.Dispatcher.InvokeAsync(OnIndexChanged);
    }

    public IReadOnlyList<ChipItem<FileHound.Core.Index.FileCategory?>> Chips { get; }
    public IReadOnlyList<ChipItem<SortMode>> SortChips { get; }
    public IReadOnlyList<string> Examples { get; } = ["ext:pdf invoice", "size:>1gb", "folder:projects", "dm:week", "*.mp4 holiday", "budjet 2025"];

    [ObservableProperty] private string _queryText = "";
    [ObservableProperty] private bool _fuzzy;
    [ObservableProperty] private IReadOnlyList<ResultItem> _results = [];
    [ObservableProperty] private ResultItem? _selected;
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private bool _usedTypoPass;
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private bool _isEmptyQuery = true;
    [ObservableProperty] private bool _hasNoResults;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string? _indexingNotice;

    public bool CanDrag => !_shell.IsElevated;

    partial void OnQueryTextChanged(string value) => _ = RunAsync();

    partial void OnFuzzyChanged(bool value)
    {
        _settings.Fuzzy = value;
        _saveSettings();
        FuzzyChanged?.Invoke(value);
        _ = RunAsync(debounce: 0);
    }

    /// <summary>Keeps the Settings page's Fuzzy toggle in sync.</summary>
    public Action<bool>? FuzzyChanged { get; set; }

    private void OnChip(ChipItem<FileHound.Core.Index.FileCategory?> chip)
    {
        foreach (var c in Chips) if (!ReferenceEquals(c, chip)) c.IsSelected = false;
        _category = chip.Value;
        _ = RunAsync(debounce: 0);
    }

    private void OnSort(ChipItem<SortMode> chip)
    {
        foreach (var c in SortChips) if (!ReferenceEquals(c, chip)) c.IsSelected = false;
        _sort = chip.Value;
        _ = RunAsync(debounce: 0);
    }

    public void SetSort(SortMode mode)
    {
        foreach (var c in SortChips) c.IsSelected = c.Value == mode;
    }

    /// <summary>Selects the category chip by index (Ctrl+1..9).</summary>
    public void SelectChip(int index)
    {
        if (index >= 0 && index < Chips.Count) Chips[index].IsSelected = true;
    }

    private bool _refreshScheduled;

    private void OnIndexChanged()
    {
        UpdateIndexingNotice();
        if (IsEmptyQuery && _category is null) return;
        var wait = TimeSpan.FromSeconds(1) - (DateTime.UtcNow - _lastLiveRefresh);
        if (wait > TimeSpan.Zero)
        {
            // Defer (rather than drop) so the last burst of changes still shows up.
            if (_refreshScheduled) return;
            _refreshScheduled = true;
            _ = Task.Delay(wait).ContinueWith(_ => Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                _refreshScheduled = false;
                OnIndexChanged();
            }), TaskScheduler.Default);
            return;
        }
        _lastLiveRefresh = DateTime.UtcNow;
        _ = RunAsync(debounce: 0, keepSelection: true);
    }

    public void UpdateIndexingNotice()
    {
        var busy = _manager.Drives.FirstOrDefault(d => d.Status is DriveStatus.Scanning or DriveStatus.Loading);
        var filling = _manager.Drives.FirstOrDefault(d => d.Status == DriveStatus.FillingDetails);
        IndexingNotice = busy is not null ? $"Still indexing {busy.Drive.Letter}: ({busy.Progress:P0}) — results may be incomplete"
            : filling is not null ? $"Measuring files on {filling.Drive.Letter}: — sizes and dates are still filling in"
            : null;
    }

    public async Task RunAsync(int debounce = 40, bool keepSelection = false)
    {
        var text = QueryText ?? "";
        IsEmptyQuery = string.IsNullOrWhiteSpace(text) && _category is null;
        if (IsEmptyQuery)
        {
            await _search.SearchAsync(new SearchRequest(""), 0); // cancels anything in flight
            Results = [];
            SummaryText = "";
            HasNoResults = false;
            ErrorText = null;
            UsedTypoPass = false;
            return;
        }
        IsSearching = true;
        var outcome = await _search.SearchAsync(new SearchRequest(text, _sort, 5000, Fuzzy, _settings.IncludeHidden, _category), debounce);
        if (outcome is null) return; // superseded
        IsSearching = false;
        var previous = keepSelection ? Selected?.FullPath : null;
        Results = outcome.Items;
        var r = outcome.Result;
        string capped = r.TotalCount > r.Hits.Count ? $" · showing top {Formatting.Count(r.Hits.Count)}" : "";
        SummaryText = $"{Formatting.Count(r.TotalCount)} result{(r.TotalCount == 1 ? "" : "s")} · {r.Elapsed.TotalMilliseconds:F0} ms{capped}";
        UsedTypoPass = r.UsedTypoPass;
        ErrorText = r.Errors.Count > 0 ? string.Join(" · ", r.Errors) : null;
        HasNoResults = r.TotalCount == 0;
        Selected = previous is null ? Results.FirstOrDefault() : Results.FirstOrDefault(i => i.FullPath == previous) ?? Results.FirstOrDefault();
        UpdateIndexingNotice();
    }

    private void Remember()
    {
        if (string.IsNullOrWhiteSpace(QueryText)) return;
        _settings.AddRecent(QueryText);
        _saveSettings();
    }

    private void Try(Action a, string? success = null)
    {
        try
        {
            a();
            if (success is not null) _toast(success);
        }
        catch (Exception ex)
        {
            Log.Warn(ex.Message);
            _toast(ex.Message);
        }
    }

    [RelayCommand]
    private void Open(ResultItem? item)
    {
        item ??= Selected;
        if (item is null) return;
        Remember();
        Try(() => _shell.Open(item.FullPath));
    }

    [RelayCommand]
    private void Reveal(ResultItem? item)
    {
        item ??= Selected;
        if (item is null) return;
        Remember();
        Try(() => _shell.Reveal(item.FullPath));
    }

    [RelayCommand]
    private void CopyPath(ResultItem? item)
    {
        item ??= Selected;
        if (item is null) return;
        Remember();
        Try(() => ShellService.CopyText(item.FullPath), "Path copied ✓");
    }

    [RelayCommand]
    private void CopyName(ResultItem? item)
    {
        item ??= Selected;
        if (item is null) return;
        Try(() => ShellService.CopyText(item.Name), "Name copied ✓");
    }

    [RelayCommand]
    private void CopyFile(ResultItem? item)
    {
        item ??= Selected;
        if (item is null) return;
        Remember();
        Try(() => ShellService.CopyFile(item.FullPath), "File copied — paste it anywhere ✓");
    }

    [RelayCommand]
    private void Properties(ResultItem? item)
    {
        item ??= Selected;
        if (item is null) return;
        Try(() => _shell.ShowProperties(item.FullPath));
    }

    [RelayCommand]
    private void InsertExample(string example) => QueryText = example;

    [RelayCommand]
    private void Clear() => QueryText = "";

    [RelayCommand]
    private void EnableFuzzy() => Fuzzy = true;

    public bool TryStartDrag(DependencyObject source, ResultItem item) => _shell.TryStartDrag(source, item.FullPath);
}
