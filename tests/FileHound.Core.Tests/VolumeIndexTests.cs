namespace FileHound.Core.Tests;

public class VolumeIndexTests
{
    private static VolumeIndex Sample(out int docs, out int report)
    {
        var v = new VolumeIndex(@"C:\", IndexMode.Standard, 4);
        var users = v.Add(0, "Users", EntryFlags.Directory, 0, 0);
        docs = v.Add(users, "Docs", EntryFlags.Directory, 0, 0);
        report = v.Add(docs, "Report Final.PDF", EntryFlags.MetadataKnown, 2048, 638000000000000000);
        return v;
    }

    [Fact]
    public void Root_path_is_drive_root()
    {
        var v = new VolumeIndex(@"E:\", IndexMode.Standard);
        Assert.Equal(@"E:\", PathBuilder.GetFullPath(v, 0));
        Assert.Equal('E', v.DriveLetter);
        Assert.Equal("E:", v.Name(0).ToString());
    }

    [Fact]
    public void Directory_root_supported()
    {
        var v = new VolumeIndex(@"D:\tmp\x\", IndexMode.Standard);
        var f = v.Add(0, "a.txt", 0, 1, 1);
        Assert.Equal(@"D:\tmp\x\a.txt", PathBuilder.GetFullPath(v, f));
        Assert.Equal(f, v.FindByPath(@"d:\TMP\x\a.txt"));
    }

    [Fact]
    public void Full_path_joins_ancestors()
    {
        var v = Sample(out _, out var r);
        Assert.Equal(@"C:\Users\Docs\Report Final.PDF", PathBuilder.GetFullPath(v, r));
        Assert.Equal(@"C:\Users\Docs", PathBuilder.GetParentPath(v, r));
    }

    [Fact]
    public void Fold_name_is_lowercase()
    {
        var v = Sample(out _, out var r);
        Assert.Equal("report final.pdf", v.FoldName(r).ToString());
        Assert.Equal(FileCategory.Document, v.Category(r));
        Assert.Equal(2048, v.Size(r));
    }

    [Fact]
    public void Depth_counts_from_root()
    {
        var v = Sample(out _, out var r);
        Assert.Equal(3, v.Depth(r));
    }

    [Fact]
    public void Grows_past_capacity()
    {
        var v = new VolumeIndex(@"C:\", IndexMode.Standard, 2);
        for (int i = 0; i < 5000; i++) v.Add(0, $"f{i}.txt", 0, 0, 0);
        Assert.Equal(5000, v.LiveCount);
        Assert.Equal("f4999.txt", v.Name(5000).ToString());
    }

    [Fact]
    public void Rename_moves_and_renames()
    {
        var v = Sample(out _, out var r);
        v.Rename(r, 0, "moved.pdf");
        Assert.Equal(@"C:\moved.pdf", PathBuilder.GetFullPath(v, r));
        Assert.Equal(1, v.Depth(r));
    }

    [Fact]
    public void Rename_directory_updates_descendant_depths()
    {
        var v = Sample(out var d, out var r);
        v.Rename(d, 0, "Docs");
        Assert.Equal(2, v.Depth(r));
        Assert.Equal(@"C:\Docs\Report Final.PDF", PathBuilder.GetFullPath(v, r));
    }

    [Fact]
    public void Delete_dir_removes_subtree()
    {
        var v = Sample(out var d, out var r);
        v.Delete(d);
        Assert.False(v.IsLive(d));
        Assert.False(v.IsLive(r));
        Assert.Equal(1, v.LiveCount); // Users only (root not counted)
        Assert.Equal(-1, v.FindByPath(@"C:\Users\Docs"));
    }

    [Fact]
    public void FindByPath_is_case_insensitive()
    {
        var v = Sample(out _, out var r);
        Assert.Equal(r, v.FindByPath(@"c:\users\DOCS\report final.pdf"));
        Assert.Equal(0, v.FindByPath(@"C:\"));
        Assert.Equal(-1, v.FindByPath(@"C:\nope"));
        Assert.Equal(-1, v.FindByPath(@"E:\Users"));
    }

    [Fact]
    public void FindChild_tracks_renames()
    {
        var v = Sample(out var d, out var r);
        Assert.Equal(-1, v.FindChild(d, "x")); // builds map
        v.Rename(r, d, "new.pdf");
        Assert.Equal(r, v.FindChild(d, "NEW.pdf"));
        Assert.Equal(-1, v.FindChild(d, "report final.pdf"));
        var added = v.Add(d, "later.txt", 0, 0, 0);
        Assert.Equal(added, v.FindChild(d, "later.txt"));
    }

    [Fact]
    public void Builder_resolves_parents_out_of_order()
    {
        var b = new VolumeIndexBuilder(@"C:\", IndexMode.Turbo);
        b.AddRecord(100, 50, "child.txt", 0);
        b.AddRecord(50, 5, "parent", EntryFlags.Directory);
        b.AddRecord(200, 999, "orphan.txt", 0);
        var v = b.Build();
        var c = v.FindByRecord(100);
        Assert.Equal(@"C:\parent\child.txt", PathBuilder.GetFullPath(v, c));
        Assert.Equal(2, v.Depth(c));
        Assert.False(v.IsLive(v.FindByRecord(200)));
        Assert.Equal(0, v.FindByRecord(5));
        Assert.Equal(2, v.LiveCount);
    }

    [Fact]
    public void Builder_handles_deep_chains_in_reverse_order()
    {
        var b = new VolumeIndexBuilder(@"C:\", IndexMode.Turbo);
        for (int i = 2000; i >= 1; i--) b.AddRecord(100 + i, i == 1 ? 5 : 100 + i - 1, $"d{i}", EntryFlags.Directory);
        var v = b.Build();
        Assert.Equal(255, v.Depth(v.FindByRecord(100 + 2000))); // capped
        Assert.StartsWith(@"C:\d1\d2\d3\", PathBuilder.GetFullPath(v, v.FindByRecord(100 + 2000)));
    }

    [Fact]
    public void Record_map_add_and_lookup()
    {
        var v = new VolumeIndex(@"C:\", IndexMode.Turbo);
        var e = v.Add(0, "x.txt", 0, 0, 0, recordNo: 123456);
        Assert.Equal(e, v.FindByRecord(123456));
        Assert.Equal(-1, v.FindByRecord(7));
    }

    [Fact]
    public void AncestorFoldContains_checks_chain()
    {
        var v = Sample(out _, out var r);
        Assert.True(PathBuilder.AncestorFoldContains(v, r, "docs"));
        Assert.True(PathBuilder.AncestorFoldContains(v, r, "ers"));
        Assert.False(PathBuilder.AncestorFoldContains(v, r, "windows"));
    }

    [Fact]
    public void TrimExcess_keeps_data_and_allows_growth()
    {
        var v = new VolumeIndex(@"C:\", IndexMode.Turbo, 10_000);
        for (int i = 0; i < 100; i++) v.Add(0, $"file{i}.txt", 0, i, i, recordNo: 1000 + i);
        long before = v.ApproximateBytes;
        v.TrimExcess();
        Assert.True(v.ApproximateBytes < before);
        Assert.Equal("file99.txt", v.Name(100).ToString());
        Assert.Equal(100, v.FindByRecord(1099));
        int e = v.Add(0, "after-trim.txt", 0, 0, 0);
        Assert.Equal(e, v.FindByPath(@"C:\after-trim.txt"));
    }

    [Fact]
    public void Metadata_update_sets_flag()
    {
        var v = new VolumeIndex(@"C:\", IndexMode.Turbo);
        var e = v.Add(0, "x.bin", 0, 0, 0);
        Assert.False(v.Flags(e).HasFlag(EntryFlags.MetadataKnown));
        v.SetMetadata(e, 99, 5);
        Assert.True(v.Flags(e).HasFlag(EntryFlags.MetadataKnown));
        Assert.Equal(99, v.Size(e));
    }
}
