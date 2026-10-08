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

public enum RecoveryTab { RecycleBin, Deleted }

/// <summary>The Recovery page: a drive, a source tab, a selection, a destination, and one Recover/Restore action.</summary>
public sealed partial class RecoveryViewModel : ObservableObject
{
    private readonly IndexManager _manager;
    private readonly Action<string> _toast;
    private readonly Action _enableTurbo;
    private RecoverySession? _session;
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
        RecycleBin.SelectionChanged += (_, _) => UpdateSelection();
        Deleted.SelectionChanged += (_, _) => UpdateSelection();
        _manager.StateChanged += (_, _) => Application.Current?.Dispatcher.InvokeAsync(RefreshDrives);
    }

    public bool IsElevated { get; }
    public RecycleBinTabViewModel RecycleBin { get; }
    public DeletedTabViewModel Deleted { get; }
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

    /// <summary>Restore puts Recycle Bin items back in place; Recover copies to the destination (different drive).</summary>
    public bool IsRestoreTab => CurrentTab == RecoveryTab.RecycleBin;

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
        _session?.Dispose();
        _session = null;
        HasSession = false;
        if (drive is null) return;
        var descriptor = _manager.Drives.FirstOrDefault(s => s.Drive.Letter == drive.Letter)?.Drive;
        if (descriptor is null) return;
        _session = _sessionFactory(_manager, descriptor);
        HasSession = true;
        RecoveryFolder = null;
        if (_session.Log is { } log) log.Changed += (_, _) => Application.Current?.Dispatcher.InvokeAsync(Deleted.Reload);
        await Task.WhenAll(RecycleBin.LoadAsync(_session), Deleted.LoadAsync(_session));
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var selected = CurrentTab == RecoveryTab.RecycleBin ? RecycleBin.Selected : Deleted.Selected;
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
    }

    [RelayCommand]
    private void PickDestination()
    {
        var dialog = new OpenFolderDialog { Title = "Where should recovered files go? (a different drive from the one you're recovering from)" };
        if (dialog.ShowDialog() == true) DestinationFolder = dialog.FolderName;
    }

    [RelayCommand]
    private async Task Recover()
    {
        if (_session is null || !CanRecover) return;
        var items = (CurrentTab == RecoveryTab.RecycleBin ? RecycleBin.Selected : Deleted.Selected).ToList();
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
                    if (IsRestoreTab)
                    {
                        string path = await _session.RestoreAsync(it.Candidate, keepBoth: true);
                        it.ApplyRestore(path);
                        ok++;
                    }
                    else
                    {
                        var r = await _session.RecoverAsync(it.Candidate, DestinationFolder!, CancellationToken.None);
                        it.ApplyOutcome(r);
                        if (r.Succeeded) { ok++; bytes += r.Bytes; }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
                {
                    it.Status = "Failed — " + ex.Message;
                    it.StatusKey = "Failed";
                    Log.Warn($"Recovery failed for {it.Candidate.OriginalPath}: {ex.Message}");
                }
                done++;
            }
            Progress = 1;
            RecoveryFolder = _session.RecoveryFolder;
            if (IsRestoreTab) _toast(ok == items.Count ? $"{ok} item{(ok == 1 ? "" : "s")} restored" : $"{ok} of {items.Count} restored — see the list for details");
            else _toast(ok == items.Count ? $"{ok} file{(ok == 1 ? "" : "s")} recovered ({Formatting.Size(bytes)}) in {sw.Elapsed.TotalSeconds:F0}s" : $"{ok} of {items.Count} recovered — see the list for details");
            if (IsRestoreTab) await RecycleBin.LoadAsync(_session);
        }
        finally
        {
            IsBusy = false;
            ProgressText = "";
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
            else CsvExport.WriteCandidates(w, (CurrentTab == RecoveryTab.RecycleBin ? RecycleBin.Items : Deleted.Items).Select(i => i.Candidate));
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
        _session?.Dispose();
        _session = null;
        HasSession = false;
    }
}
