using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace FileHound.App.Services;

/// <summary>"Start with Windows" via the per-user Run key.</summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FileHound";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --minimized");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

public static class ElevationService
{
    /// <summary>Starts an elevated copy of FileHound. Returns false if the user cancelled the UAC prompt.</summary>
    public static bool RelaunchElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"--turbo --after {Environment.ProcessId}",
            });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Info("Turbo relaunch cancelled at the UAC prompt");
            return false;
        }
    }
}

public static class Formatting
{
    private static readonly string[] s_units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Size(long bytes)
    {
        if (bytes < 0) return "—";
        if (bytes < 1024) return $"{bytes} B";
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < s_units.Length - 1) { v /= 1024; u++; }
        return v >= 100 ? $"{v:F0} {s_units[u]}" : $"{v:F1} {s_units[u]}";
    }

    public static string Modified(long utcTicks, DateTime nowUtc)
    {
        if (utcTicks <= 0) return "—";
        var when = new DateTime(utcTicks, DateTimeKind.Utc);
        var age = nowUtc - when;
        if (age < TimeSpan.Zero) return when.ToLocalTime().ToString("yyyy-MM-dd");
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} h ago";
        if (age < TimeSpan.FromDays(7)) return $"{(int)age.TotalDays} d ago";
        return when.ToLocalTime().ToString("yyyy-MM-dd");
    }

    public static string Count(long n) => n.ToString("N0");
}
