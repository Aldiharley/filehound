using System.Runtime.InteropServices;
using FileHound.Indexing.Interop;
using FileHound.Indexing.Ntfs;
using Microsoft.Win32.SafeHandles;

namespace FileHound.Indexing.Recovery;

/// <summary>
/// Which clusters are allocated right now. Loaded through <c>FSCTL_GET_VOLUME_BITMAP</c> when the volume allows it,
/// otherwise by reading <c>$Bitmap</c> (MFT record 6) through the <see cref="VolumeReader"/>. One bit per cluster, LSB first.
/// </summary>
public sealed class ClusterBitmap
{
    private readonly byte[] _bits;

    private ClusterBitmap(byte[] bits, long totalClusters, string source)
    {
        _bits = bits;
        TotalClusters = totalClusters;
        Source = source;
    }

    public long TotalClusters { get; }
    /// <summary>"FSCTL" or "$Bitmap".</summary>
    public string Source { get; }

    public static ClusterBitmap Load(VolumeReader reader)
    {
        long total = reader.Geometry.TotalClusters;
        if (total <= 0 || total > (1L << 34)) throw new NotSupportedException($"Unsupported volume size ({total} clusters).");
        if (reader.ControlHandle is { } control && TryFsctl(control, total, out var bits)) return new ClusterBitmap(bits, total, "FSCTL");
        var record = reader.ReadRecord(6);
        if (!MftRecordParser.TryParse(record, out var r) || !r.InUse) throw new InvalidDataException("$Bitmap record is unreadable.");
        int needed = (int)((total + 7) / 8);
        bits = new byte[needed];
        if (r.DataIsResident)
        {
            r.ResidentData[..Math.Min(needed, r.ResidentData.Length)].CopyTo(bits);
        }
        else
        {
            if (r.UnnamedDataRuns.IsEmpty) throw new InvalidDataException("$Bitmap has no data runs.");
            int cluster = reader.Geometry.BytesPerCluster;
            long written = 0;
            foreach (var run in DataRuns.Decode(r.UnnamedDataRuns))
            {
                if (written >= needed) break;
                long runBytes = Math.Min(run.Clusters * cluster, needed - written);
                if (run.Lcn >= 0) reader.ReadBytes(run.Lcn * cluster, bits.AsSpan((int)written, (int)runBytes)); // sparse runs stay zero (free)
                written += runBytes;
            }
        }
        return new ClusterBitmap(bits, total, "$Bitmap");
    }

    private static unsafe bool TryFsctl(SafeFileHandle control, long totalClusters, out byte[] bits)
    {
        bits = new byte[(totalClusters + 7) / 8];
        const int ChunkBytes = 1 << 20;
        byte* output = (byte*)NativeMemory.Alloc(16 + ChunkBytes);
        try
        {
            long startLcn = 0;
            while (startLcn < totalClusters)
            {
                long input = startLcn;
                bool ok = Kernel32.DeviceIoControl(control, Kernel32.FSCTL_GET_VOLUME_BITMAP, &input, sizeof(long), output, 16 + ChunkBytes, out int returned, 0);
                int err = ok ? 0 : Marshal.GetLastPInvokeError();
                if (!ok && err != Kernel32.ERROR_MORE_DATA) return false;
                if (returned < 16) return false;
                long gotStart = *(long*)output;      // rounded down to a byte boundary by the file system
                int gotBytes = returned - 16;
                long byteIndex = gotStart >> 3;
                if (byteIndex < 0 || byteIndex > bits.Length) return false;
                int copy = (int)Math.Min(gotBytes, bits.Length - byteIndex);
                new ReadOnlySpan<byte>(output + 16, copy).CopyTo(bits.AsSpan((int)byteIndex));
                if (ok) break;
                if (copy == 0) return false; // "more data" but nothing came back: don't pass off a half-filled bitmap as free space
                startLcn = gotStart + (long)gotBytes * 8;
            }
            return true;
        }
        finally { NativeMemory.Free(output); }
    }

    /// <summary>Out-of-range clusters count as allocated (the conservative answer).</summary>
    public bool IsAllocated(long lcn)
    {
        if (lcn < 0 || lcn >= TotalClusters) return true;
        return (_bits[lcn >> 3] & (1 << (int)(lcn & 7))) != 0;
    }

    /// <summary>
    /// Counts allocated clusters among the first <paramref name="clustersNeeded"/> VCNs of the runs, skipping sparse
    /// runs (they hold no data to lose). Total is the number of stored clusters examined.
    /// </summary>
    public (long Allocated, long Total) Count(IReadOnlyList<DataRun> runs, long clustersNeeded)
    {
        long allocated = 0, total = 0, seen = 0;
        foreach (var run in runs)
        {
            if (seen >= clustersNeeded) break;
            long take = Math.Min(run.Clusters, clustersNeeded - seen);
            seen += take;
            if (run.Lcn < 0) continue;
            total += take;
            // Clusters outside the volume count as allocated without walking them (a corrupt run can claim billions).
            long inside = run.Lcn >= TotalClusters ? 0 : Math.Min(take, TotalClusters - run.Lcn);
            allocated += take - inside;
            for (long i = 0; i < inside; i++) if (IsAllocated(run.Lcn + i)) allocated++;
        }
        return (allocated, total);
    }
}
