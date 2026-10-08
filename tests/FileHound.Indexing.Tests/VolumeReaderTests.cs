using FileHound.Indexing.Ntfs;
using FileHound.Indexing.Recovery;

namespace FileHound.Indexing.Tests;

public class VolumeReaderTests
{
    [Fact]
    public void Reads_geometry_from_boot_sector_and_records_through_mft_extents()
    {
        var vol = new SyntheticVolume();
        vol.SetRecord(20, new MftRecordBuilder().InUse().FileName(5, "a.txt").ResidentData(3).Build());
        using var r = vol.OpenReader();
        Assert.Equal(VolumeReadPath.Memory, r.Path);
        Assert.Equal(4096, r.Geometry.BytesPerCluster);
        Assert.Equal(1024, r.Geometry.RecordSize);
        Assert.Equal(SyntheticVolume.MftLcn, r.Geometry.MftStartLcn);
        Assert.Equal(64 * 1024, r.Geometry.MftValidDataLength);
        Assert.Single(r.MftExtents);
        var rec = r.ReadRecord(20);
        Assert.True(MftRecordParser.TryParse(rec, out var p));
        Assert.Equal("a.txt", p.Name.ToString());
    }

    [Fact]
    public void Unaligned_byte_reads_are_served()
    {
        var vol = new SyntheticVolume();
        vol.WriteCluster(10, Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray());
        using var r = vol.OpenReader();
        var buf = new byte[10];
        r.ReadBytes(10 * 4096 + 1000, buf);
        Assert.Equal(Enumerable.Range(1000, 10).Select(i => (byte)i).ToArray(), buf);
        var cluster = new byte[4096];
        r.ReadClusters(10, 1, cluster);
        Assert.Equal(255, cluster[255]);
    }

    [Fact]
    public void Boot_sector_parser_handles_negative_record_size()
    {
        var s = new byte[512];
        "NTFS    "u8.CopyTo(s.AsSpan(3));
        BitConverter.GetBytes((ushort)512).CopyTo(s, 11);
        s[13] = 8;
        BitConverter.GetBytes(1_000_000L).CopyTo(s, 40);
        BitConverter.GetBytes(786432L).CopyTo(s, 48);
        s[64] = 0xF6;
        var g = VolumeReader.ParseBootSector(s);
        Assert.Equal(4096, g.BytesPerCluster);
        Assert.Equal(1024, g.RecordSize);
        Assert.Equal(125_000, g.TotalClusters);
        Assert.Equal(786432, g.MftStartLcn);
    }

    [Fact]
    public void Boot_sector_without_ntfs_signature_is_rejected() =>
        Assert.Throws<InvalidDataException>(() => VolumeReader.ParseBootSector(new byte[512]));

    [Fact]
    public void Record_out_of_range_throws()
    {
        var vol = new SyntheticVolume(records: 16);
        using var r = vol.OpenReader();
        Assert.Throws<ArgumentOutOfRangeException>(() => r.ReadRecord(16));
    }

    [Fact]
    public void Torn_record_is_reported()
    {
        var vol = new SyntheticVolume();
        var rec = new MftRecordBuilder().InUse().FileName(5, "t.txt").Build();
        rec[1022] ^= 0xFF;
        vol.SetRecord(21, rec);
        using var r = vol.OpenReader();
        Assert.Throws<InvalidDataException>(() => r.ReadRecord(21));
    }
}
