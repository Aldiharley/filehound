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
    public async Task Loop_survives_an_unexpected_failure_and_asks_for_a_rescan()
    {
        _t.File(@"docs\a.txt");
        var index = new VolumeIndex(_t.Root, IndexMode.Standard);
        await new DirectoryWalker([], 2).WalkAsync(index, _t.Root, 0, null, CancellationToken.None);

        // Moved-in directories are walked by the updater; this walker blows up on one of them.
        var faulty = new DirectoryWalker([], 2)
        {
            AfterEnumerate = p => { if (p.EndsWith(@"\bad", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("boom"); },
        };
        using var updater = new WatcherUpdater(index, _t.Root, faulty, TimeSpan.FromMilliseconds(100));
        int faults = 0, overflows = 0;
        updater.Faulted += (_, _) => Interlocked.Increment(ref faults);
        updater.Overflowed += (_, _) => Interlocked.Increment(ref overflows);
        updater.Start();

        _outside.File(@"bad\inner.txt");
        Directory.Move(_outside.Path("bad"), _t.Path("bad"));
        TempTree.WaitUntil(() => Volatile.Read(ref faults) > 0, because: "fault reported");
        TempTree.WaitUntil(() => Volatile.Read(ref overflows) > 0, because: "rescan requested");

        var later = _t.File(@"docs\later.txt"); // the loop must still be alive afterwards
        TempTree.WaitUntil(() => index.FindByPath(later) > 0, because: "change applied after the fault");
    }

    [Fact]
    public async Task Paused_watcher_queues_changes_until_resumed()
    {
        _t.File(@"docs\a.txt");
        var index = new VolumeIndex(_t.Root, IndexMode.Standard);
        var walker = new DirectoryWalker([], 2);
        using var updater = new WatcherUpdater(index, _t.Root, walker, TimeSpan.FromMilliseconds(100));
        updater.Start(paused: true);
        var during = _t.File(@"docs\made-during-walk.txt"); // the walk below may or may not see it
        await walker.WalkAsync(index, _t.Root, 0, null, CancellationToken.None);
        File.Delete(_t.Path(@"docs\a.txt"));                  // happens "after the walk read it"
        Thread.Sleep(400);
        Assert.True(index.FindByPath(_t.Path(@"docs\a.txt")) > 0); // still paused: nothing applied
        updater.Resume();
        TempTree.WaitUntil(() => index.FindByPath(_t.Path(@"docs\a.txt")) < 0, because: "queued delete applied");
        Assert.True(index.FindByPath(during) > 0);
        int docs = index.FindByPath(_t.Path("docs"));
        int count = 0;
        for (int c = index.FirstChild(docs); c > 0; c = index.NextSibling(c)) if (index.IsLive(c)) count++;
        Assert.Equal(1, count); // no duplicate of the file created during the walk
    }

    [Fact]
    public async Task Rename_out_of_tree_deletes_entry()
    {
        var f = _t.File(@"docs\leaving.txt");
        var index = new VolumeIndex(_t.Root, IndexMode.Standard);
        var walker = new DirectoryWalker([], 2);
        await walker.WalkAsync(index, _t.Root, 0, null, CancellationToken.None);
        using var updater = new WatcherUpdater(index, _t.Root, walker, TimeSpan.FromMilliseconds(100));
        updater.Start();
        File.Move(f, _outside.Path("left.txt"));
        TempTree.WaitUntil(() => index.FindByPath(f) < 0, because: "moved out of the watched tree");
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
