using FileHound.Indexing.Interop;
using FileHound.Indexing.Ntfs;

namespace FileHound.Indexing.Tests;

public sealed class TurboTests : IDisposable
{
    private readonly TempTree _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public void UsnUpdater_applies_create_rename_delete()
    {
        var index = new VolumeIndex(@"Q:\", IndexMode.Turbo);
        index.SetRecord(0, 5);
        int dir = index.Add(0, "Docs", EntryFlags.Directory, 0, 0, recordNo: 100);
        using var updater = new UsnUpdater(index, 'Q', TimeSpan.FromSeconds(1));

        Assert.Equal(1, updater.ApplyBuffer(UsnRecordParserTests.V2(200, 100, "new.txt", UsnReason.FileCreate | UsnReason.Close)));
        int e = index.FindByRecord(200);
        Assert.Equal(@"Q:\Docs\new.txt", PathBuilder.GetFullPath(index, e));

        updater.ApplyBuffer(UsnRecordParserTests.V2(200, 5, "ren.txt", UsnReason.RenameNewName | UsnReason.RenameOldName | UsnReason.Close));
        Assert.Equal(@"Q:\ren.txt", PathBuilder.GetFullPath(index, index.FindByRecord(200)));
        Assert.Equal(e, index.FindByRecord(200));

        updater.ApplyBuffer(UsnRecordParserTests.V2(200, 5, "ren.txt", UsnReason.FileDelete | UsnReason.Close));
        Assert.False(index.IsLive(e));
        Assert.Equal(-1, index.FindByRecord(200));

        // A record whose parent is unknown is ignored; multiple records in one buffer are all applied.
        var two = UsnRecordParserTests.V2(300, 999, "orphan.txt", UsnReason.FileCreate)
            .Concat(UsnRecordParserTests.V2(301, 100, "b.txt", UsnReason.FileCreate)).ToArray();
        Assert.Equal(1, updater.ApplyBuffer(two));
        Assert.Equal(-1, index.FindByRecord(300));
        Assert.True(index.FindByRecord(301) > 0);
        Assert.True(index.IsLive(dir));
    }

    [Fact]
    public void UsnUpdater_move_out_of_indexed_tree_deletes_entry()
    {
        // Mirrors an MFT build: $Recycle.Bin is skipped, so its SID folder (record 60) is an unmapped orphan.
        var b = new VolumeIndexBuilder(@"Q:\", IndexMode.Turbo);
        b.AddRecord(100, 5, "Docs", EntryFlags.Directory);
        b.AddRecord(200, 100, "report.docx", 0);
        b.AddRecord(60, 50, "S-1-5-21-1", EntryFlags.Directory); // parent 50 ($Recycle.Bin) was skipped
        b.AddRecord(201, 100, "notes.txt", 0);
        var index = b.Build();
        Assert.Equal(-1, index.FindByRecord(60));
        using var updater = new UsnUpdater(index, 'Q', TimeSpan.FromSeconds(1));
        int report = index.FindByRecord(200);

        // Explorer "Delete" = rename into the recycle bin folder.
        updater.ApplyBuffer(UsnRecordParserTests.V2(200, 60, "$RAB12CD.docx", UsnReason.RenameNewName | UsnReason.Close));
        Assert.False(index.IsLive(report));

        // Moved into a folder we never saw (unknown parent) also leaves the tree.
        int notes = index.FindByRecord(201);
        updater.ApplyBuffer(UsnRecordParserTests.V2(201, 777, "notes.txt", UsnReason.RenameNewName | UsnReason.Close));
        Assert.False(index.IsLive(notes));

        // Creating a file under the recycle bin never adds it.
        Assert.Equal(0, updater.ApplyBuffer(UsnRecordParserTests.V2(300, 60, "$IAB12CD.docx", UsnReason.FileCreate | UsnReason.Close)));
        Assert.Equal(-1, index.FindByRecord(300));
    }

    [Fact]
    public void UsnUpdater_restored_directory_gets_its_subtree_back()
    {
        _t.File(@"proj\src\main.cs", 5);
        _t.File(@"proj\readme.md", 3);
        var b = new VolumeIndexBuilder(_t.Root, IndexMode.Turbo);
        b.AddRecord(10, 5, "proj", EntryFlags.Directory);
        b.AddRecord(11, 10, "src", EntryFlags.Directory);
        b.AddRecord(12, 11, "main.cs", 0);
        b.AddRecord(13, 10, "readme.md", 0);
        var index = b.Build();
        using var updater = new UsnUpdater(index, 'Q', TimeSpan.FromSeconds(1));

        // Deleted to the Recycle Bin (unknown parent) …
        updater.ApplyBuffer(UsnRecordParserTests.V2(10, 999, "$R1.proj", UsnReason.RenameNewName | UsnReason.Close, 0x10));
        Assert.Equal(-1, index.FindByPath(_t.Path(@"proj\src\main.cs")));

        // … and restored: only the folder gets a journal record; its subtree must come back from disk.
        updater.ApplyBuffer(UsnRecordParserTests.V2(10, 5, "proj", UsnReason.RenameNewName | UsnReason.Close, 0x10));
        int main = index.FindByPath(_t.Path(@"proj\src\main.cs"));
        Assert.True(main > 0);

        // The rebuilt subtree is mapped to the files' real NTFS record numbers, so later journal records resolve.
        Assert.True(Kernel32.TryGetRecordNumber(_t.Path(@"proj\src\main.cs"), out long mainRec));
        Assert.True(Kernel32.TryGetRecordNumber(_t.Path(@"proj\src"), out long srcRec));
        Assert.True(Kernel32.TryGetRecordNumber(_t.Path(@"proj\readme.md"), out long readmeRec));
        Assert.Equal(main, index.FindByRecord(mainRec));
        updater.ApplyBuffer(UsnRecordParserTests.V2((ulong)mainRec, (ulong)srcRec, "main.cs", UsnReason.DataExtend | UsnReason.Close));
        Assert.Equal(main, index.FindByRecord(mainRec));
        updater.ApplyBuffer(UsnRecordParserTests.V2((ulong)readmeRec, 10, "readme.md", UsnReason.FileDelete | UsnReason.Close));
        Assert.Equal(-1, index.FindByPath(_t.Path(@"proj\readme.md")));
        int count = 0;
        int src = index.FindByPath(_t.Path(@"proj\src"));
        for (int c = index.FirstChild(src); c > 0; c = index.NextSibling(c)) if (index.IsLive(c)) count++;
        Assert.Equal(1, count); // no duplicates
    }

    [Fact]
    public void UsnUpdater_ignores_cyclic_move()
    {
        var index = new VolumeIndex(@"Q:\", IndexMode.Turbo);
        index.SetRecord(0, 5);
        int a = index.Add(0, "a", EntryFlags.Directory, 0, 0, recordNo: 10);
        int b = index.Add(a, "b", EntryFlags.Directory, 0, 0, recordNo: 11);
        using var updater = new UsnUpdater(index, 'Q', TimeSpan.FromSeconds(1));
        updater.ApplyBuffer(UsnRecordParserTests.V2(10, 11, "a", UsnReason.RenameNewName | UsnReason.Close)); // a into its own child
        Assert.NotEqual(b, index.Parent(a)); // no cycle created (and no hang)
        Assert.Equal(a, index.Parent(b));
    }

    [Fact]
    public async Task MetadataFiller_only_incomplete_fills_missing_entries()
    {
        _t.File(@"a\one.bin", 10);
        _t.File(@"b\two.bin", 20);
        var b = new VolumeIndexBuilder(_t.Root, IndexMode.Turbo);
        b.AddRecord(10, 5, "a", EntryFlags.Directory);
        b.AddRecord(11, 10, "one.bin", 0);
        b.AddRecord(20, 5, "b", EntryFlags.Directory);
        b.AddRecord(21, 20, "two.bin", 0);
        var index = b.Build();
        index.Lock.EnterReadLock();
        try
        {
            index.SetMetadata(index.FindByRecord(11), 999, 1); // "already filled" (deliberately wrong value)
            index.SetMetadata(index.FindByRecord(10), 0, 1);
            index.SetMetadata(index.FindByRecord(20), 0, 1);
        }
        finally { index.Lock.ExitReadLock(); }
        Assert.True(MetadataFiller.HasIncompleteMetadata(index));

        await new MetadataFiller().FillAsync(index, null, CancellationToken.None, onlyIncomplete: true);

        Assert.Equal(999, index.Size(index.FindByRecord(11)));  // directory "a" was complete, so not revisited
        Assert.Equal(20, index.Size(index.FindByRecord(21)));
        Assert.False(MetadataFiller.HasIncompleteMetadata(index));
    }

    [Fact]
    public async Task MetadataFiller_fills_sizes_and_times()
    {
        _t.File(@"a\one.bin", 10);
        _t.File(@"a\two.bin", 20);
        for (int i = 0; i < 30; i++) _t.File($@"big\f{i}.dat", i);
        var b = new VolumeIndexBuilder(_t.Root, IndexMode.Turbo);
        b.AddRecord(10, 5, "a", EntryFlags.Directory);
        b.AddRecord(11, 10, "one.bin", 0);
        b.AddRecord(12, 10, "TWO.BIN", 0);
        b.AddRecord(13, 5, "big", EntryFlags.Directory);
        for (int i = 0; i < 30; i++) b.AddRecord(100 + i, 13, $"f{i}.dat", 0);
        var index = b.Build();

        await new MetadataFiller().FillAsync(index, null, CancellationToken.None);

        Assert.Equal(10, index.Size(index.FindByRecord(11)));
        Assert.Equal(20, index.Size(index.FindByRecord(12)));
        Assert.Equal(29, index.Size(index.FindByRecord(129)));
        Assert.True(index.Flags(index.FindByRecord(10)).HasFlag(EntryFlags.MetadataKnown));
        Assert.True(index.ModifiedTicks(index.FindByRecord(11)) > 0);
    }

    [Fact, Trait("Category", "Elevated")]
    public void MftScanner_scans_system_drive_when_elevated()
    {
        if (!Elevation.IsElevated) return; // requires admin; covered manually via the CLI
        var c = DriveDiscovery.GetDrives().Single(d => d.Letter == 'C');
        var scanner = new MftScanner();
        var v = scanner.Scan(c, [], null, CancellationToken.None);
        string about = $"method={scanner.Method}, fallback={scanner.FallbackReason ?? "none"}, live={v.LiveCount:N0}, root={v.Root}, " +
                       $"Windows={v.FindByPath(@"C:\Windows")}, System32={v.FindByPath(@"C:\Windows\System32")}, explorer={v.FindByPath(@"C:\Windows\explorer.exe")}";
        Assert.True(v.LiveCount > 10_000, about);
        Assert.True(v.FindByPath(@"C:\Windows\explorer.exe") > 0, about);
    }
}
