using FileHound.Indexing.Ntfs;

namespace FileHound.Indexing.Tests;

public class RawMftReaderTests
{
    private static readonly DateTime When = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
    private const int RecordSize = 1024;

    /// <summary>Lays records out at their record numbers in one contiguous buffer, like a $MFT stream.</summary>
    private static byte[] Mft(int count, params (int No, byte[] Record)[] records)
    {
        var buffer = new byte[count * RecordSize];
        foreach (var (no, record) in records) record.CopyTo(buffer, no * RecordSize);
        return buffer;
    }

    private static VolumeIndex Read(byte[] mft, int chunkRecords = 64)
    {
        var acc = new RawMftReader.Accumulator(@"Q:\", RecordSize, 1024);
        for (int first = 0; first * RecordSize < mft.Length; first += chunkRecords)
        {
            int len = Math.Min(chunkRecords * RecordSize, mft.Length - first * RecordSize);
            acc.AddChunk(mft.AsSpan(first * RecordSize, len), first);
        }
        return acc.Build();
    }

    [Fact]
    public void Builds_paths_sizes_times_and_skips_metadata()
    {
        var mft = Mft(80,
            (0, new MftRecordBuilder().InUse().StandardInfo(When).FileName(5, "$MFT").NonResidentData(1 << 20, [0x11, 0x01, 0x05, 0x00]).Build()),
            (5, new MftRecordBuilder().InUse(directory: true).StandardInfo(When).FileName(5, ".").Build()),
            (11, new MftRecordBuilder().InUse(directory: true).StandardInfo(When).FileName(5, "$Extend").Build()),
            (24, new MftRecordBuilder().InUse().StandardInfo(When).FileName(11, "$UsnJrnl").ResidentData(10).Build()),
            (64, new MftRecordBuilder().InUse(directory: true).StandardInfo(When, 0x10).FileName(5, "Docs").Build()),
            (65, new MftRecordBuilder().InUse().StandardInfo(When, 0x2 | 0x20).FileName(64, "report.pdf").NonResidentData(1234, [0x11, 0x01, 0x30, 0x00]).Build()),
            (67, new MftRecordBuilder().StandardInfo(When).FileName(64, "deleted.txt").ResidentData(5).Build()),          // not in use
            (68, new MftRecordBuilder().InUse().StandardInfo(When).FileName(64, "big.iso").NonResidentData(7_000_000_000, [0x11, 0x08, 0x40, 0x00]).Build()));
        var v = Read(mft);

        int report = v.FindByPath(@"Q:\Docs\report.pdf");
        Assert.True(report > 0);
        Assert.Equal(1234, v.Size(report));
        Assert.Equal(When.Ticks, v.ModifiedTicks(report));
        Assert.True(v.Flags(report).HasFlag(EntryFlags.Hidden));
        Assert.True(v.Flags(report).HasFlag(EntryFlags.MetadataKnown));
        Assert.Equal(7_000_000_000, v.Size(v.FindByPath(@"Q:\Docs\big.iso")));
        Assert.True(v.IsDirectory(v.FindByPath(@"Q:\Docs")));
        Assert.Equal(report, v.FindByRecord(65));
        Assert.Equal(-1, v.FindByPath(@"Q:\$MFT"));
        Assert.Equal(-1, v.FindByPath(@"Q:\$Extend"));
        Assert.Equal(-1, v.FindByRecord(24));           // child of a skipped metafile dir: dropped
        Assert.Equal(-1, v.FindByPath(@"Q:\Docs\deleted.txt"));
        Assert.Equal(3, v.LiveCount);                   // Docs, report.pdf, big.iso
        Assert.False(MetadataFiller.HasIncompleteMetadata(v));
    }

    [Fact]
    public void Merges_extension_records_into_base_records()
    {
        var mft = Mft(80,
            (5, new MftRecordBuilder().InUse(directory: true).StandardInfo(When).FileName(5, ".").Build()),
            (64, new MftRecordBuilder().InUse(directory: true).StandardInfo(When).FileName(5, "Docs").Build()),
            // 66's name lives in extension record 70 (comes later in the stream)
            (66, new MftRecordBuilder().InUse().StandardInfo(When).AttributeList().ResidentData(42).Build()),
            (70, new MftRecordBuilder().InUse().Extension(66).FileName(64, "many-links.txt").Build()),
            // 72's $DATA lives in extension record 71 (comes earlier in the stream)
            (71, new MftRecordBuilder().InUse().Extension(72).NonResidentData(9_999, [0x11, 0x01, 0x07, 0x00]).Build()),
            (72, new MftRecordBuilder().InUse().StandardInfo(When).AttributeList().FileName(64, "fragmented.bin").Build()));
        var v = Read(mft, chunkRecords: 16);

        int links = v.FindByPath(@"Q:\Docs\many-links.txt");
        Assert.True(links > 0);
        Assert.Equal(42, v.Size(links));
        Assert.Equal(links, v.FindByRecord(66));
        int frag = v.FindByPath(@"Q:\Docs\fragmented.bin");
        Assert.Equal(9_999, v.Size(frag));
        Assert.Equal(-1, v.FindByRecord(70));            // extension records are not entries
        Assert.Equal(3, v.LiveCount);
    }

    [Fact]
    public void Records_added_individually_in_any_order_give_the_same_index()
    {
        var records = new List<(int No, byte[] Record)>
        {
            (5, new MftRecordBuilder().InUse(directory: true).StandardInfo(When).FileName(5, ".").Build()),
            (64, new MftRecordBuilder().InUse(directory: true).StandardInfo(When).FileName(5, "Docs").Build()),
            (65, new MftRecordBuilder().InUse().StandardInfo(When).FileName(64, "a.txt").ResidentData(11).Build()),
            (66, new MftRecordBuilder().InUse().StandardInfo(When).AttributeList().ResidentData(42).Build()),
            (70, new MftRecordBuilder().InUse().Extension(66).FileName(64, "b.txt").Build()),
        };
        var acc = new RawMftReader.Accumulator(@"Q:\", RecordSize, 64);
        foreach (var (no, rec) in Enumerable.Reverse(records))   // descending, as FSCTL_GET_NTFS_FILE_RECORD walks
        {
            Assert.True(MftRecordParser.ApplyFixups(rec));        // simulate records already fixed up by NTFS
            acc.AddRecord(rec, no, fixupsMayBeApplied: true);
        }
        var v = acc.Build();
        Assert.Equal(11, v.Size(v.FindByPath(@"Q:\Docs\a.txt")));
        Assert.Equal(42, v.Size(v.FindByPath(@"Q:\Docs\b.txt")));
        Assert.Equal(3, v.LiveCount);
    }

    [Fact]
    public void Downward_enumeration_visits_each_in_use_record_once_across_partitions()
    {
        // Docs example: records 1..9 and 15 in use, 10..14 free. "Fetch n" returns the highest in-use record <= n.
        var inUse = new SortedSet<long> { 1, 2, 3, 4, 5, 6, 7, 8, 9, 15, 40, 41 };
        long Fetch(long n) => inUse.GetViewBetween(0, n).Count == 0 ? -1 : inUse.GetViewBetween(0, n).Max;
        var seen = new List<long>();
        int calls = 0;
        foreach (var (lo, hi) in FileRecordMftReader.Partition(total: 48, parts: 5))
            FileRecordMftReader.EnumerateDownward(lo, hi, n => { calls++; return Fetch(n); }, seen.Add);
        Assert.Equal(inUse.OrderBy(x => x), seen.Order());
        Assert.True(calls <= inUse.Count + 5, $"{calls} calls"); // ~one call per in-use record (+1 per partition)
    }

    [Fact]
    public void Torn_records_are_skipped()
    {
        var torn = new MftRecordBuilder().InUse().StandardInfo(When).FileName(5, "torn.txt").Build();
        torn[1022] ^= 0xFF;
        var mft = Mft(70,
            (5, new MftRecordBuilder().InUse(directory: true).StandardInfo(When).FileName(5, ".").Build()),
            (64, torn),
            (65, new MftRecordBuilder().InUse().StandardInfo(When).FileName(5, "ok.txt").ResidentData(1).Build()));
        var v = Read(mft);
        Assert.Equal(-1, v.FindByPath(@"Q:\torn.txt"));
        Assert.True(v.FindByPath(@"Q:\ok.txt") > 0);
    }

    [Fact]
    public void Chunk_must_be_record_aligned() =>
        Assert.Throws<ArgumentException>(() => new RawMftReader.Accumulator(@"Q:\", RecordSize, 16).AddChunk(new byte[1500], 0));
}
