using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileHound.App.Services;
using FileHound.Core.Recovery;
using FileHound.Indexing.Recovery;
using Microsoft.Win32;

namespace FileHound.App.ViewModels.Recovery;

public enum RecoveryTab { RecycleBin, Deleted, Undelete, PreviousVersions }

/// <summary>The Recovery page: a drive, a source tab, a selection, a destination, and one Recover/Restore action.</summary>
public sealed partial class RecoveryViewModel : ObservableObject
{
    private readonly IndexManager _manager;
    private readonly Action<string> _toast;
    private readonly Action _enableTurbo;
    private RecoverySession? _session;
    private DeletionLog? _subscribedLog;
    private bool _reloadQueued;
    private Task? _batch;
    private readonly Func<IndexManager, DriveDescriptor, RecoverySession> _sessionFactory;

    public RecoveryViewModel(IndexManager manager, bool isElevated, Action<string> toast, Action enableTurbo,
        DeletedTabViewModel? deletedTab = null, Func<IndexManager, DriveDescriptor, RecoverySession>? sessionFactory = null)
    {
        _manager = manager;
        IsElevated = isElevated;
        _toast = toast;
        _enableTurbo = enableTurbo;
        _sessionFactory = sessionFactory ?? ((m, d) => new RecoverySession(m, d));
        RecycleBin = new RecycleBinTabViewModel();
        Deleted = deletedTab ?? new DeletedTabViewModel();
        Undelete = new UndeleteTabViewModel();
        Versions = new PreviousVersionsTabViewModel();
        RecycleBin.SelectionChanged += (_, _) => UpdateSelection();
        Deleted.SelectionChanged += (_, _) => UpdateSelection();
        Undelete.SelectionChanged += (_, _) => UpdateSelection();
        Versions.SelectionChanged += (_, _) => UpdateSelection();
        Versions.Notice += (_, m) => _toast(m);
        Undelete.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(UndeleteTabViewModel.IsScanning) or nameof(UndeleteTabViewModel.Progress)) ScanStateChanged?.Invoke(this, EventArgs.Empty);
        };
        _manager.StateChanged += (_, _) => Application.Current?.Dispatcher.InvokeAsync(RefreshDrives);
    }

    public bool IsElevated { get; }
    public RecycleBinTabViewModel RecycleBin { get; }
    public DeletedTabViewModel Deleted { get; }
    public UndeleteTabViewModel Undelete { get; }
    public PreviousVersionsTabViewModel Versions { get; }

    /// <summary>Raised when the undelete scan starts, progresses or ends (the header status chip follows it).</summary>
    public event EventHandler? ScanStateChanged;
    public bool IsScanning => Undelete.IsScanning;
    public double ScanProgress => Undelete.Progress;
    public ObservableCollection<DriveItem> Drives { get; } = [];
    public IReadOnlyList<RecoveredFile> Recovered => _session?.Recovered ?? [];

    [ObservableProperty] private DriveItem? _selectedDrive;
    [ObservableProperty] private RecoveryTab _currentTab = RecoveryTab.RecycleBin;
    [ObservableProperty] private string? _destinationFolder;
    [ObservableProperty] private string? _destinationError;
    [ObservableProperty] private bool _canRecover;
    [ObservableProperty] private string _recoverButtonText = "Restore";
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private string _selectedSummary = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string? _recoveryFolder;
    [ObservableProperty] private bool _hasSession;
    [ObservableProperty] private bool _journalBannerDismissed;
    [ObservableProperty] private bool _canSaveElsewhere;

    /// <summary>Restore puts Recycle Bin items back in place and snapshot versions next to the current file; Recover copies to the destination (different drive).</summary>
    public bool IsRestoreTab => CurrentTab is RecoveryTab.RecycleBin or RecoveryTab.PreviousVersions;

    private IReadOnlyList<RecoveryItem> CurrentSelected => CurrentTab switch
    {
        RecoveryTab.RecycleBin => RecycleBin.Selected,
        RecoveryTab.Deleted => Deleted.Selected,
        RecoveryTab.Undelete => Undelete.Selected,
        _ => Versions.Selected,
    };

    private IEnumerable<RecoveryItem> CurrentItems => CurrentTab switch
    {
        RecoveryTab.RecycleBin => RecycleBin.Items,
        RecoveryTab.Deleted => Deleted.Items,
        RecoveryTab.Undelete => Undelete.Items,
        _ => Versions.Items,
    };

    public void Activate()
    {
        RefreshDrives();
        if (SelectedDrive is null && Drives.Count > 0) SelectedDrive = Drives.FirstOrDefault(d => d.Letter == 'C') ?? Drives[0];
    }

    private void RefreshDrives()
    {
        var states = _manager.Drives.Where(s => s.Drive.IsNtfs && s.Status != DriveStatus.Offline).ToList();
        foreach (var s in states)
        {
            var item = Drives.FirstOrDefault(d => d.Letter == s.Drive.Letter);
            if (item is null) { item = new DriveItem(s.Drive.Letter); Drives.Add(item); }
            item.Update(s);
        }
        foreach (var gone in Drives.Where(d => states.All(s => s.Drive.Letter != d.Letter)).ToList()) Drives.Remove(gone);
    }

    partial void OnSelectedDriveChanged(DriveItem? value) => _ = OpenSessionAsync(value);

    partial void OnCurrentTabChanged(RecoveryTab value)
    {
        OnPropertyChanged(nameof(IsRestoreTab));
        UpdateSelection();
    }

    partial void OnDestinationFolderChanged(string? value) => UpdateSelection();

    private async Task OpenSessionAsync(DriveItem? drive)
    {
        CloseSession();
        if (drive is null) return;
        var descriptor = _manager.Drives.FirstOrDefault(s => s.Drive.Letter == drive.Letter)?.Drive;
        if (descriptor is null) return;
        var session = _session = _sessionFactory(_manager, descriptor);
        HasSession = true;
        RecoveryFolder = null;
        if (session.Log is { } log)
        {
            _subscribedLog = log;
            log.Changed += OnLogChanged;
        }
        await Task.WhenAll(RecycleBin.LoadAsync(session), Deleted.LoadAsync(session), Undelete.LoadAsync(session), Versions.LoadAsync(session));
        UpdateSelection();
    }

    /// <summary>Coalesces bursts of log changes (a folder delete raises one per file) into one reload per dispatcher pass.</summary>
    private void OnLogChanged(object? sender, EventArgs e)
    {
        if (_reloadQueued) return;
        _reloadQueued = true;
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            _reloadQueued = false;
            if (_session is not null && !IsBusy) Deleted.Reload();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void CloseSession()
    {
        if (_subscribedLog is { } log) { log.Changed -= OnLogChanged; _subscribedLog = null; }
        var session = _session;
        _session = null;
        HasSession = false;
        if (session is null) return;
        // The reader may be mid-read on a thread-pool thread (a scan or a batch): let that finish before the handles close.
        var pending = new[] { Undelete.ScanTask, _batch }.Where(t => t is not null && !t.IsCompleted).ToList();
        if (pending.Count == 0) { session.Dispose(); return; }
        _ = Task.WhenAll(pending!).ContinueWith(_ => session.Dispose(), TaskScheduler.Default);
    }

    private void UpdateSelection()
    {
        var selected = CurrentSelected;
        SelectedCount = selected.Count;
        long bytes = selected.Where(i => !i.IsDirectory && i.Candidate.Size > 0).Sum(i => i.Candidate.Size);
        SelectedSummary = SelectedCount == 0 ? "" : $"{SelectedCount} selected · {Formatting.Size(bytes)}";
        string verb = IsRestoreTab ? "Restore" : "Recover";
        RecoverButtonText = SelectedCount > 0 ? $"{verb} {SelectedCount}" : verb;

        DestinationError = null;
        if (!IsRestoreTab && _session is not null && DestinationFolder is not null &&
            !RecoverySession.IsDifferentVolume(DestinationFolder, _session.Drive, out var why))
            DestinationError = why;
        bool destinationOk = IsRestoreTab || (DestinationFolder is not null && DestinationError is null);
        CanRecover = SelectedCount > 0 && destinationOk && !IsBusy && _session is not null;
        CanSaveElsewhere = IsRestoreTab && SelectedCount > 0 && !IsBusy && _session is not null;
    }

    [RelayCommand]
    private void PickDestination()
    {
        var dialog = new OpenFolderDialog { Title = "Where should recovered files go? (a different drive from the one you're recovering from)" };
        if (dialog.ShowDialog() == true) DestinationFolder = dialog.FolderName;
    }

    [RelayCommand]
    private Task Recover() => CanRecover ? RunBatchAsync(restoreInPlace: IsRestoreTab, DestinationFolder) : Task.CompletedTask;

    /// <summary>On the in-place tabs: copy the selection to a folder of the user's choice instead (any drive).</summary>
    [RelayCommand]
    private Task SaveElsewhere()
    {
        if (!CanSaveElsewhere) return Task.CompletedTask;
        var dialog = new OpenFolderDialog { Title = "Save the selected items to…" };
        return dialog.ShowDialog() == true ? RunBatchAsync(restoreInPlace: false, dialog.FolderName) : Task.CompletedTask;
    }

    private async Task RunBatchAsync(bool restoreInPlace, string? destination)
    {
        // Captured once: the field can be cleared by a drive change or by leaving the page while this runs, and the
        // batch should finish against the session it started with.
        var session = _session;
        if (session is null || (!restoreInPlace && destination is null)) return;
        var items = CurrentSelected.ToList();
        if (items.Count == 0) return;
        var batch = new TaskCompletionSource();
        _batch = batch.Task;
        IsBusy = true;
        CanRecover = false;
        int done = 0, ok = 0;
        var sw = Stopwatch.StartNew();
        long bytes = 0;
        try
        {
            foreach (var it in items)
            {
                Progress = (double)done / items.Count;
                ProgressText = $"{done + 1} of {items.Count} · {it.Name}";
                try
                {
                    if (restoreInPlace)
                    {
                        string path = await session.RestoreAsync(it.Candidate, keepBoth: true);
                        it.ApplyRestore(path);
                        ok++;
                    }
                    else
                    {
                        var r = await session.RecoverAsync(it.Candidate, destination!, CancellationToken.None);
                        it.ApplyOutcome(r);
                        if (r.Succeeded) { ok++; bytes += r.Bytes; }
                    }
                }
                catch (Exception ex)
                {
                    // One item must not end the batch; the session already swallowed the common cases, so this is the backstop.
                    it.Status = "Failed — " + ex.Message;
                    it.StatusKey = "Failed";
                    Log.Warn($"Recovery failed for {it.Candidate.OriginalPath}: {ex.GetType().Name}: {ex.Message}");
                    if (ex is ObjectDisposedException) break;
                }
                done++;
            }
            Progress = 1;
            RecoveryFolder = session.RecoveryFolder;
            if (restoreInPlace) _toast(ok == items.Count ? $"{ok} item{(ok == 1 ? "" : "s")} restored" : $"{ok} of {items.Count} restored — see the list for details");
            else _toast(ok == items.Count ? $"{ok} file{(ok == 1 ? "" : "s")} recovered ({Formatting.Size(bytes)}) in {sw.Elapsed.TotalSeconds:F0}s" : $"{ok} of {items.Count} recovered — see the list for details");
            if (restoreInPlace && CurrentTab == RecoveryTab.RecycleBin && ReferenceEquals(session, _session)) await RecycleBin.LoadAsync(session);
        }
        finally
        {
            IsBusy = false;
            ProgressText = "";
            batch.SetResult();
            UpdateSelection();
        }
    }

    [RelayCommand]
    private void OpenRecoveryFolder()
    {
        if (RecoveryFolder is null || !Directory.Exists(RecoveryFolder)) return;
        Process.Start(new ProcessStartInfo(ShellService.ExplorerPath, $"\"{RecoveryFolder}\"") { UseShellExecute = false });
    }

    [RelayCommand]
    private void ExportCsv()
    {
        var dialog = new SaveFileDialog { Title = "Export list as CSV", Filter = "CSV (*.csv)|*.csv", FileName = $"filehound-{CurrentTab.ToString().ToLowerInvariant()}-{DateTime.Now:yyyyMMdd-HHmm}.csv" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            using var w = new StreamWriter(dialog.FileName, false, new System.Text.UTF8Encoding(false));
            if (CurrentTab == RecoveryTab.Deleted && _session?.Log is { } log) CsvExport.WriteDeletions(w, log.Entries);
            else CsvExport.WriteCandidates(w, CurrentItems.Select(i => i.Candidate));
            _toast("Exported ✓");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _toast("Export failed: " + ex.Message); }
    }

    [RelayCommand]
    private void ExportDfxml()
    {
        if (_session is null) return;
        var dialog = new SaveFileDialog { Title = "Export recovery session as DFXML", Filter = "DFXML (*.xml)|*.xml", FileName = $"filehound-recovery-{DateTime.Now:yyyyMMdd-HHmm}.xml" };
        if (dialog.ShowDialog() != true) return;
        try { _session.ExportDfxml(dialog.FileName); _toast("Exported ✓"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _toast("Export failed: " + ex.Message); }
    }

    [RelayCommand]
    private void EnableTurbo() => _enableTurbo();

    [RelayCommand]
    private void DismissJournalBanner() => JournalBannerDismissed = true;

    /// <summary>Closes the session (resumes FileHound's writes to the drive). Called when the page is left.</summary>
    public void Deactivate()
    {
        Deleted.Cancel();
        Undelete.Cancel();
        CloseSession();
    }
}
