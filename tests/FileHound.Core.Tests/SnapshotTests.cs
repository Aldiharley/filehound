using FileHound.Core.Persistence;
using FileHound.Core.Stats;

namespace FileHound.Core.Tests;

public sealed class SnapshotTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fh-snap-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Concurrent_saves_of_the_same_path_do_not_collide()
    {
        // Two drive scans finishing seconds apart each save every dirty index; the second must not fail on the first's temp file.
        var path = Path.Combine(_dir, "E_CAFEBABE.fhx");
        var v = Sample();
        var errors = new List<Exception>();
        var start = new ManualResetEventSlim(false);
        var threads = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
        {
            start.Wait();
            for (int i = 0; i < 20; i++)
            {
                try { SnapshotSerializer.Save(v, path); }
                catch (Exception ex) { lock (errors) errors.Add(ex); }
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        start.Set();
        threads.ForEach(t => t.Join());
        Assert.Empty(errors);
        Assert.NotNull(SnapshotSerializer.Load(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    private static VolumeIndex Sample(IndexMode mode = IndexMode.Standard)
    {
        var v = new VolumeIndex(@"E:\", mode) { VolumeSerial = 0xCAFEBABE, UsnJournalId = 42, NextUsn = 9001 };
        var docs = v.Add(0, "Docs", EntryFlags.Directory | EntryFlags.MetadataKnown, 0, 111, recordNo: mode == IndexMode.Turbo ? 40 : -1);
        v.Add(docs, "Report.PDF", EntryFlags.MetadataKnown | EntryFlags.Hidden, 2048, 222, recordNo: mode == IndexMode.Turbo ? 41 : -1);
        var tmp = v.Add(0, "tmp", EntryFlags.Directory, 0, 0, recordNo: mode == IndexMode.Turbo ? 42 : -1);
        v.Add(tmp, "junk.bin", 0, 0, 0, recordNo: mode == IndexMode.Turbo ? 43 : -1);
        v.Rename(docs, 0, "Documents");
        v.Delete(tmp);
        return v;
    }

    [Fact]
    public void Round_trip_preserves_entries_and_header()
    {
        var path = Path.Combine(_dir, "e.fhx");
        SnapshotSerializer.Save(Sample(), path);
        var v = SnapshotSerializer.Load(path)!;
        Assert.NotNull(v);
        Assert.Equal(@"E:\", v.Root);
        Assert.Equal(0xCAFEBABEu, v.VolumeSerial);
        Assert.Equal(42ul, v.UsnJournalId);
        Assert.Equal(9001, v.NextUsn);
        Assert.Equal(2, v.LiveCount);
        Assert.Equal(3, v.Count); // compacted: root + 2
        var r = v.FindByPath(@"E:\Documents\report.pdf");
        Assert.True(r > 0);
        Assert.Equal("Report.PDF", v.Name(r).ToString());
        Assert.Equal(2048, v.Size(r));
        Assert.Equal(222, v.ModifiedTicks(r));
        Assert.True(v.Flags(r).HasFlag(EntryFlags.Hidden));
        Assert.Equal(FileCategory.Document, v.Category(r));
        Assert.Equal(2, v.Depth(r));
        Assert.False(v.IsDirty);
    }

    [Fact]
    public void Round_trip_preserves_turbo_records()
    {
        var path = Path.Combine(_dir, "t.fhx");
        SnapshotSerializer.Save(Sample(IndexMode.Turbo), path);
        var v = SnapshotSerializer.Load(path)!;
        Assert.Equal(IndexMode.Turbo, v.Mode);
        Assert.Equal(v.FindByPath(@"E:\Documents\Report.PDF"), v.FindByRecord(41));
        Assert.Equal(-1, v.FindByRecord(43));
    }

    [Fact]
    public void Flipped_byte_is_rejected_and_deleted()
    {
        var path = Path.Combine(_dir, "c.fhx");
        SnapshotSerializer.Save(Sample(), path);
        var bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
        Assert.Null(SnapshotSerializer.Load(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Truncated_file_is_rejected()
    {
        var path = Path.Combine(_dir, "tr.fhx");
        SnapshotSerializer.Save(Sample(), path);
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length - 20)]);
        Assert.Null(SnapshotSerializer.Load(path));
    }

    [Fact]
    public void Bad_magic_is_rejected()
    {
        var path = Path.Combine(_dir, "m.fhx");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]);
        Assert.Null(SnapshotSerializer.Load(path));
    }

    [Fact] public void Missing_file_returns_null() => Assert.Null(SnapshotSerializer.Load(Path.Combine(_dir, "none.fhx")));

    [Fact] public void File_name_is_letter_and_serial() => Assert.Equal("C_1A2B3C4D.fhx", SnapshotSerializer.FileNameFor('c', 0x1A2B3C4D));

    [Fact]
    public void Statistics_counts_and_largest()
    {
        var v = new VolumeIndex(@"C:\", IndexMode.Standard);
        var d = v.Add(0, "Music", EntryFlags.Directory, 0, 0);
        v.Add(d, "a.mp3", EntryFlags.MetadataKnown, 300, 0);
        v.Add(d, "b.mp3", EntryFlags.MetadataKnown, 100, 0);
        v.Add(0, "big.iso", EntryFlags.MetadataKnown, 9_000, 0);
        v.Add(0, "unknown.bin", 0, 0, 0);
        var gone = v.Add(0, "gone.txt", EntryFlags.MetadataKnown, 99_999, 0);
        v.Delete(gone);

        var s = IndexStatistics.Compute([v], largestCount: 2);
        Assert.Equal(4, s.Files);
        Assert.Equal(1, s.Folders);
        Assert.Equal(2, s.Categories.Single(c => c.Category == FileCategory.Audio).Count);
        Assert.Equal(1, s.Categories.Single(c => c.Category == FileCategory.Archive).Count);
        Assert.Equal(["big.iso", "a.mp3"], s.Largest.Select(l => l.Volume.Name(l.Entry).ToString()));
        Assert.Equal(9_400, s.TotalBytes);
    }
}
