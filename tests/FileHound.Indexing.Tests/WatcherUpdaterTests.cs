namespace FileHound.Indexing.Tests;

public sealed class WatcherUpdaterTests : IDisposable
{
    private readonly TempTree _t = new();
    private readonly TempTree _outside = new();

    public void Dispose()
    {
        _t.Dispose();
        _outside.Dispose();
    }

    [Fact]
    public async Task Applies_create_rename_move_in_and_delete()
    {
        _t.File(@"docs\a.txt");
        var index = new VolumeIndex(_t.Root, IndexMode.Standard);
        var walker = new DirectoryWalker([], 2);
        await walker.WalkAsync(index, _t.Root, 0, null, CancellationToken.None);

        using var updater = new WatcherUpdater(index, _t.Root, walker, TimeSpan.FromMilliseconds(100));
        int applied = 0;
        updater.Applied += (_, _) => Interlocked.Increment(ref applied);
        updater.Start();

        var created = _t.File(@"docs\new.txt", 42);
        TempTree.WaitUntil(() => index.FindByPath(created) > 0, because: "create");
        TempTree.WaitUntil(() => index.Size(index.FindByPath(created)) == 42, because: "size");

        File.Move(created, _t.Path(@"docs\renamed.txt"));
        TempTree.WaitUntil(() => index.FindByPath(_t.Path(@"docs\renamed.txt")) > 0 && index.FindByPath(created) < 0, because: "rename");

        _outside.File(@"pkg\inner\deep.txt", 7);
        Directory.Move(_outside.Path("pkg"), _t.Path("pkg"));
        TempTree.WaitUntil(() => index.FindByPath(_t.Path(@"pkg\inner\deep.txt")) > 0, because: "move-in subtree");

        int deep = index.FindByPath(_t.Path(@"pkg\inner\deep.txt"));
        Directory.Delete(_t.Path("pkg"), recursive: true);
        TempTree.WaitUntil(() => !index.IsLive(deep), because: "delete subtree");

        File.WriteAllBytes(_t.Path(@"docs\a.txt"), new byte[500]);
        TempTree.WaitUntil(() => index.Size(index.FindByPath(_t.Path(@"docs\a.txt"))) == 500, because: "change");

        Assert.True(applied > 0);
    }

    [Fact]
    public async Task Directory_created_in_place_with_children_has_no_duplicates()
    {
        var index = new VolumeIndex(_t.Root, IndexMode.Standard);
        var walker = new DirectoryWalker([], 2);
        await walker.WalkAsync(index, _t.Root, 0, null, CancellationToken.None);
        using var updater = new WatcherUpdater(index, _t.Root, walker, TimeSpan.FromMilliseconds(300));
        updater.Start();

        for (int i = 0; i < 20; i++) _t.File($@"burst\f{i}.txt");
        TempTree.WaitUntil(() => index.FindByPath(_t.Path(@"burst\f19.txt")) > 0);
        Thread.Sleep(600); // let any trailing events drain

        int dir = index.FindByPath(_t.Path("burst"));
        int children = 0;
        for (int c = index.FirstChild(dir); c > 0; c = index.NextSibling(c))
            if (index.IsLive(c)) children++;
        Assert.Equal(20, children);
    }
}
