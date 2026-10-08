using System.Buffers.Binary;
using FileHound.Indexing.Recovery;

namespace FileHound.Indexing.Tests;

/// <summary>
/// An NTFS-shaped volume image in memory for recovery tests: boot sector, an $MFT at <see cref="MftLcn"/> holding
/// <c>records</c> 1 KB records (0 = $MFT, 5 = root, 6 = $Bitmap pre-filled), the $Bitmap data at <see cref="BitmapLcn"/>,
/// and free clusters for file data.
/// </summary>
internal sealed class SyntheticVolume
{
    public const int Cluster = 4096, Record = 1024;
    public const long MftLcn = 4, BitmapLcn = 2;

    private readonly int _clusters;

    public SyntheticVolume(int clusters = 256, int records = 64)
    {
        _clusters = clusters;
        Image = new byte[(long)clusters * Cluster];
        int mftClusters = Math.Max(1, (records * Record + Cluster - 1) / Cluster);

        // Boot sector
        "NTFS    "u8.CopyTo(Image.AsSpan(3));
        BinaryPrimitives.WriteUInt16LittleEndian(Image.AsSpan(11), 512);
        Image[13] = Cluster / 512;
        BinaryPrimitives.WriteInt64LittleEndian(Image.AsSpan(40), (long)clusters * (Cluster / 512));
        BinaryPrimitives.WriteInt64LittleEndian(Image.AsSpan(48), MftLcn);
        Image[64] = 0xF6; // 2^10 = 1024-byte records

        SetRecord(0, new MftRecordBuilder().InUse().FileName(5, "$MFT").NonResidentData((long)records * Record, Runs((MftLcn, mftClusters))).Build());
        SetRecord(5, new MftRecordBuilder().InUse(directory: true).FileName(5, ".").Build());
        SetRecord(6, new MftRecordBuilder().InUse().FileName(5, "$Bitmap").NonResidentData((clusters + 7) / 8, Runs((BitmapLcn, 1))).Build());
        for (long l = 0; l < MftLcn + mftClusters; l++) SetAllocated(l, true);
    }

    public byte[] Image { get; }

    /// <summary>Stores a record as the builder produced it (update-sequence protection applied, as on disk).</summary>
    public void SetRecord(long no, byte[] record)
    {
        if (record.Length != Record) throw new ArgumentException("Record size must be 1024.", nameof(record));
        record.CopyTo(Image.AsSpan((int)(MftLcn * Cluster + no * Record)));
    }

    public void SetAllocated(long lcn, bool allocated)
    {
        if (lcn >= _clusters) throw new ArgumentOutOfRangeException(nameof(lcn));
        int index = (int)(BitmapLcn * Cluster + (lcn >> 3));
        int bit = 1 << (int)(lcn & 7);
        if (allocated) Image[index] |= (byte)bit; else Image[index] &= (byte)~bit;
    }

    public void WriteCluster(long lcn, ReadOnlySpan<byte> data)
    {
        if (data.Length > Cluster) throw new ArgumentException("At most one cluster.", nameof(data));
        data.CopyTo(Image.AsSpan((int)(lcn * Cluster)));
    }

    public VolumeReader OpenReader() =>
        new(new MemoryBlockSource(Image), VolumeReader.ParseBootSector(Image.AsSpan(0, 512)), control: null);

    /// <summary>Encodes mapping pairs with 2-byte lengths and 4-byte (relative) offsets.</summary>
    public static byte[] Runs(params (long Lcn, long Clusters)[] runs)
    {
        var bytes = new List<byte>();
        long previous = 0;
        foreach (var (lcn, clusters) in runs)
        {
            bytes.Add(0x42);
            bytes.Add((byte)clusters); bytes.Add((byte)(clusters >> 8));
            long delta = lcn - previous;
            bytes.Add((byte)delta); bytes.Add((byte)(delta >> 8)); bytes.Add((byte)(delta >> 16)); bytes.Add((byte)(delta >> 24));
            previous = lcn;
        }
        bytes.Add(0);
        return bytes.ToArray();
    }
}
