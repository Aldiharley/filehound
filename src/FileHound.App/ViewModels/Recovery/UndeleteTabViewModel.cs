using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileHound.App.Services;
using FileHound.Core.Recovery;
using FileHound.Indexing.Recovery;

namespace FileHound.App.ViewModels.Recovery;

public enum UndeleteSort { BestGrade, Newest, Name, Size }

/// <summary>The Undelete tab: one explicit Scan of the drive's MFT, then a graded, filterable list (UI §5.3).</summary>
public sealed partial class UndeleteTabViewModel : ObservableObject
{
    private readonly Func<RecoverySession?, IProgress<UndeleteProgress>, CancellationToken, Task<IReadOnlyList<RecoveryCandidate>>> _scanner;
    private RecoverySession? _session;
    private List<RecoveryItem> _all = [];
    private CancellationTokenSource? _cts;

    /// <summary>The scan in flight, so the session can be closed only after it has let go of the reader.</summary>
    public Task? ScanTask { get; private set; }

    /// <param name="scanner">Runs the scan; the default calls <see cref="RecoverySession.UndeleteAsync"/>.</param>
    public UndeleteTabViewModel(Func<RecoverySession?, IProgress<UndeleteProgress>, CancellationToken, Task<IReadOnlyList<RecoveryCandidate>>>? scanner = null)
    {
        _scanner = scanner ?? ((s, p, ct) => s is null ? Task.FromResult<IReadOnlyList<RecoveryCandidate>>([]) : s.UndeleteAsync(p, ct));
    }

    public ObservableCollection<RecoveryItem> Items { get; } = [];

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private UndeleteSort _sort = UndeleteSort.BestGrade;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private bool _hasScanned;
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _whyExpanded;
    [ObservableProperty] private string _estimatedIntactText = "";
    [ObservableProperty] private string _readPathText = "";
    [ObservableProperty] private bool _isAvailable;

    public string ScanButtonText => HasScanned ? "Rescan" : "Scan";

    public event EventHandler? SelectionChanged;

    partial void OnFilterChanged(string value) => Apply();
    partial void OnSortChanged(UndeleteSort value) => Apply();
    partial void OnHasScannedChanged(bool value) => OnPropertyChanged(nameof(ScanButtonText));

    public Task LoadAsync(RecoverySession? session)
    {
        Cancel();
        _session = session;
        _all = [];
        HasScanned = false;
        Error = null;
        ReadPathText = "";
        IsAvailable = session?.IsElevated ?? false;
        Apply();
        return Task.CompletedTask;
    }

    public void Cancel()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task Scan(CancellationToken token)
    {
        Cancel();
        var cts = _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var session = _session;
        IsScanning = true;
        Error = null;
        Progress = 0;
        ProgressText = "Reading the master file table…";
        var progress = new Progress<UndeleteProgress>(p =>
        {
            Progress = p.RecordsTotal > 0 ? (double)p.RecordsScanned / p.RecordsTotal : 0;
            ProgressText = $"{p.RecordsScanned:N0} of {p.RecordsTotal:N0} records · {p.Found:N0} deleted";
        });
        try
        {
            var scan = _scanner(session, progress, cts.Token);
            ScanTask = scan;
            var found = await scan;
            if (cts.IsCancellationRequested) return;
            var now = DateTime.UtcNow;
            var items = found.Select(c => new RecoveryItem(c, now)).ToList();
            foreach (var it in items) it.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(RecoveryItem.IsSelected)) { SelectionChanged?.Invoke(this, EventArgs.Empty); UpdateEstimate(); } };
            _all = items;
            HasScanned = true;
            ReadPathText = session?.ReadPath is { } p ? $"read path: {p}" : "";
            Apply();
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Warn($"Undelete scan failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (_cts is null || ReferenceEquals(_cts, cts)) { IsScanning = false; ProgressText = ""; }
            ScanTask = null;
        }
    }

    private static int Rank(RecoveryGrade g) => g switch
    {
        RecoveryGrade.Excellent => 0, RecoveryGrade.Good => 1, RecoveryGrade.Partial => 2, RecoveryGrade.Unknown => 3,
        RecoveryGrade.Zeroed => 4, RecoveryGrade.Overwritten => 5, _ => 6,
    };

    private void Apply()
    {
        var f = Filter.Trim();
        IEnumerable<RecoveryItem> shown = _all;
        if (!string.IsNullOrEmpty(f)) shown = shown.Where(i => i.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || i.Folder.Contains(f, StringComparison.OrdinalIgnoreCase));
        shown = Sort switch
        {
            UndeleteSort.Name => shown.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            UndeleteSort.Size => shown.OrderByDescending(i => i.Candidate.Size),
            UndeleteSort.Newest => shown.OrderByDescending(i => i.Candidate.DeletedUtc ?? i.Candidate.ModifiedUtc ?? DateTime.MinValue),
            _ => shown.OrderBy(i => Rank(i.Candidate.Grade)).ThenByDescending(i => i.Candidate.PercentIntact).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
        };
        Items.Clear();
        foreach (var i in shown) Items.Add(i);
        Count = _all.Count;
        IsEmpty = HasScanned && _all.Count == 0;
        UpdateEstimate();
    }

    private void UpdateEstimate()
    {
        var selected = Selected;
        if (selected.Count == 0) { EstimatedIntactText = ""; return; }
        double weight = 0, intact = 0;
        foreach (var i in selected)
        {
            double w = Math.Max(1, i.Candidate.Size);
            weight += w;
            intact += w * i.Candidate.PercentIntact;
        }
        EstimatedIntactText = $"Estimated intact: {Math.Round(intact / weight)}%";
    }

    public IReadOnlyList<RecoveryItem> Selected => _all.Where(i => i.IsSelected).ToList();

    [RelayCommand]
    private void CopyOriginalPath(RecoveryItem? item)
    {
        if (item is null) return;
        try { Clipboard.SetText(item.Candidate.OriginalPath); } catch (System.Runtime.InteropServices.COMException) { }
    }
}
