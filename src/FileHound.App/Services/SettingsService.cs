using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileHound.App.Services;

public sealed class AppSettings
{
    public const string DefaultHotkey = "Ctrl+Alt+Space";
    public static readonly string[] FallbackHotkeys = ["Ctrl+Shift+Space", "Alt+Shift+Space", "Ctrl+Alt+F", "Ctrl+Shift+F12"];

    public string Hotkey { get; set; } = DefaultHotkey;
    public bool Fuzzy { get; set; } = true;
    public bool IncludeHidden { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool TrayHintShown { get; set; }
    public List<string> ExcludedPaths { get; set; } = [];
    public List<string> RecentSearches { get; set; } = [];

    public void AddRecent(string query, int max = 12)
    {
        query = query.Trim();
        if (query.Length < 2) return;
        RecentSearches.RemoveAll(q => string.Equals(q, query, StringComparison.OrdinalIgnoreCase));
        RecentSearches.Insert(0, query);
        if (RecentSearches.Count > max) RecentSearches.RemoveRange(max, RecentSearches.Count - max);
    }
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON (atomic writes, malformed files are set aside).</summary>
public sealed class SettingsService(string path)
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
    private readonly object _gate = new();

    public string Path { get; } = path;

    public AppSettings Load()
    {
        if (!File.Exists(Path)) return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), s_json) ?? new AppSettings();
        }
        catch (JsonException ex)
        {
            Log.Warn($"Settings file is malformed, using defaults: {ex.Message}");
            try { File.Move(Path, System.IO.Path.ChangeExtension(Path, ".bad.json"), overwrite: true); } catch (IOException) { }
            return new AppSettings();
        }
        catch (IOException ex)
        {
            Log.Warn($"Settings unreadable, using defaults: {ex.Message}");
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var tmp = Path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(settings, s_json));
                File.Move(tmp, Path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Could not save settings: {ex.Message}");
            }
        }
    }
}
