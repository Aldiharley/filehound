using System.Security.Principal;
using FileHound.Indexing.Interop;

namespace FileHound.Indexing;

public sealed record DriveDescriptor(char Letter, string Root, string Format, string Label, long TotalSize, long FreeSpace, uint Serial, bool IsRemovable)
{
    public bool IsNtfs => string.Equals(Format, "NTFS", StringComparison.OrdinalIgnoreCase);
    /// <summary>Stable identity across runs (drive letter + volume serial).</summary>
    public string Key => $"{Letter}_{Serial:X8}";
}

public static class DriveDiscovery
{
    /// <summary>Ready fixed and removable drives, ordered by letter.</summary>
    public static IReadOnlyList<DriveDescriptor> GetDrives()
    {
        var list = new List<DriveDescriptor>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable) || !d.IsReady) continue;
                list.Add(new DriveDescriptor(
                    char.ToUpperInvariant(d.Name[0]), d.RootDirectory.FullName, d.DriveFormat, d.VolumeLabel,
                    d.TotalSize, d.AvailableFreeSpace, GetSerial(d.RootDirectory.FullName), d.DriveType == DriveType.Removable));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Drive vanished or is locked (e.g. BitLocker): skip it.
            }
        }
        return list.OrderBy(d => d.Letter).ToList();
    }

    public static unsafe uint GetSerial(string root)
    {
        return Kernel32.GetVolumeInformation(root, null, 0, out uint serial, out _, out _, null, 0) ? serial : 0u;
    }
}

public static class Elevation
{
    private static readonly Lazy<bool> s_isElevated = new(() =>
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    });

    /// <summary>True when the process runs with an elevated (administrator) token.</summary>
    public static bool IsElevated => s_isElevated.Value;
}
