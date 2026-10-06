using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using FileHound.App.Services;
using FileHound.Core.Matching;
using FileHound.Core.Query;

namespace FileHound.App.ViewModels;

/// <summary>One search result row (immutable, built off the UI thread).</summary>
public sealed class ResultItem
{
    private ImageSource? _icon;
    private bool _iconLoaded;

    public static ShellIconProvider? IconProvider { get; set; }

    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required string FolderPath { get; init; }
    public required bool IsDirectory { get; init; }
    public long Size { get; init; }
    public string SizeText { get; init; } = "";
    public string ModifiedText { get; init; } = "";
    public MatchTier Tier { get; init; }
    public IReadOnlyList<(int Start, int Length)> Highlights { get; init; } = [];

    /// <summary>Shell icon, loaded lazily on first binding (UI thread).</summary>
    public ImageSource? Icon
    {
        get
        {
            if (!_iconLoaded) { _icon = IconProvider?.Get(FullPath, IsDirectory); _iconLoaded = true; }
            return _icon;
        }
    }

    public static ResultItem From(VolumeIndex v, int e, SearchQuery? query, DateTime nowUtc)
    {
        string name, full, folder;
        long size, modified;
        bool isDir, known;
        v.Lock.EnterReadLock();
        try
        {
            name = v.Name(e).ToString();
            full = PathBuilder.GetFullPath(v, e);
            folder = PathBuilder.GetParentPath(v, e);
            isDir = v.IsDirectory(e);
            known = (v.Flags(e) & EntryFlags.MetadataKnown) != 0;
            size = v.Size(e);
            modified = v.ModifiedTicks(e);
        }
        finally { v.Lock.ExitReadLock(); }
        return new ResultItem
        {
            Name = name,
            FullPath = full,
            FolderPath = folder,
            IsDirectory = isDir,
            Size = known ? size : -1,
            SizeText = isDir ? "" : known ? Formatting.Size(size) : "…",
            ModifiedText = known ? Formatting.Modified(modified, nowUtc) : "…",
            Highlights = query is null ? [] : Highlighter.Compute(name, query),
        };
    }
}

/// <summary>A selectable chip (category filter or sort mode).</summary>
public sealed partial class ChipItem<T>(string label, T value, Brush dot, Action<ChipItem<T>> onSelected, bool selected = false) : ObservableObject
{
    public string Label { get; } = label;
    public T Value { get; } = value;
    public Brush Dot { get; } = dot;

    /// <summary>Initial state is set without invoking the selection callback.</summary>
    [ObservableProperty]
    private bool _isSelected = selected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) onSelected(this);
    }
}

/// <summary>A drive row/card bound to the latest <see cref="DriveState"/>.</summary>
public sealed partial class DriveItem : ObservableObject
{
    public DriveItem(char letter) => Letter = letter;

    public char Letter { get; }
    public string Title => $"{Letter}:";

    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _format = "";
    [ObservableProperty] private string _capacityText = "";
    [ObservableProperty] private double _usedFraction;
    [ObservableProperty] private Brush _usedBrush = Brushes.Transparent;
    [ObservableProperty] private string _entriesText = "";
    [ObservableProperty] private string _modeText = "";
    [ObservableProperty] private Brush _modeBrush = Brushes.Transparent;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _skippedText = "";
    [ObservableProperty] private string _lastIndexedText = "";
    [ObservableProperty] private bool _isOffline;
    [ObservableProperty] private string? _error;

    public void Update(DriveState s)
    {
        var d = s.Drive;
        Label = string.IsNullOrWhiteSpace(d.Label) ? (d.IsRemovable ? "Removable drive" : "Local disk") : d.Label;
        Format = d.Format;
        long used = d.TotalSize - d.FreeSpace;
        UsedFraction = d.TotalSize > 0 ? (double)used / d.TotalSize : 0;
        UsedBrush = Theme.Brush(UsedFraction < 0.7 ? "MintDeep" : UsedFraction < 0.9 ? "ButterDeep" : "RoseDeep");
        CapacityText = $"{Formatting.Size(d.FreeSpace)} free of {Formatting.Size(d.TotalSize)}";
        EntriesText = $"{Formatting.Count(s.Entries)} items";
        ModeText = s.Mode == IndexMode.Turbo ? "Turbo" : "Standard";
        ModeBrush = Theme.Brush(s.Mode == IndexMode.Turbo ? "Mint" : "Sky");
        IsBusy = s.Status is DriveStatus.Loading or DriveStatus.Scanning or DriveStatus.FillingDetails;
        Progress = s.Progress;
        IsOffline = s.Status == DriveStatus.Offline;
        Error = s.Error;
        SkippedText = s.Skipped > 0 ? $"{Formatting.Count(s.Skipped)} folders skipped" : "No folders skipped";
        LastIndexedText = s.LastIndexed is { } t ? $"Indexed {Formatting.Modified(t.UtcTicks, DateTime.UtcNow)}" : "Not indexed yet";
        StatusText = s.Status switch
        {
            DriveStatus.Loading => "Loading…",
            DriveStatus.Scanning => $"Indexing… {s.Progress:P0}",
            DriveStatus.FillingDetails => $"Measuring files… {s.Progress:P0}",
            DriveStatus.Ready => "Ready",
            DriveStatus.Offline => "Offline",
            DriveStatus.Error => "Error",
            _ => "",
        };
    }
}

/// <summary>A donut slice and legend row.</summary>
public sealed record CategorySlice(string Label, long Count, double Fraction, Brush Brush)
{
    public string PercentText => Fraction >= 0.01 ? $"{Fraction:P0}" : "<1%";
    public string CountText => Formatting.Count(Count);
}

/// <summary>Access to theme brushes from code.</summary>
public static class Theme
{
    public static Brush Brush(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    public static Brush ForCategory(FileCategory c) => Brush(c switch
    {
        FileCategory.Folder => "Mint",
        FileCategory.Document => "Butter",
        FileCategory.Image => "Lilac",
        FileCategory.Video => "Sky",
        FileCategory.Audio => "Rose",
        FileCategory.Archive => "Peach",
        FileCategory.App => "SkyDeep",
        FileCategory.Code => "MintDeep",
        _ => "OtherCategory",
    });

    public static string LabelFor(FileCategory c) => c switch
    {
        FileCategory.Folder => "Folders",
        FileCategory.Document => "Documents",
        FileCategory.Image => "Images",
        FileCategory.Video => "Video",
        FileCategory.Audio => "Audio",
        FileCategory.Archive => "Archives",
        FileCategory.App => "Apps",
        FileCategory.Code => "Code",
        _ => "Other",
    };
}
