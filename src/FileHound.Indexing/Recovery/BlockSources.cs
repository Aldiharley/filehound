using System.Runtime.InteropServices;
using FileHound.Indexing.Interop;
using Microsoft.Win32.SafeHandles;

namespace FileHound.Indexing.Recovery;

public enum VolumeReadPath { Volume, PhysicalDisk, ShadowCopy, Memory }

/// <summary>Raw, read-only access to a volume's bytes. Offsets and lengths are 4 KB-aligned; the reader aligns for callers.</summary>
internal interface IBlockSource : IDisposable
{
    VolumeReadPath Path { get; }
    string Description { get; }
    /// <summary>Fills the whole span or throws <see cref="IOException"/>.</summary>
    void Read(long volumeOffset, Span<byte> dest);
}

/// <summary>A volume image in memory (tests).</summary>
internal sealed class MemoryBlockSource(byte[] image) : IBlockSource
{
    public VolumeReadPath Path => VolumeReadPath.Memory;
    public string Description => $"memory ({image.Length:N0} bytes)";

    public void Read(long volumeOffset, Span<byte> dest)
    {
        if (volumeOffset < 0 || volumeOffset + dest.Length > image.Length)
            throw new IOException($"Read of {dest.Length} bytes at {volumeOffset} is outside the image.");
        image.AsSpan((int)volumeOffset, dest.Length).CopyTo(dest);
    }

    public void Dispose() { }
}

/// <summary>
/// A Win32 handle plus a base offset: the volume itself (<c>\\.\X:</c>), the physical disk at the partition's start,
/// or a shadow-copy device. Reads go through a 4 KB-aligned native buffer so unbuffered handles accept them.
/// </summary>
internal sealed unsafe class HandleBlockSource : IBlockSource
{
    public const int Alignment = 4096;
    private readonly SafeFileHandle _handle;
    private readonly long _baseOffset;

    private HandleBlockSource(SafeFileHandle handle, long baseOffset, VolumeReadPath path, string description)
    {
        _handle = handle;
        _baseOffset = baseOffset;
        Path = path;
        Description = description;
    }

    public VolumeReadPath Path { get; }
    public string Description { get; }

    public void Read(long volumeOffset, Span<byte> dest)
    {
        if (volumeOffset % Alignment != 0 || dest.Length % Alignment != 0)
            throw new ArgumentException("Block reads must be 4 KB-aligned.");
        byte* buf = (byte*)NativeMemory.AlignedAlloc((nuint)dest.Length, Alignment);
        try
        {
            Kernel32.ReadExactly(_handle, _baseOffset + volumeOffset, buf, dest.Length);
            new ReadOnlySpan<byte>(buf, dest.Length).CopyTo(dest);
        }
        finally { NativeMemory.AlignedFree(buf); }
    }

    public void Dispose() => _handle.Dispose();

    /// <summary>The volume handle: fails on this PC's C: with Win32 error 50 (security software), works elsewhere.</summary>
    public static (HandleBlockSource? Source, string Reason) TryOpenVolume(char letter, long probeOffset)
    {
        string name = $@"\\.\{char.ToUpperInvariant(letter)}:";
        var h = Kernel32.CreateFile(name, Kernel32.GENERIC_READ, Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE, 0, Kernel32.OPEN_EXISTING, Kernel32.FILE_FLAG_BACKUP_SEMANTICS, 0);
        if (h.IsInvalid) return (null, $"{name}: open failed (Win32 error {Marshal.GetLastPInvokeError()})");
        var source = new HandleBlockSource(h, 0, VolumeReadPath.Volume, name);
        return Probe(source, probeOffset, expectFileRecord: true);
    }

    /// <summary>
    /// The physical disk at the partition offset (<c>IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS</c> on the control handle).
    /// Refuses volumes spanning several extents and BitLocker volumes (the sector reads as ciphertext, OEM id -FVE-FS-).
    /// </summary>
    public static (HandleBlockSource? Source, string Reason) TryOpenPhysicalDisk(SafeFileHandle control, long probeOffset)
    {
        byte* ext = stackalloc byte[8 + 24 * 4];
        if (!Kernel32.DeviceIoControl(control, Kernel32.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, null, 0, ext, 8 + 24 * 4, out _, 0))
            return (null, $"disk extents: query failed (Win32 error {Marshal.GetLastPInvokeError()})");
        uint count = *(uint*)ext;
        if (count != 1) return (null, $"disk extents: volume spans {count} extents");
        uint disk = *(uint*)(ext + 8);
        long partitionOffset = *(long*)(ext + 16);
        string name = $@"\\.\PhysicalDrive{disk}";
        var h = Kernel32.CreateFile(name, Kernel32.GENERIC_READ, Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE, 0, Kernel32.OPEN_EXISTING, Kernel32.FILE_FLAG_NO_BUFFERING, 0);
        if (h.IsInvalid) return (null, $"{name}: open failed (Win32 error {Marshal.GetLastPInvokeError()})");
        var source = new HandleBlockSource(h, partitionOffset, VolumeReadPath.PhysicalDisk, $"{name} @{partitionOffset}");
        // The partition's first sector must be plain NTFS.
        var boot = new byte[Alignment];
        try { source.Read(0, boot); }
        catch (IOException ex) { source.Dispose(); return (null, $"{name}: {ex.Message}"); }
        if (boot.AsSpan(3, 8).SequenceEqual("-FVE-FS-"u8)) { source.Dispose(); return (null, $"{name}: BitLocker ciphertext at the partition start"); }
        if (!boot.AsSpan(3, 4).SequenceEqual("NTFS"u8)) { source.Dispose(); return (null, $"{name}: no NTFS boot sector at the partition start"); }
        return Probe(source, probeOffset, expectFileRecord: true);
    }

    /// <summary>A shadow-copy device (<c>\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN</c>) opened as a block device.</summary>
    public static (HandleBlockSource? Source, string Reason) TryOpenShadow(string device, long probeOffset)
    {
        var h = Kernel32.CreateFile(device, Kernel32.GENERIC_READ, Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE, 0, Kernel32.OPEN_EXISTING, Kernel32.FILE_FLAG_BACKUP_SEMANTICS, 0);
        if (h.IsInvalid) return (null, $"{device}: open failed (Win32 error {Marshal.GetLastPInvokeError()})");
        return Probe(new HandleBlockSource(h, 0, VolumeReadPath.ShadowCopy, device), probeOffset, expectFileRecord: true);
    }

    private static (HandleBlockSource? Source, string Reason) Probe(HandleBlockSource source, long probeOffset, bool expectFileRecord)
    {
        var buf = new byte[Alignment];
        try { source.Read(probeOffset, buf); }
        catch (IOException ex) { source.Dispose(); return (null, $"{source.Description}: {ex.Message}"); }
        if (expectFileRecord && !buf.AsSpan(0, 4).SequenceEqual("FILE"u8))
        {
            source.Dispose();
            return (null, $"{source.Description}: no FILE record at the $MFT start");
        }
        return (source, "ok");
    }
}
