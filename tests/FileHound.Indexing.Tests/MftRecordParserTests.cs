using FileHound.Indexing.Ntfs;

namespace FileHound.Indexing.Tests;

public class MftRecordParserTests
{
    private static readonly DateTime When = new(2026, 5, 4, 3, 2, 1, DateTimeKind.Utc);

    private static byte[] Fixed(byte[] record)
    {
        Assert.True(MftRecordParser.ApplyFixups(record));
        return record;
    }

    [Fact]
    public void Data_runs_decode_positive_negative_and_sparse()
    {
        byte[] runs = [0x21, 0x18, 0x34, 0x56, 0x11, 0x10, 0xF0, 0x01, 0x08, 0x00];
        var r = DataRuns.Decode(runs);
        Assert.Equal([new DataRun(0, 0x5634, 0x18), new DataRun(0x18, 0x5624, 0x10), new DataRun(0x28, -1, 8)], r);
    }

    [Fact]
    public void Data_runs_reject_truncated_input() => Assert.Throws<InvalidDataException>(() => DataRuns.Decode([0x21, 0x18]));

    [Fact]
    public void Fixups_restore_sector_tails()
    {
        var b = new MftRecordBuilder().InUse().StandardInfo(When).FileName(5, "a.txt").Build();
        Assert.Equal(MftRecordBuilder.Usn, BitConverter.ToUInt16(b, 510));
        Assert.True(MftRecordParser.ApplyFixups(b));
        Assert.NotEqual(MftRecordBuilder.Usn, BitConverter.ToUInt16(b, 510));
    }

    [Fact]
    public void Torn_record_is_rejected()
    {
        var b = new MftRecordBuilder().InUse().StandardInfo(When).FileName(5, "a.txt").Build();
        b[1022] ^= 0xFF; // second sector's tail no longer carries the USN
        Assert.False(MftRecordParser.ApplyFixups(b));
    }

    [Fact]
    public void Bad_signature_is_rejected() => Assert.False(MftRecordParser.ApplyFixups(new byte[1024]));

    [Fact]
    public void Records_with_fixups_already_applied_are_accepted_only_when_allowed()
    {
        // FSCTL_GET_NTFS_FILE_RECORD may hand back records NTFS already fixed up in memory.
        var b = new MftRecordBuilder().InUse().StandardInfo(When).FileName(5, "a.txt").ResidentData(3).Build();
        Assert.True(MftRecordParser.ApplyFixups(b));
        var copy = (byte[])b.Clone();
        Assert.False(MftRecordParser.ApplyFixups(b));                                // tails no longer carry the USN
        Assert.True(MftRecordParser.ApplyFixups(b, acceptAlreadyApplied: true));
        Assert.Equal(copy, b);                                                        // unchanged
        Assert.True(MftRecordParser.TryParse(b, out var r));
        Assert.Equal("a.txt", r.Name.ToString());
    }

    [Fact]
    public void In_memory_record_with_stale_update_sequence_array_is_accepted()
    {
        // Real case (mft-diff on C:): a record modified in memory but not yet flushed. NTFS returns it fixed up, but the
        // update sequence array still holds values from the last disk write: tails=[6444,0000], saved=[0000,0000].
        var b = new MftRecordBuilder().InUse().StandardInfo(When).FileName(5, "notion-cache_0").ResidentData(600).Build();
        Assert.True(MftRecordParser.ApplyFixups(b));
        b[510] = 0x44; b[511] = 0x64;                         // current data in the first sector tail
        var expected = (byte[])b.Clone();
        Assert.False(MftRecordParser.ApplyFixups(b));           // a raw disk read in this state would be torn
        Assert.True(MftRecordParser.ApplyFixups(b, acceptAlreadyApplied: true));
        Assert.Equal(expected, b);                              // left exactly as NTFS returned it
        Assert.True(MftRecordParser.TryParse(b, out var r));
        Assert.Equal("notion-cache_0", r.Name.ToString());
    }

    [Fact]
    public void In_memory_record_still_requires_the_FILE_signature() =>
        Assert.False(MftRecordParser.ApplyFixups(new byte[1024], acceptAlreadyApplied: true));

    [Fact]
    public void Parses_name_parent_size_time_attributes()
    {
        var b = Fixed(new MftRecordBuilder().InUse().StandardInfo(When, attributes: 0x2 | 0x20).FileName(64, "Report.pdf").ResidentData(321).Build());
        Assert.True(MftRecordParser.TryParse(b, out var r));
        Assert.True(r.InUse);
        Assert.False(r.IsDirectory);
        Assert.Equal(0, r.BaseRecord);
        Assert.True(r.HasName);
        Assert.Equal("Report.pdf", r.Name.ToString());
        Assert.Equal(64, r.ParentRecord);
        Assert.True(r.HasSize);
        Assert.Equal(321, r.Size);
        Assert.Equal(When.Ticks, r.ModifiedUtcTicks);
        Assert.Equal(0x22u, r.Attributes);
    }

    [Fact]
    public void Win32_name_wins_over_dos_name()
    {
        var b = Fixed(new MftRecordBuilder().InUse().StandardInfo(When).FileName(5, "LONGFI~1.TXT", nameSpace: 2).FileName(5, "long file name.txt", nameSpace: 1).Build());
        Assert.True(MftRecordParser.TryParse(b, out var r));
        Assert.Equal("long file name.txt", r.Name.ToString());
    }

    [Fact]
    public void Non_resident_data_size_and_runs()
    {
        byte[] runs = [0x11, 0x04, 0x20, 0x00];
        var b = Fixed(new MftRecordBuilder().InUse().StandardInfo(When).FileName(5, "big.iso").NonResidentData(5_000_000_000, runs).Build());
        Assert.True(MftRecordParser.TryParse(b, out var r));
        Assert.Equal(5_000_000_000, r.Size);
        Assert.Equal(runs, r.UnnamedDataRuns[..4].ToArray());
    }

    [Fact]
    public void Named_stream_is_ignored()
    {
        var b = Fixed(new MftRecordBuilder().InUse().StandardInfo(When).FileName(5, "f.txt").ResidentData(10, "Zone.Identifier").ResidentData(77).Build());
        Assert.True(MftRecordParser.TryParse(b, out var r));
        Assert.Equal(77, r.Size);
    }

    [Fact]
    public void Non_first_extent_does_not_set_size()
    {
        var b = Fixed(new MftRecordBuilder().InUse().StandardInfo(When).FileName(5, "frag.bin").NonResidentData(999, [0x11, 0x01, 0x05, 0x00], startVcn: 10).Build());
        Assert.True(MftRecordParser.TryParse(b, out var r));
        Assert.False(r.HasSize);
    }

    [Fact]
    public void Directory_flag()
    {
        var b = Fixed(new MftRecordBuilder().InUse(directory: true).StandardInfo(When, 0x10).FileName(5, "Docs").Build());
        Assert.True(MftRecordParser.TryParse(b, out var r));
        Assert.True(r.IsDirectory);
    }

    [Fact]
    public void Not_in_use()
    {
        var b = Fixed(new MftRecordBuilder().StandardInfo(When).FileName(5, "gone.txt").Build());
        Assert.True(MftRecordParser.TryParse(b, out var r));
        Assert.False(r.InUse);
    }

    [Fact]
    public void Extension_record_and_attribute_list()
    {
        var ext = Fixed(new MftRecordBuilder().InUse().Extension(66).FileName(64, "many-links.txt").Build());
        Assert.True(MftRecordParser.TryParse(ext, out var e));
        Assert.Equal(66, e.BaseRecord);
        Assert.Equal("many-links.txt", e.Name.ToString());

        var baseRec = Fixed(new MftRecordBuilder().InUse().StandardInfo(When).AttributeList().Build());
        Assert.True(MftRecordParser.TryParse(baseRec, out var b));
        Assert.True(b.HasAttributeList);
        Assert.False(b.HasName);
    }

    [Fact]
    public void Malformed_attribute_length_is_rejected_without_throwing()
    {
        var bad = new byte[16];
        BitConverter.GetBytes(0x30u).CopyTo(bad, 0);
        BitConverter.GetBytes(4000u).CopyTo(bad, 4); // longer than the record
        var b = Fixed(new MftRecordBuilder().InUse().StandardInfo(When).Raw(bad).Build());
        Assert.False(MftRecordParser.TryParse(b, out _));
    }
}
