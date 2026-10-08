using FileHound.Core.Recovery;
using FileHound.Indexing.Interop;
using FileHound.Indexing.Ntfs;
using FileHound.Indexing.Recovery;

namespace FileHound.Indexing.Tests;

public class DeletionEventTests
{
    private static (VolumeIndex Index, int Docs) IndexWithDocs()
    {
        var index = new VolumeIndex(@"Q:\", IndexMode.Turbo);
        index.SetRecord(0, 5);
        int docs = index.Add(0, "Docs", EntryFlags.Directory, 0, 0, recordNo: 100);
        return (index, docs);
    }

    [Fact]
    public void Delete_raises_with_parent_path_before_removal()
    {
        var (index, docs) = IndexWithDocs();
        int f = index.Add(docs, "report.docx", EntryFlags.MetadataKnown, 4321, 777, recordNo: 200);
        using var u = new UsnUpdater(index, 'Q', TimeSpan.FromSeconds(1));
        UsnDeletion? got = null;
        u.Deleted += (_, d) => got = d;
        long when = new DateTime(2026, 10, 8, 10, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();

        u.ApplyBuffer(UsnRecordParserTests.V2(200, 100, "report.docx", UsnReason.FileDelete | UsnReason.Close, usn: 500, sequence: 3, timestamp: when));

        Assert.NotNull(got);
        Assert.Equal(@"Q:\Docs", got!.ParentPath);
        Assert.Equal("report.docx", got.Name);
        Assert.Equal(4321, got.Size);
        Assert.Equal(777, got.ModifiedUtcTicks);
        Assert.Equal(3, got.Sequence);
        Assert.Equal(500, got.Usn);
        Assert.Equal(DateTime.FromFileTimeUtc(when).Ticks, got.DeletedUtcTicks);
        Assert.Equal(DeletionKind.Deleted, got.Kind);
        Assert.False(index.IsLive(f));
    }

    [Fact]
    public void Delete_of_unknown_record_still_reports_name_and_parent_when_parent_is_known()
    {
        var (index, _) = IndexWithDocs();
        using var u = new UsnUpdater(index, 'Q', TimeSpan.FromSeconds(1));
        UsnDeletion? got = null;
        u.Deleted += (_, d) => got = d;
        u.ApplyBuffer(UsnRecordParserTests.V2(999, 100, "never-indexed.tmp", UsnReason.FileDelete | UsnReason.Close));
        Assert.NotNull(got);
        Assert.Equal(@"Q:\Docs", got!.ParentPath);
        Assert.Equal(-1, got.Size);
    }

    [Fact]
    public void Rename_into_recycle_bin_is_recycled()
    {
        var index = new VolumeIndex(@"Q:\", IndexMode.Turbo);
        index.SetRecord(0, 5);
        int bin = index.Add(0, "$Recycle.Bin", EntryFlags.Directory | EntryFlags.Hidden, 0, 0, recordNo: 30);
        index.Add(bin, "S-1-5-21-1", EntryFlags.Directory, 0, 0, recordNo: 31);
        int f = index.Add(0, "a.txt", 0, 1, 1, recordNo: 200);
        using var u = new UsnUpdater(index, 'Q', TimeSpan.FromSeconds(1));
        UsnDeletion? got = null;
        u.Deleted += (_, d) => got = d;

        u.ApplyBuffer(UsnRecordParserTests.V2(200, 31, "$RX1Y2Z3.txt", UsnReason.RenameNewName | UsnReason.Close));

        Assert.Equal(DeletionKind.Recycled, got!.Kind);
        Assert.Equal("a.txt", got.Name);
        Assert.Equal(@"Q:\", got.ParentPath);
        Assert.False(index.IsLive(f));
    }

    [Fact]
    public void Posix_delete_marker_is_a_delete()
    {
        var index = new VolumeIndex(@"Q:\", IndexMode.Turbo);
        index.SetRecord(0, 5);
        int ext = index.Add(0, "$Extend", EntryFlags.Directory, 0, 0, recordNo: 11);
        index.Add(ext, "$Deleted", EntryFlags.Directory, 0, 0, recordNo: 12);
        int f = index.Add(0, "notes.md", 0, 9, 9, recordNo: 0x1234);
        using var u = new UsnUpdater(index, 'Q', TimeSpan.FromSeconds(1));
        UsnDeletion? got = null;
        u.Deleted += (_, d) => got = d;

        // Windows 11 POSIX delete: renamed into \$Extend\$Deleted as 24 hex chars whose first 16 are the FRN.
        u.ApplyBuffer(UsnRecordParserTests.V2(0x1234, 12, "0003000000001234" + "00000001", UsnReason.RenameNewName | UsnReason.Close, sequence: 3));

        Assert.Equal(DeletionKind.Deleted, got!.Kind);
        Assert.Equal("notes.md", got.Name);
        Assert.False(index.IsLive(f));
    }

    [Fact]
    public void Plain_move_is_not_a_deletion()
    {
        var (index, docs) = IndexWithDocs();
        index.Add(0, "Other", EntryFlags.Directory, 0, 0, recordNo: 101);
        index.Add(docs, "m.txt", 0, 1, 1, recordNo: 200);
        using var u = new UsnUpdater(index, 'Q', TimeSpan.FromSeconds(1));
        int raised = 0;
        u.Deleted += (_, _) => raised++;
        u.ApplyBuffer(UsnRecordParserTests.V2(200, 101, "m.txt", UsnReason.RenameNewName | UsnReason.Close));
        Assert.Equal(0, raised);
        Assert.True(index.FindByPath(@"Q:\Other\m.txt") > 0);
    }
}

public sealed class DeletionLogTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fh-dl-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Log_captures_persists_and_retags_replaced()
    {
        var index = new VolumeIndex(@"Q:\", IndexMode.Turbo);
        index.SetRecord(0, 5);
        int docs = index.Add(0, "Docs", EntryFlags.Directory, 0, 0, recordNo: 100);
        index.Add(docs, "x.txt", 0, 1, 1, recordNo: 200);
        var store = Path.Combine(_dir, "Q.dlog");
        using var u = new UsnUpdater(index, 'Q', TimeSpan.FromSeconds(1));
        using (var log = new DeletionLog(u, store))
        {
            u.ApplyBuffer(UsnRecordParserTests.V2(200, 100, "x.txt", UsnReason.FileDelete | UsnReason.Close, usn: 1, sequence: 3));
            Assert.Single(log.Entries);
            Assert.Equal(DeletionKind.Deleted, log.Entries[0].Kind);
            // Save-by-replace: a new file with the same name in the same folder right after.
            u.ApplyBuffer(UsnRecordParserTests.V2(201, 100, "x.txt", UsnReason.FileCreate | UsnReason.Close, usn: 2));
            Assert.Equal(DeletionKind.Replaced, log.Entries[0].Kind);
        }
        using var u2 = new UsnUpdater(index, 'Q', TimeSpan.FromSeconds(1));
        using var log2 = new DeletionLog(u2, store);
        Assert.Single(log2.Entries);
        Assert.Equal("x.txt", log2.Entries[0].Name);
        Assert.Equal(DeletionKind.Replaced, log2.Entries[0].Kind);
    }

    [Fact]
    public void Suspended_persistence_does_not_write()
    {
        var index = new VolumeIndex(@"Q:\", IndexMode.Turbo);
        index.SetRecord(0, 5);
        index.Add(0, "y.txt", 0, 1, 1, recordNo: 200);
        var store = Path.Combine(_dir, "S.dlog");
        using var u = new UsnUpdater(index, 'Q', TimeSpan.FromSeconds(1));
        using (var log = new DeletionLog(u, store))
        {
            log.SuspendPersistence(true);
            u.ApplyBuffer(UsnRecordParserTests.V2(200, 5, "y.txt", UsnReason.FileDelete | UsnReason.Close, usn: 1));
            log.Flush();
            Assert.False(File.Exists(store));
            log.SuspendPersistence(false);
            log.Flush();
            Assert.True(File.Exists(store));
        }
    }
}
