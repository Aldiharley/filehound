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
    private CancellationTokenSource? _checkCts;
    private readonly Func<char, long, SlotState> _slotCheck;

    public DeletedTabViewModel(Func<char, long, SlotState>? slotCheck = null)
    {
        _slotCheck = slotCheck ?? ((letter, rec) => JournalGapOracle.Check(letter, rec) switch
        {
            JournalGapOracle.SlotState.Free => SlotState.Free,
            JournalGapOracle.SlotState.Reused => SlotState.Reused,
            _ => SlotState.Unknown,
        });
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
        _session = session;
        _checkCts?.Cancel();
        if (session?.Log is null) { IsAvailable = false; _all = []; Apply(); return; }
        IsAvailable = true;
        IsLoading = true;
        try
        {
            await session.Log.BackfillAsync(CancellationToken.None);
            Reload();
        }
        finally { IsLoading = false; }
    }

    /// <summary>Rebuilds the list from the log (also called when the log reports changes).</summary>
    public void Reload()
    {
        if (_session?.Log is null) return;
        var now = DateTime.UtcNow;
        var entries = _session.Log.Entries;
        var items = entries.Select(e => new RecoveryItem(ToCandidate(e), now)).ToList();
        foreach (var it in items) it.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(RecoveryItem.IsSelected)) SelectionChanged?.Invoke(this, EventArgs.Empty); };
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

    /// <summary>Computes slot states off the UI thread, 200 rows at a time, visible rows first.</summary>
    private async Task CheckSlotsAsync()
    {
        _checkCts?.Cancel();
        var cts = _checkCts = new CancellationTokenSource();
        var session = _session;
        if (session is null) return;
        var pending = Items.Concat(_all).Distinct().Where(i => i.Candidate.Key is DeletionLogItem { Slot: SlotState.Pending, Kind: DeletionKind.Deleted }).ToList();
        for (int start = 0; start < pending.Count && !cts.IsCancellationRequested; start += 200)
        {
            var batch = pending.Skip(start).Take(200).ToList();
            await Task.Run(() =>
            {
                foreach (var it in batch)
                {
                    if (cts.IsCancellationRequested) return;
                    var d = (DeletionLogItem)it.Candidate.Key;
                    d.Slot = _slotCheck(session.Drive.Letter, d.Entry.RecordNo);
                }
            }, cts.Token);
            foreach (var it in batch)
            {
                var (key, text, tip) = RecoveryItem.Describe(it.Candidate);
                it.GradeKey = key; it.GradeText = text; it.GradeTooltip = tip;
            }
        }
    }

    public IReadOnlyList<RecoveryItem> Selected => _all.Where(i => i.IsSelected).ToList();

    [RelayCommand]
    private void CopyOriginalPath(RecoveryItem? item)
    {
        if (item is null) return;
        try { Clipboard.SetText(item.Candidate.OriginalPath); } catch (System.Runtime.InteropServices.COMException) { }
    }
}
