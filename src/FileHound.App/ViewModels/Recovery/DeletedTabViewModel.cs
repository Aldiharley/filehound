using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileHound.Core.Recovery;
using FileHound.Indexing.Recovery;

namespace FileHound.App.ViewModels.Recovery;

public enum DeletedSort { Newest, Name, Size }

/// <summary>The Recently deleted tab: the USN deletion log with a noise filter and a lazy "slot still free?" check.</summary>
public sealed partial class DeletedTabViewModel : ObservableObject
{
    /// <summary>FR-10: things nobody wants back, hidden unless the toggle is off.</summary>
    internal static readonly string[] NoiseExtensions = [".tmp", ".etl", ".log", ".part", ".crdownload", ".lock", ".pf"];
    internal static readonly string[] NoisePathParts = [@"\AppData\Local\Temp\", @"\$Recycle.Bin\", @"\System Volume Information\", @"\$Extend\", @"\Cache\", @"\Code Cache\", @"\GPUCache\", @"\INetCache\", @"\WebCache\", @"\Temp\"];

    private RecoverySession? _session;
    private List<RecoveryItem> _all = [];
    private CancellationTokenSource? _loadCts;
    private readonly Func<RecoverySession, IReadOnlyList<long>, SlotState[]> _slotCheck;

    /// <param name="slotCheck">Checks a batch of MFT record numbers; the default asks the session (raw record read, FSCTL fallback).</param>
    public DeletedTabViewModel(Func<RecoverySession, IReadOnlyList<long>, SlotState[]>? slotCheck = null)
    {
        _slotCheck = slotCheck ?? CheckSlots;
    }

    private static SlotState[] CheckSlots(RecoverySession session, IReadOnlyList<long> records) =>
        session.CheckSlots(records).Select(s => s switch
        {
            JournalGapOracle.SlotState.Free => SlotState.Free,
            JournalGapOracle.SlotState.Reused => SlotState.Reused,
            _ => SlotState.Unknown,
        }).ToArray();

    /// <summary>Stops the backfill and slot checks in flight (drive change, leaving the page).</summary>
    public void Cancel()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
    }

    public ObservableCollection<RecoveryItem> Items { get; } = [];

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _hideNoise = true;
    [ObservableProperty] private DeletedSort _sort = DeletedSort.Newest;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private string _journalSpanText = "";
    /// <summary>True when the only thing missing is administrator access (the card offers Enable Turbo).</summary>
    [ObservableProperty] private bool _needsTurbo;
    [ObservableProperty] private string _unavailableTitle = "";
    [ObservableProperty] private string _unavailableText = "";

    public event EventHandler? SelectionChanged;

    partial void OnFilterChanged(string value) => Apply();
    partial void OnHideNoiseChanged(bool value) => Apply();
    partial void OnSortChanged(DeletedSort value) => Apply();

    public static bool IsNoise(DeletionEntry e)
    {
        if (e.IsDirectory) return false;
        if (e.Name.StartsWith("~$", StringComparison.Ordinal)) return true;
        var ext = Path.GetExtension(e.Name);
        if (NoiseExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return true;
        var full = e.ParentPath.EndsWith('\\') ? e.ParentPath : e.ParentPath + "\\";
        foreach (var part in NoisePathParts) if (full.Contains(part, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public async Task LoadAsync(RecoverySession? session)
    {
        Cancel();
        _session = session;
        _all = [];
        if (session?.Log is null) { IsAvailable = false; Explain(session); Apply(); return; }
        IsAvailable = true;
        IsLoading = true;
        var cts = _loadCts = new CancellationTokenSource();
        try
        {
            await session.Log.BackfillAsync(cts.Token);
            if (!cts.IsCancellationRequested) Reload();
        }
        catch (OperationCanceledException) { }
        finally { IsLoading = false; }
    }

    /// <summary>Why there is no deletion log for this drive right now.</summary>
    private void Explain(RecoverySession? session)
    {
        if (session is null) { NeedsTurbo = false; UnavailableTitle = ""; UnavailableText = ""; return; }
        (NeedsTurbo, UnavailableTitle, UnavailableText) = Describe(session.IsElevated, session.Drive.Letter, session.State);
    }

    /// <summary>The three reasons a drive has no deletion log, in the order they are checked.</summary>
    internal static (bool NeedsTurbo, string Title, string Text) Describe(bool elevated, char letter, DriveState? state)
    {
        if (!elevated)
            return (true, "Needs administrator access",
                "Recently deleted comes from the drive's change journal, which FileHound reads in Turbo mode. Enable Turbo (one administrator approval) and the log fills from the journal's history.");
        if (state is { Status: DriveStatus.Loading or DriveStatus.Scanning or DriveStatus.FillingDetails })
            return (false, $"Still indexing {letter}:",
                $"The deletion log starts as soon as Turbo indexing of {letter}: finishes ({state.Progress:P0} so far). This tab fills in by itself.");
        if (state is { Mode: IndexMode.Standard })
            return (false, $"{letter}: is indexed in Standard mode", state.Error is { Length: > 0 } e
                ? $"Turbo could not read this drive's change journal ({e}), so there is no deletion log for it. Undelete and Deep scan still work."
                : "This drive has no usable change journal, so there is no deletion log for it. Undelete and Deep scan still work.");
        return (false, $"No deletion log for {letter}: yet", "The log appears once the drive is indexed in Turbo mode.");
    }

    /// <summary>Rebuilds the list from the log (also called when the log reports changes).</summary>
    public void Reload()
    {
        if (_session?.Log is null) return;
        var now = DateTime.UtcNow;
        var entries = _session.Log.Entries;
        // Keep what the user already has: ticks and finished slot checks survive a reload of the same entries.
        var previous = _all.ToDictionary(i => Key(((DeletionLogItem)i.Candidate.Key).Entry), i => i);
        var items = new List<RecoveryItem>(entries.Count);
        foreach (var e in entries)
        {
            if (previous.TryGetValue(Key(e), out var old) && ((DeletionLogItem)old.Candidate.Key).Entry.Kind == e.Kind) { items.Add(old); continue; }
            var it = new RecoveryItem(ToCandidate(e), now);
            it.PropertyChanged += (_, a) => { if (a.PropertyName == nameof(RecoveryItem.IsSelected)) SelectionChanged?.Invoke(this, EventArgs.Empty); };
            items.Add(it);
        }
        _all = items;
        if (entries.Count > 0)
        {
            var oldest = new DateTime(entries[^1].DeletedUtcTicks, DateTimeKind.Utc);
            var span = now - oldest;
            JournalSpanText = span.TotalDays >= 2 ? $"the last {(int)span.TotalDays} days" : span.TotalHours >= 2 ? $"the last {(int)span.TotalHours} hours" : "the last hour or so";
        }
        Apply();
        _ = CheckSlotsAsync();
    }

    private static (long, ushort, long) Key(DeletionEntry e) => (e.RecordNo, e.Sequence, e.Usn);

    public static RecoveryCandidate ToCandidate(DeletionEntry e) => new(
        RecoverySource.DeletionLog, e.Name, e.ParentPath.Length == 0 ? null : e.ParentPath, e.Size,
        e.ModifiedUtcTicks > 0 ? new DateTime(e.ModifiedUtcTicks, DateTimeKind.Utc) : null,
        e.DeletedUtcTicks > 0 ? new DateTime(e.DeletedUtcTicks, DateTimeKind.Utc) : null,
        RecoveryGrade.Unknown, 0, e.IsDirectory, null, new DeletionLogItem(e));

    private void Apply()
    {
        var f = Filter.Trim();
        IEnumerable<RecoveryItem> shown = _all;
        if (HideNoise) shown = shown.Where(i => i.Candidate.Key is DeletionLogItem d && d.Kind != DeletionKind.Replaced && !IsNoise(d.Entry));
        if (!string.IsNullOrEmpty(f)) shown = shown.Where(i => i.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || i.Folder.Contains(f, StringComparison.OrdinalIgnoreCase));
        shown = Sort switch
        {
            DeletedSort.Name => shown.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            DeletedSort.Size => shown.OrderByDescending(i => i.Candidate.Size),
            _ => shown.OrderByDescending(i => i.Candidate.DeletedUtc ?? DateTime.MinValue),
        };
        Items.Clear();
        foreach (var i in shown) Items.Add(i);
        Count = _all.Count;
        IsEmpty = _all.Count == 0;
    }

    private bool _checking;

    /// <summary>Computes slot states off the UI thread, 200 rows per volume handle, visible rows first.</summary>
    private async Task CheckSlotsAsync()
    {
        if (_checking) return; // a running pass picks up newly pending rows on its next batch
        var session = _session;
        var cts = _loadCts;
        if (session is null || cts is null) return;
        _checking = true;
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var batch = Items.Concat(_all).Distinct().Where(i => i.Candidate.Key is DeletionLogItem { Slot: SlotState.Pending, Kind: DeletionKind.Deleted }).Take(200).ToList();
                if (batch.Count == 0) break;
                var records = batch.Select(i => ((DeletionLogItem)i.Candidate.Key).Entry.RecordNo).ToList();
                SlotState[] states;
                try { states = await Task.Run(() => _slotCheck(session, records)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { break; }
                if (cts.IsCancellationRequested) break;
                for (int i = 0; i < batch.Count; i++)
                {
                    var it = batch[i];
                    ((DeletionLogItem)it.Candidate.Key).Slot = states[i];
                    var (key, text, tip) = RecoveryItem.Describe(it.Candidate);
                    it.GradeKey = key; it.GradeText = text; it.GradeTooltip = tip;
                }
            }
        }
        finally { _checking = false; }
    }

    public IReadOnlyList<RecoveryItem> Selected => _all.Where(i => i.IsSelected).ToList();

    [RelayCommand]
    private void CopyOriginalPath(RecoveryItem? item)
    {
        if (item is null) return;
        try { Clipboard.SetText(item.Candidate.OriginalPath); } catch (System.Runtime.InteropServices.COMException) { }
    }
}
