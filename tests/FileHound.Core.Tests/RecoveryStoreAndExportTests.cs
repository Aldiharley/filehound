using System.Xml.Linq;
using FileHound.Core.Recovery;

namespace FileHound.Core.Tests;

public class DeletionLogStoreTests
{
    private static DeletionEntry E(long rec, string name, long usn, DeletionKind k = DeletionKind.Deleted) =>
        new(rec, 3, 5, name, @"C:\Docs", 10, false, 1, 2, k, usn);

    [Fact]
    public void Ring_buffer_drops_oldest()
    {
        var s = new DeletionLogStore(3);
        for (int i = 0; i < 5; i++) s.Add(E(i, $"f{i}", i));
        Assert.Equal(3, s.Count);
        Assert.Equal(["f4", "f3", "f2"], s.Snapshot().Select(e => e.Name));
        Assert.Equal(4, s.LastUsn);
    }

    [Fact]
    public void Mark_retags_latest_matching_frn()
    {
        var s = new DeletionLogStore();
        s.Add(E(7, "a", 1));
        s.Add(E(7, "a", 2));
        Assert.True(s.TryMark(7, 3, DeletionKind.Recycled));
        Assert.Equal(DeletionKind.Recycled, s.Snapshot()[0].Kind);
        Assert.Equal(DeletionKind.Deleted, s.Snapshot()[1].Kind);
        Assert.False(s.TryMark(7, 4, DeletionKind.Recycled)); // sequence mismatch
        Assert.False(s.TryMark(99, 3, DeletionKind.Recycled));
    }

    [Fact]
    public void Round_trips_and_rejects_corruption()
    {
        var dir = Directory.CreateTempSubdirectory("fh-dlog-");
        var p = Path.Combine(dir.FullName, "C_1.dlog");
        try
        {
            var s = new DeletionLogStore();
            s.Add(E(1, "one.txt", 10));
            s.Add(E(2, "two", 11, DeletionKind.Recycled));
            s.Save(p);
            var l = DeletionLogStore.Load(p);
            Assert.Equal(2, l.Count);
            Assert.Equal("two", l.Snapshot()[0].Name);
            Assert.Equal(DeletionKind.Recycled, l.Snapshot()[0].Kind);
            Assert.Equal(@"C:\Docs", l.Snapshot()[0].ParentPath);
            Assert.Equal(11, l.LastUsn);
            Assert.False(l.IsDirty);

            var b = File.ReadAllBytes(p);
            b[b.Length / 2] ^= 1;
            File.WriteAllBytes(p, b);
            Assert.Equal(0, DeletionLogStore.Load(p).Count);
            Assert.Equal(0, DeletionLogStore.Load(Path.Combine(dir.FullName, "none.dlog")).Count);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Add_sets_dirty_and_save_clears_it()
    {
        var dir = Directory.CreateTempSubdirectory("fh-dlog-");
        try
        {
            var s = new DeletionLogStore();
            Assert.False(s.IsDirty);
            s.Add(E(1, "a", 1));
            Assert.True(s.IsDirty);
            s.Save(Path.Combine(dir.FullName, "x.dlog"));
            Assert.False(s.IsDirty);
        }
        finally { dir.Delete(true); }
    }
}

public class ExportTests
{
    [Fact]
    public void Csv_quotes_commas_and_quotes()
    {
        var sw = new StringWriter();
        CsvExport.WriteCandidates(sw, [new RecoveryCandidate(RecoverySource.RecycleBin, "a,b \"q\".txt", @"C:\x", 5, null,
            new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), RecoveryGrade.Excellent, 100, false, null, 0)]);
        var lines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("source,name,original_folder,size,modified_utc,deleted_utc,grade,percent_intact,is_directory,detail", lines[0]);
        Assert.Equal("RecycleBin,\"a,b \"\"q\"\".txt\",C:\\x,5,,2026-01-02T03:04:05Z,Excellent,100,false,", lines[1]);
    }

    [Fact]
    public void Csv_deletions_and_manifest_have_headers()
    {
        var sw = new StringWriter();
        CsvExport.WriteDeletions(sw, [new DeletionEntry(5, 1, 3, "n", @"C:\p", 1, true, 0, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks, DeletionKind.Deleted, 9)]);
        var lines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("deleted_utc,name,parent_path,size,is_directory,kind,record_no,sequence,usn", lines[0]);
        Assert.Equal("2026-01-01T00:00:00Z,n,C:\\p,1,true,Deleted,5,1,9", lines[1]);

        var c = new RecoveryCandidate(RecoverySource.DeletionLog, "f.bin", @"C:\d", 3, null, null, RecoveryGrade.Good, 100, false, null, 0);
        sw = new StringWriter();
        CsvExport.WriteManifest(sw, [new RecoveredFile(c, @"E:\r\f.bin", 3, "abc", RecoveryGrade.Good, null)]);
        lines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("original_path,recovered_path,size,sha256,grade,source,error", lines[0]);
        Assert.Equal("C:\\d\\f.bin,E:\\r\\f.bin,3,abc,Good,DeletionLog,", lines[1]);
    }

    [Fact]
    public void Dfxml_is_well_formed_with_hash()
    {
        var sw = new StringWriter();
        var c = new RecoveryCandidate(RecoverySource.DeletionLog, "f.bin", @"C:\d", 3, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), null, RecoveryGrade.Good, 100, false, null, 0);
        DfxmlExport.Write(sw, @"\\.\C:", [new RecoveredFile(c, @"E:\r\f.bin", 3, "abc123", RecoveryGrade.Good, null)], DateTime.UtcNow, DateTime.UtcNow);
        var doc = XDocument.Parse(sw.ToString());
        var ns = doc.Root!.Name.Namespace;
        Assert.Equal("dfxml", doc.Root.Name.LocalName);
        var fo = doc.Root.Element(ns + "fileobject")!;
        Assert.Equal(@"C:\d\f.bin", fo.Element(ns + "filename")!.Value);
        Assert.Equal("3", fo.Element(ns + "filesize")!.Value);
        Assert.Equal("2026-01-01T00:00:00Z", fo.Element(ns + "mtime")!.Value);
        Assert.Equal("abc123", fo.Element(ns + "hashdigest")!.Value);
        Assert.Equal("sha256", fo.Element(ns + "hashdigest")!.Attribute("type")!.Value);
        Assert.Equal("FileHound", doc.Root.Element(ns + "creator")!.Element(ns + "program")!.Value);
    }
}
