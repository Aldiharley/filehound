using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileHound.Indexing.Recovery;

namespace FileHound.App.ViewModels.Recovery;

/// <summary>The Recycle Bin tab: every readable bin on the selected drive, with Restore (in place) and Recover-to.</summary>
public sealed partial class RecycleBinTabViewModel : ObservableObject
{
    private List<RecoveryItem> _all = [];

    public ObservableCollection<RecoveryItem> Items { get; } = [];

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private bool _showAllUsers;

    public event EventHandler? SelectionChanged;

    partial void OnFilterChanged(string value) => Apply();

    public async Task LoadAsync(RecoverySession? session)
    {
        if (session is null) { _all = []; Apply(); return; }
        IsLoading = true;
        try
        {
            var now = DateTime.UtcNow;
            var items = await Task.Run(() => session.RecycleBinCandidates(ShowAllUsers).Select(c => new RecoveryItem(c, now)).ToList());
            foreach (var it in items) it.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(RecoveryItem.IsSelected)) SelectionChanged?.Invoke(this, EventArgs.Empty); };
            _all = items.OrderByDescending(i => i.Candidate.DeletedUtc ?? DateTime.MinValue).ToList();
            Apply();
        }
        finally { IsLoading = false; }
    }

    private void Apply()
    {
        var f = Filter.Trim();
        var shown = string.IsNullOrEmpty(f) ? _all : _all.Where(i => i.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || i.Folder.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        Items.Clear();
        foreach (var i in shown) Items.Add(i);
        Count = _all.Count;
        IsEmpty = _all.Count == 0;
    }

    public IReadOnlyList<RecoveryItem> Selected => _all.Where(i => i.IsSelected).ToList();

    [RelayCommand]
    private void CopyOriginalPath(RecoveryItem? item)
    {
        if (item is null) return;
        try { Clipboard.SetText(item.Candidate.OriginalPath); } catch (System.Runtime.InteropServices.COMException) { }
    }
}
