using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FileHound.App.ViewModels;

public sealed partial class DrivesViewModel : ObservableObject
{
    private readonly IndexManager _manager;
    private readonly Dictionary<char, DriveItem> _items = [];

    public DrivesViewModel(IndexManager manager, bool isElevated, Action enableTurbo)
    {
        _manager = manager;
        IsElevated = isElevated;
        EnableTurboCommand = new RelayCommand(enableTurbo);
        _manager.StateChanged += (_, _) => Application.Current?.Dispatcher.InvokeAsync(Refresh);
    }

    public ObservableCollection<DriveItem> Drives { get; } = [];
    public bool IsElevated { get; }
    public IRelayCommand EnableTurboCommand { get; }

    public void Refresh()
    {
        foreach (var s in _manager.Drives)
        {
            if (!_items.TryGetValue(s.Drive.Letter, out var item))
            {
                item = new DriveItem(s.Drive.Letter);
                _items[s.Drive.Letter] = item;
                Drives.Add(item);
            }
            item.Update(s);
        }
    }

    [RelayCommand]
    private Task Rescan(DriveItem? item) => item is null ? Task.CompletedTask : _manager.RescanAsync(item.Letter);

    [RelayCommand]
    private async Task RescanAll()
    {
        foreach (var d in Drives.ToList()) await _manager.RescanAsync(d.Letter);
    }
}
