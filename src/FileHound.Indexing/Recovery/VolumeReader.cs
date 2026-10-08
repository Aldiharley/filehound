using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using FileHound.Indexing.Interop;
using FileHound.Indexing.Ntfs;
using Microsoft.Win32.SafeHandles;

namespace FileHound.Indexing.Recovery;

public sealed record VolumeGeometry(int BytesPerSector, int BytesPerCluster, int RecordSize, long TotalClusters, long MftStartLcn, long MftValidDataLength);

/// <summary>
/// The one way recovery code reads raw bytes from a volume. <see cref="Open"/> tries the volume handle, then the
/// physical disk at the partition offset, then the newest shadow-copy device, and keeps the first that can read the
/// start of the $MFT. There is no write API.
/// </summary>
public sealed class VolumeReader : IDisposable
{
    private readonly IBlockSource _source;
    private readonly List<DataRun> _mftExtents;

    internal VolumeReader(IBlockSource source, VolumeGeometry geometry, SafeFileHandle? control)
    {
        _source = source;
        ControlHandle = control;
        Geometry = geometry;
        // Record 0 describes the $MFT itself; its extents map record numbers to clusters.
        var record0 = new byte[geometry.RecordSize];
        ReadBytes(geometry.MftStartLcn * geometry.BytesPerCluster, record0);
        if (!MftRecordParser.ApplyFixups(record0) || !MftRecordParser.TryParse(record0, out var r) || !r.InUse)
            throw new InvalidDataException("MFT record 0 is unreadable");
        if (r.HasAttributeList) throw new NotSupportedException("$MFT has an attribute list (heavily fragmented MFT)");
        if (r.UnnamedDataRuns.IsEmpty) throw new InvalidDataException("$MFT data runs not found in record 0");
        _mftExtents = DataRuns.Decode(r.UnnamedDataRuns);
        if (geometry.MftValidDataLength <= 0) Geometry = geometry with { MftValidDataLength = r.Size };
    }

    public VolumeReadPath Path => _source.Path;
    public string PathDescription => _source.Description;
    public VolumeGeometry Geometry { get; }
    /// <summary><c>\\.\X:</c> for FSCTLs (opens even where raw reads are refused); null for an in-memory image.</summary>
    public SafeFileHandle? ControlHandle { get; }
    public IReadOnlyList<DataRun> MftExtents => _mftExtents;
    public long RecordCount => Geometry.MftValidDataLength / Geometry.RecordSize;

    /// <summary>Opens the first readable path to the drive. Throws <see cref="NotSupportedException"/> listing every failure.</summary>
    public static VolumeReader Open(DriveDescriptor drive, bool allowShadow = true)
    {
        var control = Kernel32.OpenVolume(drive.Letter);
        if (control.IsInvalid)
        {
            int err = Marshal.GetLastPInvokeError();
            control.Dispose();
            throw new NotSupportedException(err == 5 ? "Reading the drive needs administrator access (Turbo)." : $@"Cannot open \\.\{drive.Letter}: (Win32 error {err})");
        }
        VolumeGeometry geometry;
        try { geometry = QueryGeometry(control); }
        catch { control.Dispose(); throw; }
        long probe = geometry.MftStartLcn * geometry.BytesPerCluster;
        var reasons = new List<string>();

        var (source, reason) = HandleBlockSource.TryOpenVolume(drive.Letter, probe);
        reasons.Add(reason);
        if (source is null)
        {
            (source, reason) = HandleBlockSource.TryOpenPhysicalDisk(control, probe);
            reasons.Add(reason);
        }
        if (source is null && allowShadow)
        {
            var shadow = ShadowCopies.List(drive.Letter).FirstOrDefault();
            if (shadow is null) reasons.Add("shadow copies: none exist for this drive");
            else { (source, reason) = HandleBlockSource.TryOpenShadow(shadow.Device, probe); reasons.Add(reason); }
        }
        if (source is null)
        {
            control.Dispose();
            throw new NotSupportedException($"No readable path to {drive.Letter}: — " + string.Join("; ", reasons));
        }
        try { return new VolumeReader(source, geometry, control); }
        catch { source.Dispose(); control.Dispose(); throw; }
    }

    private static unsafe VolumeGeometry QueryGeometry(SafeFileHandle control)
    {
        byte* data = stackalloc byte[128];
        if (!Kernel32.DeviceIoControl(control, Kernel32.FSCTL_GET_NTFS_VOLUME_DATA, null, 0, data, 128, out _, 0))
            throw new NotSupportedException($"FSCTL_GET_NTFS_VOLUME_DATA failed (Win32 error {Marshal.GetLastPInvokeError()})");
        var g = new VolumeGeometry(
            BytesPerSector: (int)*(uint*)(data + 40),
            BytesPerCluster: (int)*(uint*)(data + 44),
            RecordSize: (int)*(uint*)(data + 48),
            TotalClusters: *(long*)(data + 16),   // NTFS_VOLUME_DATA_BUFFER: NumberSectors @8, TotalClusters @16, FreeClusters @24, TotalReserved @32
            MftStartLcn: *(long*)(data + 64),
            MftValidDataLength: *(long*)(data + 56));
        Validate(g);
        return g;
    }

    /// <summary>Geometry from an NTFS boot sector (for devices where the FSCTL is unavailable, and for images).</summary>
    public static VolumeGeometry ParseBootSector(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < 512 || !sector.Slice(3, 4).SequenceEqual("NTFS"u8)) throw new InvalidDataException("Not an NTFS boot sector.");
        int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(sector[11..]);
        int sectorsPerCluster = sector[13];
        if (sectorsPerCluster > 0x80) sectorsPerCluster = 1 << (256 - sectorsPerCluster); // large clusters are encoded as 2^(256-n)
        long totalSectors = BinaryPrimitives.ReadInt64LittleEndian(sector[40..]);
        long mftLcn = BinaryPrimitives.ReadInt64LittleEndian(sector[48..]);
        sbyte clustersPerRecord = (sbyte)sector[64];
        int bytesPerCluster = bytesPerSector * sectorsPerCluster;
        int recordSize = clustersPerRecord < 0 ? 1 << -clustersPerRecord : clustersPerRecord * bytesPerCluster;
        var g = new VolumeGeometry(bytesPerSector, bytesPerCluster, recordSize, sectorsPerCluster == 0 ? 0 : totalSectors / sectorsPerCluster, mftLcn, 0);
        Validate(g);
        return g;
    }

    private static void Validate(VolumeGeometry g)
    {
        if (g.BytesPerSector < 512 || g.BytesPerCluster < g.BytesPerSector || g.BytesPerCluster % g.BytesPerSector != 0
            || g.RecordSize < 512 || (g.RecordSize & (g.RecordSize - 1)) != 0 || g.RecordSize > Math.Max(g.BytesPerCluster, 4096) || g.MftStartLcn < 0)
            throw new NotSupportedException($"Unsupported NTFS geometry (record {g.RecordSize}, cluster {g.BytesPerCluster}, sector {g.BytesPerSector})");
    }

    /// <summary>Reads any byte range (aligned internally to 4 KB blocks).</summary>
    public void ReadBytes(long volumeOffset, Span<byte> dest)
    {
        if (dest.IsEmpty) return;
        if (volumeOffset < 0) throw new ArgumentOutOfRangeException(nameof(volumeOffset));
        const int A = HandleBlockSource.Alignment;
        long start = volumeOffset / A * A;
        long end = (volumeOffset + dest.Length + A - 1) / A * A;
        int length = checked((int)(end - start));
        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var buf = rented.AsSpan(0, length);
            _source.Read(start, buf);
            buf.Slice((int)(volumeOffset - start), dest.Length).CopyTo(dest);
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    public void ReadClusters(long lcn, int count, Span<byte> dest)
    {
        if (lcn < 0 || count < 0 || lcn + count > Geometry.TotalClusters) throw new ArgumentOutOfRangeException(nameof(lcn), $"Clusters {lcn}+{count} are outside the volume ({Geometry.TotalClusters}).");
        long bytes = (long)count * Geometry.BytesPerCluster;
        if (dest.Length < bytes) throw new ArgumentException("Destination too small.", nameof(dest));
        ReadBytes(lcn * Geometry.BytesPerCluster, dest[..(int)bytes]);
    }

    /// <summary>Reads one FILE record with its fixups applied. Throws for a record outside the MFT or a torn record.</summary>
    public byte[] ReadRecord(long recordNo)
    {
        if (recordNo < 0 || recordNo >= RecordCount) throw new ArgumentOutOfRangeException(nameof(recordNo), $"Record {recordNo} is outside the MFT ({RecordCount} records).");
        long byteOffset = recordNo * Geometry.RecordSize;
        long vcn = byteOffset / Geometry.BytesPerCluster;
        long within = byteOffset % Geometry.BytesPerCluster;
        long lcn = -1;
        foreach (var run in _mftExtents)
        {
            if (vcn >= run.Vcn && vcn < run.Vcn + run.Clusters) { lcn = run.Lcn < 0 ? -1 : run.Lcn + (vcn - run.Vcn); break; }
        }
        if (lcn < 0) throw new InvalidDataException($"Record {recordNo} is not stored (sparse $MFT run).");
        var record = new byte[Geometry.RecordSize];
        ReadBytes(lcn * Geometry.BytesPerCluster + within, record);
        if (!MftRecordParser.ApplyFixups(record)) throw new InvalidDataException($"Record {recordNo} is torn or not a FILE record.");
        return record;
    }

    public void Dispose()
    {
        _source.Dispose();
        ControlHandle?.Dispose();
    }
}
