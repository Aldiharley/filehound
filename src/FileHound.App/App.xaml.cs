using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FileHound.App.Services;
using FileHound.App.ViewModels;
using FileHound.App.Views;
using H.NotifyIcon;

namespace FileHound.App;

public partial class App : Application
{
    private SingleInstance? _single;
    private IndexManager? _manager;
    private SettingsService? _settingsService;
    private AppSettings _settings = new();
    private MainWindow? _window;
    private MainViewModel? _main;
    private HotkeyService? _hotkey;
    private TaskbarIcon? _tray;
    private bool _exiting;

    public static string DataDirectory { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileHound");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        bool minimized = args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        int? afterPid = null;
        int ai = Array.FindIndex(args, a => a.Equals("--after", StringComparison.OrdinalIgnoreCase));
        if (ai >= 0 && ai + 1 < args.Length && int.TryParse(args[ai + 1], out int pid)) afterPid = pid;
        // Dev/QA options: --data <dir> (isolated data folder), --drives C,D (limit indexing), --snapshot <dir> [--query q]
        string? Arg(string name)
        {
            int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        if (Arg("--data") is { } dataDir) DataDirectory = Path.GetFullPath(dataDir);
        var onlyDrives = Arg("--drives")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => char.ToUpperInvariant(s[0])).ToHashSet();
        string? snapshotDir = Arg("--snapshot");

        Log.Init(Path.Combine(DataDirectory, "logs"));
        DispatcherUnhandledException += OnDispatcherException;
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("Unobserved task exception", ex.Exception); ex.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("Fatal exception", ex.ExceptionObject as Exception);

        _single = SingleInstance.Acquire(afterPid);
        if (!_single.IsFirst)
        {
            if (_single.ElevatedInstanceRunning)
                MessageBox.Show("FileHound is already running with Turbo indexing (as administrator).\n\nUse its tray icon or hotkey to open it.",
                    "FileHound", MessageBoxButton.OK, MessageBoxImage.Information);
            _single.SignalFirstInstance();
            Shutdown();
            return;
        }

        bool elevated = Elevation.IsElevated;
        Log.Info($"FileHound starting (elevated={elevated}, args={string.Join(' ', args)})");
        _settingsService = new SettingsService(Path.Combine(DataDirectory, "settings.json"));
        _settings = _settingsService.Load();
        void Save() => _settingsService.Save(_settings);

        IndexManager.Log = Log.Info;
        _manager = new IndexManager(new IndexOptions(DataDirectory, _settings.ExcludedPaths,
            DriveSource: onlyDrives is null ? null : () => DriveDiscovery.GetDrives().Where(d => onlyDrives.Contains(d.Letter)).ToList()));
        var shell = new ShellService();
        ResultItem.IconProvider = new ShellIconProvider();
        var search = new SearchService(_manager);

        _hotkey = new HotkeyService();
        MainViewModel? main = null;
        var searchVm = new SearchViewModel(search, shell, _manager, _settings, Save, t => main?.ShowToast(t));
        var dashboard = new DashboardViewModel(_manager, _settings, Save, t => main?.BeginSearch(t), shell);
        var drives = new DrivesViewModel(_manager, elevated, () => RequestTurbo());
        bool hotkeyOk = false;
        var settingsVm = new SettingsViewModel(_settings, Save, g => _hotkey.Register(g), DataDirectory, hotkeyRegistered: true);
        main = new MainViewModel(_manager, elevated, UserNames.Resolve(_settings.DisplayName)) { Dashboard = dashboard, Search = searchVm, Drives = drives, Settings = settingsVm };
        main.TurboRequested += (_, _) => RequestTurbo();
        settingsVm.DisplayNameChanged = name => main.UserName = UserNames.Resolve(name);
        _main = main;

        _window = new MainWindow(main, _settings);
        _window.Closing += OnWindowClosing;
        _hotkey.Attach(_window);
        _hotkey.Pressed += (_, _) => ToggleWindow();
        if (HotkeyGesture.TryParse(_settings.Hotkey, out var gesture)) hotkeyOk = _hotkey.Register(gesture);
        if (!hotkeyOk && _settings.Hotkey == AppSettings.DefaultHotkey)
        {
            // The default is popular (other launchers use it too); fall back to the first free alternative.
            foreach (var candidate in AppSettings.FallbackHotkeys)
            {
                if (!HotkeyGesture.TryParse(candidate, out var g) || !_hotkey.Register(g)) continue;
                Log.Info($"Default hotkey {AppSettings.DefaultHotkey} is taken; using {candidate}");
                _settings.Hotkey = candidate;
                Save();
                settingsVm.HotkeyText = candidate.Replace("+", " + ");
                hotkeyOk = true;
                break;
            }
        }
        settingsVm.HotkeyStatus = hotkeyOk ? "✓ Registered" : "⚠ In use by another app — pick another combination";
        settingsVm.SuspendHotkey = () => _hotkey.Unregister();
        settingsVm.ExclusionsChanged = paths => _manager.SetExcludedPaths(paths);
        settingsVm.FuzzyChanged = on => searchVm.Fuzzy = on;   // property setters no-op when unchanged, so no loop
        searchVm.FuzzyChanged = on => settingsVm.Fuzzy = on;

        CreateTray();
        main.Initialize();
        if (!(minimized || _settings.StartMinimized)) ShowWindow();

        _single.ListenForActivation(() => Dispatcher.InvokeAsync(ShowWindow));
        _ = StartIndexingAsync();

        if (snapshotDir is not null)
        {
            ShowWindow();
            Dispatcher.InvokeAsync(async () =>
            {
                try { await SnapshotMode.RunAsync(_window, main, snapshotDir, Arg("--query") ?? "report", TimeSpan.FromSeconds(double.TryParse(Arg("--warmup"), out var s) ? s : 6)); }
                catch (Exception ex) { Log.Error("Snapshot mode failed", ex); }
                ExitApp();
            });
        }
    }

    private async Task StartIndexingAsync()
    {
        try
        {
            await _manager!.StartAsync();
            _main?.Dashboard.Activate();
        }
        catch (Exception ex)
        {
            Log.Error("Index manager failed to start", ex);
        }
    }

    private void CreateTray()
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, string glyph, Action click)
        {
            var mi = new MenuItem { Header = header, Icon = new TextBlock { Text = glyph } };
            mi.Click += (_, _) => click();
            return mi;
        }
        menu.Items.Add(Item("Show FileHound", "", ShowWindow));
        menu.Items.Add(Item("Rescan all drives", "", () => _ = _main!.Drives.RescanAllCommand.ExecuteAsync(null)));
        menu.Items.Add(Item("Settings", "", () => { ShowWindow(); _main!.CurrentPage = AppPage.Settings; }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Exit", "", ExitApp));

        _tray = new TaskbarIcon
        {
            ToolTipText = "FileHound — press " + _settings.Hotkey.Replace("+", " + "),
            IconSource = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico")),
            ContextMenu = menu,
            NoLeftClickDelay = true,
        };
        _tray.TrayLeftMouseUp += (_, _) => ShowWindow();
        _tray.ForceCreate(enablesEfficiencyMode: false);
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;  // reliably come to front when summoned from the hotkey
        _window.Topmost = false;
        _window.FocusSearchIfOnSearchPage();
    }

    private void ToggleWindow()
    {
        if (_window is null || _main is null) return;
        if (_window.IsVisible && _window.IsActive && _window.WindowState != WindowState.Minimized)
        {
            _window.Hide();
            return;
        }
        ShowWindow();
        _main.CurrentPage = AppPage.Search;
        _window.FocusSearch(selectAll: true);
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting || !_settings.CloseToTray) { if (!_exiting) { e.Cancel = true; ExitApp(); } return; }
        e.Cancel = true;
        _window!.Hide();
        if (!_settings.TrayHintShown)
        {
            _settings.TrayHintShown = true;
            _settingsService!.Save(_settings);
            _tray?.ShowNotification("FileHound is still sniffing 🐾", $"It keeps your index fresh in the tray. Press {_settings.Hotkey.Replace("+", " + ")} anytime.");
        }
    }

    private void RequestTurbo()
    {
        if (Elevation.IsElevated) return;
        if (!ElevationService.RelaunchElevated())
        {
            _main?.ShowToast("Turbo needs administrator approval — nothing changed");
            return;
        }
        // The instance lock is held until ExitApp has saved snapshots and settings (released by _single.Dispose()),
        // so the elevated copy, which waits for this pid, never overlaps with those writes.
        ExitApp();
    }

    private void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        Log.Info("Exiting");
        _window?.Hide();
        _hotkey?.Dispose();
        _tray?.Dispose();
        try
        {
            // Save snapshots so the next start is instant.
            Task.Run(async () => { if (_manager is not null) await _manager.DisposeAsync(); }).Wait(TimeSpan.FromSeconds(20));
        }
        catch (AggregateException ex) { Log.Error("Shutdown error", ex); }
        _settingsService?.Save(_settings);
        _window?.Close();
        _single?.Dispose();
        Log.FlushAsync().Wait(TimeSpan.FromSeconds(2));
        Shutdown();
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
        if (_window is null)
        {
            // Failed before the UI existed: a headless process would just linger in the background.
            MessageBox.Show($"FileHound couldn't start:\n\n{e.Exception.Message}\n\nDetails are in {Log.Directory}", "FileHound", MessageBoxButton.OK, MessageBoxImage.Error);
            _single?.Dispose();
            Shutdown(1);
            return;
        }
        _main?.ShowToast("Oops — something went wrong (details in the log)");
    }
}
