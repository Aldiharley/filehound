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

    [Fact]
    public void MftScanner_scans_system_drive_when_elevated()
    {
        if (!Elevation.IsElevated) return; // requires admin; covered manually via the CLI
        var c = DriveDiscovery.GetDrives().Single(d => d.Letter == 'C');
        var v = new MftScanner().Scan(c, [], null, CancellationToken.None);
        Assert.True(v.LiveCount > 10_000);
        Assert.True(v.FindByPath(@"C:\Windows\explorer.exe") > 0);
    }
}
