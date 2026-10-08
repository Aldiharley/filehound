using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using FileHound.Indexing.Interop;

namespace FileHound.Indexing.Recovery;

/// <summary>A Volume Shadow Copy as seen from the live system.</summary>
public sealed record ShadowCopy(string Id, string Device, DateTime CreatedUtc, string VolumeName, char? Letter);

/// <summary>
/// Enumerates and creates snapshots (FR-17, FR-20). WMI <c>Win32_ShadowCopy</c> first; <c>vssadmin list shadows</c>
/// as the fallback for enumeration. Both need elevation; without it there are simply no snapshots to show.
/// </summary>
public static class ShadowCopies
{
    /// <summary>Snapshots of the drive, newest first; empty when there are none or the process is not elevated.</summary>
    public static IReadOnlyList<ShadowCopy> List(char letter)
    {
        if (!Elevation.IsElevated) return [];
        letter = char.ToUpperInvariant(letter);
        IReadOnlyList<ShadowCopy> all;
        try { all = ListViaWmi(letter); }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or TypeInitializationException or PlatformNotSupportedException or InvalidOperationException)
        {
            IndexManager.Log?.Invoke($"Win32_ShadowCopy query failed ({ex.GetType().Name}: {ex.Message}); using vssadmin");
            all = ParseVssadmin(RunVssadmin("list shadows"));
        }
        return all.Where(s => s.Letter == letter).OrderByDescending(s => s.CreatedUtc).ToList();
    }

    private static List<ShadowCopy> ListViaWmi(char letter)
    {
        string? guid = Kernel32.VolumeGuidPathOf($"{letter}:\\");
        var result = new List<ShadowCopy>();
        using var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT ID, DeviceObject, InstallDate, VolumeName FROM Win32_ShadowCopy");
        foreach (var o in searcher.Get())
        {
            string id = o["ID"] as string ?? "";
            string device = o["DeviceObject"] as string ?? "";
            string volume = o["VolumeName"] as string ?? "";
            DateTime created = DateTime.MinValue;
            if (o["InstallDate"] is string s)
            {
                try { created = ManagementDateTimeConverter.ToDateTime(s).ToUniversalTime(); }
                catch (ArgumentOutOfRangeException) { }
            }
            char? l = guid is not null && string.Equals(volume, guid, StringComparison.OrdinalIgnoreCase) ? letter : null;
            if (device.Length > 0) result.Add(new ShadowCopy(id, device, created, volume, l));
        }
        return result;
    }

    /// <summary>Parses <c>vssadmin list shadows</c> output (pure; tested). Dates are printed in the tool's culture — the current one by default.</summary>
    internal static IReadOnlyList<ShadowCopy> ParseVssadmin(string output, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var result = new List<ShadowCopy>();
        DateTime created = DateTime.MinValue;
        string id = "", volume = "";
        char? letter = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (TryAfter(line, "at creation time:", out var when))
            {
                created = DateTime.TryParse(when, culture, DateTimeStyles.AssumeLocal, out var t1) ? t1.ToUniversalTime()
                    : DateTime.TryParse(when, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t2) ? t2.ToUniversalTime()
                    : DateTime.MinValue;
            }
            else if (TryAfter(line, "Shadow Copy ID:", out var v)) id = v;
            else if (TryAfter(line, "Original Volume:", out var ov))
            {
                // "(C:)\\?\Volume{...}\"
                int close = ov.IndexOf(')');
                letter = ov.Length >= 3 && ov[0] == '(' && ov[2] == ':' ? char.ToUpperInvariant(ov[1]) : null;
                volume = close >= 0 ? ov[(close + 1)..].Trim() : ov;
            }
            else if (TryAfter(line, "Shadow Copy Volume:", out var device))
            {
                result.Add(new ShadowCopy(id, device, created, volume, letter));
                id = ""; volume = ""; letter = null;
            }
        }
        return result;

        static bool TryAfter(string line, string prefix, out string rest)
        {
            int i = line.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            rest = i < 0 ? "" : line[(i + prefix.Length)..].Trim();
            return i >= 0;
        }
    }

    /// <summary>
    /// FR-20: creates a client-accessible snapshot of the drive (writes a diff area to it). Returns the new shadow ID,
    /// or null with the reason logged. Only called after the user confirmed the warning.
    /// </summary>
    public static string? Create(char letter)
    {
        try
        {
            using var cls = new ManagementClass(@"root\cimv2", "Win32_ShadowCopy", null);
            using var inParams = cls.GetMethodParameters("Create");
            inParams["Volume"] = $"{char.ToUpperInvariant(letter)}:\\";
            inParams["Context"] = "ClientAccessible";
            using var outParams = cls.InvokeMethod("Create", inParams, null);
            uint rc = Convert.ToUInt32(outParams["ReturnValue"], CultureInfo.InvariantCulture);
            if (rc != 0)
            {
                IndexManager.Log?.Invoke($"Win32_ShadowCopy.Create({letter}:) returned {rc}");
                return null;
            }
            return outParams["ShadowID"] as string;
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or TypeInitializationException or PlatformNotSupportedException or InvalidOperationException)
        {
            IndexManager.Log?.Invoke($"Win32_ShadowCopy.Create({letter}:) failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static string RunVssadmin(string args)
    {
        try
        {
            // stderr is not redirected: reading stdout to the end with a redirected, unread stderr can deadlock.
            var psi = new ProcessStartInfo("vssadmin", args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p is null) return "";
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15_000);
            return output;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return ""; }
    }
}
