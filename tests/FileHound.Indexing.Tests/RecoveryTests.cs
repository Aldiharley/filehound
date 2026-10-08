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

public sealed class RecycleBinSourceTests : IDisposable
{
    private readonly TempTree _drive = new();     // fake drive root holding $Recycle.Bin
    private readonly TempTree _home = new();      // where "original" paths live
    private readonly string _sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;

    public void Dispose() { _drive.Dispose(); _home.Dispose(); }

    private string Bin => Path.Combine(_drive.Root, "$Recycle.Bin", _sid);

    private void Put(string id, string originalPath, byte[]? data, bool folder = false)
    {
        Directory.CreateDirectory(Bin);
        var when = new DateTime(2026, 10, 7, 9, 30, 0, DateTimeKind.Utc);
        File.WriteAllBytes(Path.Combine(Bin, "$I" + id), FileHound.Core.Tests.RecycleBinFixtures.V2(originalPath, data?.Length ?? 0, when));
        if (folder) { var d = Directory.CreateDirectory(Path.Combine(Bin, "$R" + id)); File.WriteAllText(Path.Combine(d.FullName, "inner.txt"), "x"); }
        else if (data is not null) File.WriteAllBytes(Path.Combine(Bin, "$R" + id), data);
    }

    private DriveDescriptor Drive => new('Z', _drive.Root, "NTFS", "Test", 1000, 500, 1, false);

    [Fact]
    public void Enumerates_files_folders_and_orphans()
    {
        Put("AAA111.txt", Path.Combine(_home.Root, "docs", "notes.txt"), [1, 2, 3]);
        Put("BBB222", Path.Combine(_home.Root, "proj"), null, folder: true);
        Put("CCC333.pdf", Path.Combine(_home.Root, "lost.pdf"), null);                 // $I only
        File.WriteAllBytes(Path.Combine(Bin, "$RDDD444.jpg"), [9, 9]);                 // $R only

        var items = RecycleBinSource.Enumerate([Drive], allUsers: false);
        Assert.Equal(4, items.Count);
        var notes = items.Single(i => i.DisplayName == "notes.txt");
        Assert.Equal(3, notes.Size); Assert.True(notes.HasData); Assert.False(notes.IsDirectory);
        Assert.True(items.Single(i => i.DisplayName == "proj").IsDirectory);
        var lost = items.Single(i => i.DisplayName == "lost.pdf");
        Assert.False(lost.HasData);
        var orphan = items.Single(i => i.DisplayName == "$RDDD444.jpg");
        Assert.False(orphan.HasMetadata); Assert.Equal(2, orphan.Size);

        var cands = RecycleBinSource.ToCandidates(items).ToList();
        Assert.Equal(RecoveryGrade.Excellent, cands.Single(c => c.Name == "notes.txt").Grade);
        Assert.Equal(Path.Combine(_home.Root, "docs"), cands.Single(c => c.Name == "notes.txt").OriginalFolder);
        Assert.Equal("data file missing", cands.Single(c => c.Name == "lost.pdf").Detail);
        Assert.Equal("no metadata — original name unknown", cands.Single(c => c.Name == "$RDDD444.jpg").Detail);
    }

    [Fact]
    public void Restore_moves_back_and_never_overwrites()
    {
        var original = Path.Combine(_home.Root, "docs", "notes.txt");
        Put("AAA111.txt", original, [1, 2, 3]);
        var item = RecycleBinSource.Enumerate([Drive], false).Single();

        var restored = RecycleBinSource.Restore(item, keepBoth: false);
        Assert.Equal(original, restored);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(original));
        Assert.False(File.Exists(item.MetadataPath));
        Assert.False(File.Exists(item.DataPath));

        Put("AAA112.txt", original, [4]);
        var again = RecycleBinSource.Enumerate([Drive], false).Single();
        Assert.Throws<IOException>(() => RecycleBinSource.Restore(again, keepBoth: false));
        var kept = RecycleBinSource.Restore(again, keepBoth: true);
        Assert.Equal(Path.Combine(_home.Root, "docs", "notes (restored).txt"), kept);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(original));
    }

    [Fact]
    public void CopyTo_leaves_bin_untouched()
    {
        Put("BBB222", Path.Combine(_home.Root, "proj"), null, folder: true);
        var item = RecycleBinSource.Enumerate([Drive], false).Single();
        var dest = Path.Combine(_home.Root, "out");
        var copied = RecycleBinSource.CopyTo(item, dest);
        Assert.Equal(Path.Combine(dest, "proj"), copied);
        Assert.True(File.Exists(Path.Combine(copied, "inner.txt")));
        Assert.True(Directory.Exists(item.DataPath));
        Assert.True(File.Exists(item.MetadataPath));
    }

    [Fact]
    public void Other_users_bins_are_skipped_unless_all_users()
    {
        Put("AAA111.txt", Path.Combine(_home.Root, "a.txt"), [1]);
        var other = Path.Combine(_drive.Root, "$Recycle.Bin", "S-1-5-21-999-999-999-1234");
        Directory.CreateDirectory(other);
        File.WriteAllBytes(Path.Combine(other, "$IZZZ.txt"), FileHound.Core.Tests.RecycleBinFixtures.V2(@"C:\o.txt", 1, DateTime.UtcNow));
        File.WriteAllBytes(Path.Combine(other, "$RZZZ.txt"), [1]);
        Assert.Single(RecycleBinSource.Enumerate([Drive], allUsers: false));
        Assert.Equal(2, RecycleBinSource.Enumerate([Drive], allUsers: true).Count);
    }
}

public class JournalGapOracleTests
{
    [Fact, Trait("Category", "Elevated")]
    public void Record_16_is_in_use_and_a_huge_record_number_is_free()
    {
        if (!Elevation.IsElevated) return;
        Assert.Equal(JournalGapOracle.SlotState.Reused, JournalGapOracle.Check('C', 16));
        Assert.Equal(JournalGapOracle.SlotState.Free, JournalGapOracle.Check('C', long.MaxValue / 4));
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
