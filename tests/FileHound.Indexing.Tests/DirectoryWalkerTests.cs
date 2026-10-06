namespace FileHound.Indexing.Tests;

public sealed class DirectoryWalkerTests : IDisposable
{
    private readonly TempTree _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public async Task Walks_tree_with_metadata_hidden_excludes_and_junctions()
    {
        _t.File(@"a\b\c.txt", 123);
        _t.File(@"a\d.md", 5);
        var hidden = _t.File("hidden.txt");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        _t.File(@"skip\x.txt");
        _t.File(@"$Recycle.Bin\trash.txt");
        bool junction = _t.Junction("link", "a");

        var index = new VolumeIndex(_t.Root, IndexMode.Standard);
        var progress = new ListProgress<ScanProgress>();
        var walker = new DirectoryWalker([_t.Path("skip")], workers: 4) { ProgressInterval = TimeSpan.FromMilliseconds(1) };
        var result = await walker.WalkAsync(index, _t.Root, VolumeIndex.RootEntry, progress, CancellationToken.None);

        int c = index.FindByPath(_t.Path(@"a\b\c.txt"));
        Assert.True(c > 0);
        Assert.Equal(123, index.Size(c));
        Assert.True(index.Flags(c).HasFlag(EntryFlags.MetadataKnown));
        Assert.True(index.ModifiedTicks(c) > 0);
        Assert.True(index.IsDirectory(index.FindByPath(_t.Path(@"a\b"))));
        Assert.True(index.Flags(index.FindByPath(hidden)).HasFlag(EntryFlags.Hidden));
        Assert.Equal(-1, index.FindByPath(_t.Path("skip")));
        Assert.Equal(-1, index.FindByPath(_t.Path(@"$Recycle.Bin")));
        if (junction)
        {
            int link = index.FindByPath(_t.Path("link"));
            Assert.True(link > 0);
            Assert.True(index.Flags(link).HasFlag(EntryFlags.ReparsePoint));
            Assert.Equal(-1, index.FindByPath(_t.Path(@"link\d.md")));
        }
        Assert.Equal(index.LiveCount, result.Entries);
        Assert.Equal(1.0, result.Fraction);
        Assert.NotEmpty(progress.Items);
    }

    [Fact]
    public async Task Walks_subtree_into_existing_entry()
    {
        var index = new VolumeIndex(_t.Root, IndexMode.Standard);
        var sub = _t.Dir("late");
        _t.File(@"late\inner\z.bin", 9);
        int subEntry = index.Add(0, "late", EntryFlags.Directory, 0, 0);
        await new DirectoryWalker([], 2).WalkAsync(index, sub, subEntry, null, CancellationToken.None);
        Assert.Equal(9, index.Size(index.FindByPath(_t.Path(@"late\inner\z.bin"))));
    }

    [Fact]
    public async Task Cancellation_stops_walk()
    {
        for (int i = 0; i < 50; i++) _t.File($@"d{i}\f.txt");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var index = new VolumeIndex(_t.Root, IndexMode.Standard);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DirectoryWalker([], 2).WalkAsync(index, _t.Root, 0, null, cts.Token));
    }
}
