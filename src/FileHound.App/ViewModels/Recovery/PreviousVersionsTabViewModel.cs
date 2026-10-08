using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileHound.App.Services;
using FileHound.Core.Recovery;
using FileHound.Indexing.Recovery;

namespace FileHound.App.ViewModels.Recovery;

/// <summary>The Previous versions tab: a path, the snapshots that still hold it, and the one explicit write — Freeze (UI §5.4).</summary>
public sealed partial class PreviousVersionsTabViewModel : ObservableObject
{
    private readonly Func<char, string, IReadOnlyList<ShadowVersion>> _versions;
    private readonly Func<char, string?> _freeze;
    private readonly Func<char, IReadOnlyList<ShadowCopy>> _snapshots;
    private RecoverySession? _session;
    private List<RecoveryItem> _all = [];

    public PreviousVersionsTabViewModel(Func<char, string, IReadOnlyList<ShadowVersion>>? versions = null, Func<char, string?>? freeze = null,
        Func<char, IReadOnlyList<ShadowCopy>>? snapshots = null)
    {
        _versions = versions ?? ShadowCopySource.Versions;
        _freeze = freeze ?? ShadowCopies.Create;
        _snapshots = snapshots ?? ShadowCopies.List;
    }

    public ObservableCollection<RecoveryItem> Items { get; } = [];

    [ObservableProperty] private string _path = "";
    [ObservableProperty] private string _snapshotsText = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private bool _hasLookedUp;
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _hasSnapshots;

    /// <summary>Set by the view: shows the FR-20 warning and returns true only when the user chose to continue.</summary>
    public Func<bool>? ConfirmFreeze { get; set; }

    public event EventHandler? SelectionChanged;
    /// <summary>Short messages for the toast ("Snapshot created").</summary>
    public event EventHandler<string>? Notice;

    private char Letter => _session?.Drive.Letter ?? (Path.Length > 0 ? char.ToUpperInvariant(Path[0]) : 'C');

    public async Task LoadAsync(RecoverySession? session)
    {
        _session = session;
        _all = [];
        HasLookedUp = false;
        Error = null;
        Apply();
        IsAvailable = session?.IsElevated ?? false;
        if (session is null) { SnapshotsText = ""; HasSnapshots = false; return; }
        await RefreshSnapshotsAsync();
    }

    private async Task RefreshSnapshotsAsync()
    {
        char letter = Letter;
        try
        {
            var list = await Task.Run(() => _snapshots(letter));
            HasSnapshots = list.Count > 0;
            SnapshotsText = list.Count == 0
                ? $"No snapshots exist for {letter}:"
                : $"{list.Count} snapshot{(list.Count == 1 ? "" : "s")} · newest {list[0].CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SnapshotsText = "Snapshots could not be listed";
            Log.Warn($"Shadow copy listing failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task Lookup()
    {
        Error = null;
        var path = Path.Trim().Trim('"');
        if (path.Length == 0) return;
        IsBusy = true;
        try
        {
            char letter = Letter;
            var versions = await Task.Run(() => _versions(letter, path));
            var now = DateTime.UtcNow;
            var items = versions.Select(v => new RecoveryItem(ToCandidate(v), now)).ToList();
            foreach (var it in items) it.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(RecoveryItem.IsSelected)) SelectionChanged?.Invoke(this, EventArgs.Empty); };
            _all = items;
            HasLookedUp = true;
            Apply();
        }
        catch (ArgumentException ex) { Error = ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
            Log.Warn($"Previous versions lookup failed: {ex.Message}");
        }
        finally { IsBusy = false; }
    }

    public static RecoveryCandidate ToCandidate(ShadowVersion v)
    {
        string trimmed = v.OriginalPath.TrimEnd('\\');
        return new RecoveryCandidate(RecoverySource.ShadowCopy, System.IO.Path.GetFileName(trimmed), System.IO.Path.GetDirectoryName(trimmed),
            v.Size, v.ModifiedUtc, v.Snapshot.CreatedUtc, RecoveryGrade.Excellent, 100, v.IsDirectory,
            $"Snapshot from {v.Snapshot.CreatedUtc.ToLocalTime():f}", v);
    }

    [RelayCommand]
    private async Task Freeze()
    {
        if (ConfirmFreeze?.Invoke() != true) return;
        IsBusy = true;
        try
        {
            char letter = Letter;
            string? id = await Task.Run(() => _freeze(letter));
            if (id is null) Error = "Windows refused to create a snapshot (details are in the log)";
            else
            {
                Notice?.Invoke(this, $"Snapshot of {letter}: created");
                await RefreshSnapshotsAsync();
            }
        }
        finally { IsBusy = false; }
    }

    private void Apply()
    {
        Items.Clear();
        foreach (var i in _all) Items.Add(i);
        Count = _all.Count;
        IsEmpty = HasLookedUp && _all.Count == 0;
    }

    public IReadOnlyList<RecoveryItem> Selected => _all.Where(i => i.IsSelected).ToList();
}
