using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileHound.App.Services;
using FileHound.Core.Index;
using FileHound.Core.Recovery;
using FileHound.Indexing.Recovery;

namespace FileHound.App.ViewModels.Recovery;

public enum DeepScanSort { Type, Size, Location }

/// <summary>The Deep scan tab (UI §5.5): one-time consent, a scan of free space with pause/resume/stop, type chips, sort chips and a preview pane.</summary>
public sealed partial class DeepScanTabViewModel : ObservableObject
{
    public delegate Task<IReadOnlyList<RecoveryCandidate>> Scanner(RecoverySession? session, IReadOnlyCollection<string>? typeFilter, IProgress<CarveProgress> progress,
        Action<IReadOnlyList<RecoveryCandidate>> batch, CancellationToken ct);

    private readonly Func<bool> _consented;
    private readonly Action _markConsented;
    private readonly Scanner _scanner;
    private readonly Func<RecoverySession?, CarvedFile, PreviewContent?> _previewBuilder;
    private RecoverySession? _session;
    private List<RecoveryItem> _all = [];
    private CancellationTokenSource? _cts;
    private FileCategory? _category;
    private DeepScanSort _sort = DeepScanSort.Type;
    private readonly System.Diagnostics.Stopwatch _sinceApply = System.Diagnostics.Stopwatch.StartNew();
    private bool _applyPending;
    private int _previewVersion;

    /// <summary>The scan in flight, so the session is closed only after it has let go of the reader.</summary>
    public Task? ScanTask { get; private set; }

    public DeepScanTabViewModel(Func<bool> consented, Action markConsented, Scanner? scanner = null, Func<RecoverySession?, CarvedFile, PreviewContent?>? preview = null)
    {
        _consented = consented;
        _markConsented = markConsented;
        _scanner = scanner ?? ((s, f, p, b, ct) => s is null ? Task.FromResult<IReadOnlyList<RecoveryCandidate>>([]) : s.CarveAsync(f, p, b, ct));
        _previewBuilder = preview ?? ((s, f) => s is null ? null : PreviewService.Build(s.Reader, f));
        TypeChips =
        [
            new("All", null, Theme.Brush("Peach"), OnTypeChip, selected: true),
            new("Images", FileCategory.Image, Theme.ForCategory(FileCategory.Image), OnTypeChip),
            new("Video", FileCategory.Video, Theme.ForCategory(FileCategory.Video), OnTypeChip),
            new("Audio", FileCategory.Audio, Theme.ForCategory(FileCategory.Audio), OnTypeChip),
            new("Documents", FileCategory.Document, Theme.ForCategory(FileCategory.Document), OnTypeChip),
            new("Archives", FileCategory.Archive, Theme.ForCategory(FileCategory.Archive), OnTypeChip),
            new("Apps", FileCategory.App, Theme.ForCategory(FileCategory.App), OnTypeChip),
        ];
        SortChips =
        [
            new("Type", DeepScanSort.Type, Theme.Brush("Peach"), OnSortChip, selected: true),
            new("Size", DeepScanSort.Size, Theme.Brush("Butter"), OnSortChip),
            new("Location", DeepScanSort.Location, Theme.Brush("Sky"), OnSortChip),
        ];
    }

    public ObservableCollection<RecoveryItem> Items { get; } = [];
    public IReadOnlyList<ChipItem<FileCategory?>> TypeChips { get; }
    public IReadOnlyList<ChipItem<DeepScanSort>> SortChips { get; }

    [ObservableProperty] private bool _needsConsent;
    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private bool _hasScanned;
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private RecoveryItem? _previewItem;
    [ObservableProperty] private PreviewContent? _preview;

    public string PauseResumeText => IsPaused ? "Resume" : "Pause";
    public string ConsentText => $"Deep scan reads all free space on {_session?.Drive.Letter ?? 'C'}:, including data deleted by other user accounts. " +
        "On SSDs, Windows tells the drive to discard deleted data within seconds, so recent deletions may already be gone. " +
        "Use the PC as little as possible until you've recovered what you need.";

    public event EventHandler? SelectionChanged;

    partial void OnIsPausedChanged(bool value) => OnPropertyChanged(nameof(PauseResumeText));

    partial void OnPreviewItemChanged(RecoveryItem? value) => _ = LoadPreviewAsync(value);

    /// <summary>Reads and decodes off the UI thread; a newer selection wins.</summary>
    private async Task LoadPreviewAsync(RecoveryItem? value)
    {
        int version = ++_previewVersion;
        if (value?.Candidate.Key is not CarvedFile f) { Preview = null; return; }
        var session = _session;
        PreviewContent? content;
        try { content = await Task.Run(() => _previewBuilder(session, f)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or ArgumentOutOfRangeException) { content = null; }
        if (version == _previewVersion) Preview = content;
    }

    public Task LoadAsync(RecoverySession? session)
    {
        Cancel();
        _session = session;
        _all = [];
        HasScanned = false;
        Error = null;
        PreviewItem = null;
        IsAvailable = session?.IsElevated ?? false;
        NeedsConsent = false;
        OnPropertyChanged(nameof(ConsentText));
        Apply();
        return Task.CompletedTask;
    }

    public void Cancel()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>Scan free space: shows the FR-30 card first when the user has never agreed to it.</summary>
    [RelayCommand]
    private Task Scan()
    {
        if (IsScanning) return Task.CompletedTask;
        if (!_consented()) { NeedsConsent = true; return Task.CompletedTask; }
        return RunScanAsync();
    }

    /// <summary>"I understand, scan" on the consent card.</summary>
    [RelayCommand]
    private Task Consent()
    {
        _markConsented();
        NeedsConsent = false;
        return RunScanAsync();
    }

    [RelayCommand]
    private void DismissConsent() => NeedsConsent = false;

    [RelayCommand]
    private void Stop() => Cancel();

    [RelayCommand]
    private void PauseResume()
    {
        if (!IsScanning) return;
        if (IsPaused) { _session?.ResumeCarve(); IsPaused = false; }
        else { _session?.PauseCarve(); IsPaused = true; }
    }

    private async Task RunScanAsync()
    {
        Cancel();
        var cts = _cts = new CancellationTokenSource();
        var session = _session;
        _all = [];
        Apply();
        IsScanning = true;
        IsPaused = false;
        Error = null;
        Progress = 0;
        ProgressText = "Reading free space…";
        var now = DateTime.UtcNow;
        var progress = new Progress<CarveProgress>(p =>
        {
            Progress = p.FreeBytes > 0 ? (double)p.BytesScanned / p.FreeBytes : 0;
            ProgressText = FormatProgress(p.BytesScanned, p.FreeBytes, p.Eta);
        });
        void OnBatch(IReadOnlyList<RecoveryCandidate> found)
        {
            void Add()
            {
                if (!ReferenceEquals(_cts, cts)) return;
                foreach (var c in found) _all.Add(Wrap(c, now));
                ApplyThrottled();
            }
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) Add(); else dispatcher.InvokeAsync(Add);
        }
        try
        {
            var scan = _scanner(session, null, progress, OnBatch, cts.Token);
            ScanTask = scan;
            var found = await scan;
            if (cts.IsCancellationRequested) return;
            // The batches already delivered everything; rebuild from the full list to be exact.
            _all = found.Select(c => Wrap(c, now)).ToList();
            HasScanned = true;
            Apply();
        }
        catch (OperationCanceledException) { HasScanned = _all.Count > 0; Apply(); }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Warn($"Deep scan failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // Reset unless a newer scan has already replaced this one (Stop clears the field, so null counts as "this one").
            if (_cts is null || ReferenceEquals(_cts, cts)) { IsScanning = false; IsPaused = false; ProgressText = ""; }
            ScanTask = null;
        }
    }

    private RecoveryItem Wrap(RecoveryCandidate c, DateTime now)
    {
        var item = new RecoveryItem(c, now);
        if (c.Key is CarvedFile f)
        {
            item.TypeBrush = Theme.ForCategory(f.Type.Category);
            var session = _session;
            item.ThumbnailLoader = () =>
            {
                try { return session is null ? null : PreviewService.Thumbnail(session.Reader, f); }
                catch (Exception ex) when (ex is ObjectDisposedException or NotSupportedException or IOException) { return null; }
            };
        }
        item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(RecoveryItem.IsSelected)) SelectionChanged?.Invoke(this, EventArgs.Empty); };
        return item;
    }

    /// <summary>"12.4 GB of 410 GB · ETA 6 min".</summary>
    public static string FormatProgress(long scanned, long total, TimeSpan? eta)
    {
        string text = $"{Formatting.Size(scanned)} of {Formatting.Size(total)}";
        if (eta is { } t && t > TimeSpan.Zero)
            text += t.TotalMinutes >= 1 ? $" · ETA {Math.Ceiling(t.TotalMinutes)} min" : " · almost done";
        return text;
    }

    private void OnTypeChip(ChipItem<FileCategory?> chip)
    {
        foreach (var c in TypeChips) if (!ReferenceEquals(c, chip)) c.IsSelected = false;
        _category = chip.Value;
        Apply();
    }

    private void OnSortChip(ChipItem<DeepScanSort> chip)
    {
        foreach (var c in SortChips) if (!ReferenceEquals(c, chip)) c.IsSelected = false;
        _sort = chip.Value;
        Apply();
    }

    /// <summary>During a scan, batches arrive faster than the list can be rebuilt: refresh at most twice a second.</summary>
    private void ApplyThrottled()
    {
        if (_sinceApply.ElapsedMilliseconds < 500)
        {
            if (_applyPending) return;
            _applyPending = true;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) { _applyPending = false; Apply(); return; }
            _ = Task.Delay(500).ContinueWith(_ => dispatcher.InvokeAsync(() => { _applyPending = false; Apply(); }), TaskScheduler.Default);
            return;
        }
        Apply();
    }

    private void Apply()
    {
        _sinceApply.Restart();
        var keep = PreviewItem;
        IEnumerable<RecoveryItem> shown = _all;
        if (_category is { } cat) shown = shown.Where(i => i.Candidate.Key is CarvedFile f && f.Type.Category == cat);
        shown = _sort switch
        {
            DeepScanSort.Size => shown.OrderByDescending(i => i.Candidate.Size),
            DeepScanSort.Location => shown.OrderBy(i => ((CarvedFile)i.Candidate.Key).StartLcn),
            _ => shown.OrderBy(i => ((CarvedFile)i.Candidate.Key).Type.Label, StringComparer.OrdinalIgnoreCase).ThenBy(i => ((CarvedFile)i.Candidate.Key).StartLcn),
        };
        Items.Clear();
        foreach (var i in shown) Items.Add(i);
        Count = _all.Count;
        IsEmpty = HasScanned && _all.Count == 0;
        if (keep is not null && Items.Contains(keep) && !ReferenceEquals(PreviewItem, keep)) PreviewItem = keep;   // Clear() dropped the selection
    }

    public IReadOnlyList<RecoveryItem> Selected => _all.Where(i => i.IsSelected).ToList();
}
