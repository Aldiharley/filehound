using System.IO;
using System.Windows.Input;
using FileHound.App.Services;

namespace FileHound.App.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fh-settings-").FullName;
    private string PathFor(string name) => System.IO.Path.Combine(_dir, name);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var s = new SettingsService(PathFor("none.json")).Load();
        Assert.Equal(AppSettings.DefaultHotkey, s.Hotkey);
        Assert.True(s.Fuzzy);
        Assert.True(s.CloseToTray);
    }

    [Fact]
    public void Round_trip()
    {
        var svc = new SettingsService(PathFor("settings.json"));
        var s = new AppSettings { Hotkey = "Ctrl+Shift+Space", Fuzzy = false, ExcludedPaths = [@"D:\games"] };
        s.AddRecent("invoice");
        svc.Save(s);
        var loaded = svc.Load();
        Assert.Equal("Ctrl+Shift+Space", loaded.Hotkey);
        Assert.False(loaded.Fuzzy);
        Assert.Equal([@"D:\games"], loaded.ExcludedPaths);
        Assert.Equal(["invoice"], loaded.RecentSearches);
    }

    [Fact]
    public void Malformed_file_is_set_aside()
    {
        var path = PathFor("settings.json");
        File.WriteAllText(path, "{ not json");
        var s = new SettingsService(path).Load();
        Assert.Equal(AppSettings.DefaultHotkey, s.Hotkey);
        Assert.True(File.Exists(PathFor("settings.bad.json")));
    }

    [Fact]
    public void Recent_searches_dedupe_and_cap()
    {
        var s = new AppSettings();
        for (int i = 0; i < 20; i++) s.AddRecent($"q{i}");
        s.AddRecent("Q5");
        Assert.Equal(12, s.RecentSearches.Count);
        Assert.Equal("Q5", s.RecentSearches[0]);
        Assert.DoesNotContain("q5", s.RecentSearches);
        s.AddRecent(" a ");
        Assert.Equal("Q5", s.RecentSearches[0]); // too short, ignored
    }
}

public class FormattingTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(10L * 1024 * 1024, "10.0 MB")]
    [InlineData(250L * 1024 * 1024 * 1024, "250 GB")]
    [InlineData(-1, "—")]
    public void Size(long bytes, string expected) => Assert.Equal(expected, Formatting.Size(bytes));

    [Fact]
    public void Modified_relative_and_absolute()
    {
        var now = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("just now", Formatting.Modified(now.AddSeconds(-10).Ticks, now));
        Assert.Equal("5 min ago", Formatting.Modified(now.AddMinutes(-5).Ticks, now));
        Assert.Equal("3 h ago", Formatting.Modified(now.AddHours(-3).Ticks, now));
        Assert.Equal("2 d ago", Formatting.Modified(now.AddDays(-2).Ticks, now));
        Assert.Equal(now.AddDays(-30).ToLocalTime().ToString("yyyy-MM-dd"), Formatting.Modified(now.AddDays(-30).Ticks, now));
        Assert.Equal("—", Formatting.Modified(0, now));
    }
}

public class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt+Space", ModifierKeys.Control | ModifierKeys.Alt, Key.Space)]
    [InlineData("ctrl + shift + F12", ModifierKeys.Control | ModifierKeys.Shift, Key.F12)]
    [InlineData("Win+Alt+5", ModifierKeys.Windows | ModifierKeys.Alt, Key.D5)]
    public void Parses(string text, ModifierKeys mods, Key key)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var g));
        Assert.Equal(mods, g.Modifiers);
        Assert.Equal(key, g.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Space")]           // no modifier
    [InlineData("Ctrl+Alt")]        // no key
    [InlineData("Ctrl+Banana")]
    [InlineData("Ctrl+A+B")]
    public void Rejects(string text) => Assert.False(HotkeyGesture.TryParse(text, out _));

    [Fact]
    public void Round_trips_to_canonical_text()
    {
        Assert.True(HotkeyGesture.TryParse("alt+CTRL+space", out var g));
        Assert.Equal("Ctrl+Alt+Space", g.ToString());
        Assert.True(HotkeyGesture.TryParse("Ctrl+Shift+7", out g));
        Assert.Equal("Ctrl+Shift+7", g.ToString());
    }

    [Fact]
    public void Fallback_hotkeys_all_parse()
    {
        foreach (var h in AppSettings.FallbackHotkeys.Prepend(AppSettings.DefaultHotkey))
            Assert.True(HotkeyGesture.TryParse(h, out _), h);
    }
}

public class UserNamesTests
{
    [Theory]
    [InlineData("Dennis Davison", "aldih", "Dennis")]
    [InlineData("Davison, Dennis", "aldih", "Dennis")]
    [InlineData(@"CORP\Dennis Davison", "aldih", "Dennis")]
    [InlineData(null, "aldih", "Aldih")]
    [InlineData("", "dennis.davison", "Dennis")]
    [InlineData("   ", @"CORP\jdoe", "Jdoe")]
    [InlineData(null, "", "there")]
    public void Falls_back_from_windows_display_name_to_account_name(string? display, string account, string expected)
        => Assert.Equal(expected, UserNames.Pick(null, display, account));

    [Fact]
    public void A_name_typed_in_settings_wins_as_typed()
    {
        Assert.Equal("Boss", UserNames.Pick("  Boss ", "Dennis Davison", "aldih"));
        Assert.Equal("Dennis", UserNames.Pick("   ", "Dennis Davison", "aldih")); // blanks mean "automatic"
    }

    [Fact]
    public void Resolve_always_gives_something_to_say() => Assert.False(string.IsNullOrWhiteSpace(UserNames.Resolve(null)));
}

public class ShellServiceTests
{
    [Fact]
    public void Explorer_is_launched_by_its_full_path_in_the_Windows_directory()
    {
        // A bare "explorer.exe" would be resolved from the app or current directory first, which matters when elevated.
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.True(System.IO.Path.IsPathRooted(ShellService.ExplorerPath));
        Assert.StartsWith(windows, ShellService.ExplorerPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(ShellService.ExplorerPath));
    }

    [Fact]
    public void Elevated_relaunch_waits_longer_than_the_previous_instance_can_take_to_shut_down()
    {
        // ExitApp allows 20 s for snapshot saving plus settings and log flushes; the wait must cover all of it.
        Assert.True(SingleInstance.PreviousInstanceExitTimeout >= TimeSpan.FromSeconds(25));
    }
}
