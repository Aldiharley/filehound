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
                char letter = char.ToUpperInvariant(d.Name[0]);
                // USB/SD disks often report DriveType.Fixed; treat them as removable so we never hold watch handles
                // that block "Safely remove hardware".
                bool removable = d.DriveType == DriveType.Removable || IsExternalBus(letter);
                list.Add(new DriveDescriptor(
                    letter, d.RootDirectory.FullName, d.DriveFormat, d.VolumeLabel,
                    d.TotalSize, d.AvailableFreeSpace, GetSerial(d.RootDirectory.FullName), removable));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Drive vanished or is locked (e.g. BitLocker): skip it.
            }
        }
        return list.OrderBy(d => d.Letter).ToList();
    }

    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const uint BusType1394 = 4, BusTypeUsb = 7, BusTypeSd = 12, BusTypeMmc = 13;

    /// <summary>True when the volume's disk sits on a hot-pluggable bus (USB, SD, MMC, FireWire). No admin needed.</summary>
    public static unsafe bool IsExternalBus(char letter)
    {
        using var h = Kernel32.CreateFile($@"\\.\{letter}:", 0, Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE, 0, Kernel32.OPEN_EXISTING, 0, 0);
        if (h.IsInvalid) return false;
        uint* query = stackalloc uint[3]; // STORAGE_PROPERTY_QUERY { StorageDeviceProperty, PropertyStandardQuery, pad }
        query[0] = 0; query[1] = 0; query[2] = 0;
        byte* desc = stackalloc byte[512]; // STORAGE_DEVICE_DESCRIPTOR; BusType at offset 28
        if (!Kernel32.DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, query, 12, desc, 512, out int returned, 0) || returned < 32) return false;
        uint bus = *(uint*)(desc + 28);
        return bus is BusTypeUsb or BusType1394 or BusTypeSd or BusTypeMmc;
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
