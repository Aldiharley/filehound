using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileHound.App.Services;
using Microsoft.Win32;

namespace FileHound.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Action _save;
    private readonly Func<HotkeyGesture, bool> _applyHotkey;
    private readonly string _dataDirectory;

    public SettingsViewModel(AppSettings settings, Action save, Func<HotkeyGesture, bool> applyHotkey, string dataDirectory, bool hotkeyRegistered)
    {
        _settings = settings;
        _save = save;
        _applyHotkey = applyHotkey;
        _dataDirectory = dataDirectory;
        _startWithWindows = StartupService.IsEnabled();
        _startMinimized = settings.StartMinimized;
        _closeToTray = settings.CloseToTray;
        _fuzzy = settings.Fuzzy;
        _includeHidden = settings.IncludeHidden;
        _displayName = settings.DisplayName ?? "";
        _hotkeyText = settings.Hotkey.Replace("+", " + ");
        _hotkeyStatus = hotkeyRegistered ? "✓ Registered" : "⚠ In use by another app — pick another combination";
        foreach (var p in settings.ExcludedPaths) Excluded.Add(p);
    }

    public ObservableCollection<string> Excluded { get; } = [];
    public string Version { get; } = "FileHound " + (Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0");

    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _fuzzy;
    [ObservableProperty] private bool _includeHidden;
    [ObservableProperty] private string _displayName;
    [ObservableProperty] private string _hotkeyText;
    [ObservableProperty] private string _hotkeyStatus;
    [ObservableProperty] private bool _isCapturing;
    [ObservableProperty] private bool _excludesChanged;

    partial void OnStartWithWindowsChanged(bool value)
    {
        try { StartupService.SetEnabled(value); _settings.StartWithWindows = value; _save(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { Log.Warn($"Startup toggle failed: {ex.Message}"); }
    }

    partial void OnStartMinimizedChanged(bool value) { _settings.StartMinimized = value; _save(); }
    partial void OnCloseToTrayChanged(bool value) { _settings.CloseToTray = value; _save(); }
    partial void OnFuzzyChanged(bool value)
    {
        _settings.Fuzzy = value;
        _save();
        FuzzyChanged?.Invoke(value);
    }

    /// <summary>Keeps the Search page's Fuzzy chip in sync.</summary>
    public Action<bool>? FuzzyChanged { get; set; }

    /// <summary>Hint under the name box: what the greeting falls back to when the box is empty.</summary>
    public string DisplayNameHint { get; } = $"Used in the greeting. Leave empty to use your Windows name ({UserNames.Automatic()}).";

    partial void OnDisplayNameChanged(string value)
    {
        _settings.DisplayName = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        _save();
        DisplayNameChanged?.Invoke(_settings.DisplayName);
    }

    /// <summary>Lets the window refresh its greeting as the user types.</summary>
    public Action<string?>? DisplayNameChanged { get; set; }

    /// <summary>Pushes the excluded-folder list to the indexer (applied on the next scan).</summary>
    public Action<IReadOnlyList<string>>? ExclusionsChanged { get; set; }
    partial void OnIncludeHiddenChanged(bool value) { _settings.IncludeHidden = value; _save(); }

    /// <summary>Called by the view while capturing; returns true when a valid combination was recorded.</summary>
    public bool TryCaptureHotkey(System.Windows.Input.ModifierKeys modifiers, System.Windows.Input.Key key)
    {
        if (HotkeyGesture.IsModifierKey(key) || modifiers == System.Windows.Input.ModifierKeys.None) return false;
        var gesture = new HotkeyGesture(modifiers, key);
        IsCapturing = false;
        HotkeyText = gesture.ToString().Replace("+", " + ");
        bool ok = _applyHotkey(gesture);
        if (ok)
        {
            _settings.Hotkey = gesture.ToString();
            _save();
            HotkeyStatus = "✓ Registered";
        }
        else
        {
            // Keep the previous hotkey working rather than leaving none registered.
            bool restored = HotkeyGesture.TryParse(_settings.Hotkey, out var previous) && _applyHotkey(previous);
            HotkeyStatus = restored
                ? $"⚠ {gesture} is in use by another app — kept {_settings.Hotkey}"
                : "⚠ In use by another app — pick another combination";
            if (restored) HotkeyText = _settings.Hotkey.Replace("+", " + ");
        }
        return true;
    }

    /// <summary>Unregisters the current global hotkey (so pressing it again can be captured).</summary>
    public Action? SuspendHotkey { get; set; }

    [RelayCommand]
    private void BeginCapture()
    {
        SuspendHotkey?.Invoke();
        IsCapturing = true;
        HotkeyText = "Press a key combination…";
    }

    /// <summary>Escape while capturing: restore the previous hotkey.</summary>
    public void CancelCapture()
    {
        IsCapturing = false;
        HotkeyText = _settings.Hotkey.Replace("+", " + ");
        if (HotkeyGesture.TryParse(_settings.Hotkey, out var g))
            HotkeyStatus = _applyHotkey(g) ? "✓ Registered" : "⚠ In use by another app — pick another combination";
    }

    [RelayCommand]
    private void AddExcluded()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder FileHound should skip" };
        if (dialog.ShowDialog() != true) return;
        var path = dialog.FolderName.TrimEnd('\\');
        if (Excluded.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        Excluded.Add(path);
        SaveExclusions();
    }

    [RelayCommand]
    private void RemoveExcluded(string path)
    {
        Excluded.Remove(path);
        SaveExclusions();
    }

    private void SaveExclusions()
    {
        _settings.ExcludedPaths = Excluded.ToList();
        _save();
        ExclusionsChanged?.Invoke(_settings.ExcludedPaths);
        ExcludesChanged = true;
    }

    [RelayCommand]
    private void ClearRecent()
    {
        _settings.RecentSearches.Clear();
        _save();
    }

    [RelayCommand]
    private void OpenLogs() => OpenFolder(Log.Directory);

    [RelayCommand]
    private void OpenIndexFolder() => OpenFolder(Path.Combine(_dataDirectory, "index"));

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(ShellService.ExplorerPath, $"\"{path}\"") { UseShellExecute = false });
    }
}
