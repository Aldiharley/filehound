using FileHound.Core.Search;

namespace FileHound.Indexing.Tests;

public sealed class IndexManagerTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly TempTree _data = new();

    public void Dispose()
    {
        _tree.Dispose();
        _data.Dispose();
    }

    private IndexOptions Options(Func<IReadOnlyList<DriveDescriptor>>? source = null) => new(
        _data.Root, [], PreferTurbo: false,
        DriveSource: source ?? (() => [new DriveDescriptor('Z', _tree.Root, "NTFS", "Test", 1000, 500, 0x1234, false)]),
        DrivePollInterval: TimeSpan.FromMilliseconds(200));

    private static int Count(IndexManager m, string q) => new SearchEngine().Search(m.Volumes, new SearchRequest(q, Fuzzy: false)).TotalCount;

    [Fact]
    public async Task Indexes_fake_drive_and_reaches_ready()
    {
        _tree.File(@"photos\holiday.jpg", 10);
        await using var m = new IndexManager(Options());
        await m.StartAsync();
        await m.WaitForIdleAsync();
        var d = Assert.Single(m.Drives);
        Assert.Equal(DriveStatus.Ready, d.Status);
        Assert.Equal(IndexMode.Standard, d.Mode);
        Assert.True(d.MetadataComplete);
        Assert.Equal(1, Count(m, "holiday.jpg"));
    }

    [Fact]
    public async Task Snapshot_is_loaded_on_next_start()
    {
        _tree.File(@"docs\budget.xlsx", 10);
        await using (var m = new IndexManager(Options()))
        {
            await m.StartAsync();
            await m.WaitForIdleAsync();
            await m.SaveSnapshotsAsync();
        }
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(_data.Root, "index"), "*.fhx"));

        await using var m2 = new IndexManager(Options());
        await m2.StartAsync(); // returns after snapshots are published, before the refresh walk completes
        Assert.Equal(1, Count(m2, "budget.xlsx"));
        await m2.WaitForIdleAsync();
        Assert.Equal(1, Count(m2, "budget.xlsx"));
    }

    [Fact]
    public async Task Old_snapshot_index_is_released_after_refresh()
    {
        _tree.File(@"docs\budget.xlsx", 10);
        await using (var m = new IndexManager(Options()))
        {
            await m.StartAsync();
            await m.WaitForIdleAsync();
            await m.SaveSnapshotsAsync();
        }

        await using var m2 = new IndexManager(Options());
        await m2.StartAsync();
        var weak = WeakFirstVolume(m2);
        await m2.WaitForIdleAsync();
        Assert.False(IsCurrentVolume(weak, m2), "refresh should have swapped in a new index");
        // The drive task that just completed may still be unwinding on another thread, and its frames root the old
        // index for a few more microseconds. A leak, by contrast, never clears: so collect repeatedly, briefly.
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (weak.IsAlive && deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            if (weak.IsAlive) await Task.Delay(20);
        }
        Assert.False(weak.IsAlive, "snapshot index is still referenced after the refresh swap");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference WeakFirstVolume(IndexManager m) => new(m.Volumes[0]);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool IsCurrentVolume(WeakReference weak, IndexManager m) => ReferenceEquals(weak.Target, m.Volumes[0]);

    [Fact]
    public async Task Live_change_raises_IndexChanged()
    {
        await using var m = new IndexManager(Options());
        await m.StartAsync();
        await m.WaitForIdleAsync();
        int changed = 0;
        m.IndexChanged += (_, _) => Interlocked.Increment(ref changed);
        _tree.File("fresh.txt");
        TempTree.WaitUntil(() => Count(m, "fresh.txt") == 1 && changed > 0, 4000);
    }

    [Fact]
    public async Task Removed_drive_goes_offline()
    {
        bool present = true;
        var drive = new DriveDescriptor('Z', _tree.Root, "NTFS", "Test", 1000, 500, 0x1234, false);
        await using var m = new IndexManager(Options(() => present ? [drive] : []));
        await m.StartAsync();
        await m.WaitForIdleAsync();
        present = false;
        TempTree.WaitUntil(() => m.Drives.Single().Status == DriveStatus.Offline, 4000);
        Assert.Empty(m.Volumes);
    }

    [Fact]
    public async Task Rescan_keeps_previous_index_searchable_and_coalesces()
    {
        for (int i = 0; i < 300; i++) _tree.File($@"d{i % 30}\file{i}.txt");
        _tree.File("keeper.txt");
        await using var m = new IndexManager(Options());
        await m.StartAsync();
        await m.WaitForIdleAsync();
        Assert.Equal(1, Count(m, "keeper.txt"));

        // Several concurrent rescans (as from overflow bursts + the user) must not race or leak.
        var rescans = Enumerable.Range(0, 5).Select(_ => Task.Run(() => m.RescanAsync('Z'))).ToArray();
        Assert.Equal(1, Count(m, "keeper.txt")); // still searchable while rescanning
        await Task.WhenAll(rescans);
        Assert.Equal(1, Count(m, "keeper.txt"));
        await m.WaitForIdleAsync();
        Assert.Equal(DriveStatus.Ready, m.Drives.Single().Status);
        Assert.Equal(1, Count(m, "keeper.txt"));

        _tree.File("after.txt");
        TempTree.WaitUntil(() => Count(m, "after.txt") == 1, 4000, "live updates still attached after rescans");
    }

    [Fact]
    public async Task Exclusions_can_change_at_runtime()
    {
        _tree.File(@"secret\hidden-plan.txt");
        await using var m = new IndexManager(Options());
        await m.StartAsync();
        await m.WaitForIdleAsync();
        Assert.Equal(1, Count(m, "hidden-plan"));
        m.SetExcludedPaths([_tree.Path("secret")]);
        await m.RescanAsync('Z');
        await m.WaitForIdleAsync();
        Assert.Equal(0, Count(m, "hidden-plan"));
    }

    [Fact]
    public async Task Rescan_rebuilds_index()
    {
        await using var m = new IndexManager(Options());
        await m.StartAsync();
        await m.WaitForIdleAsync();
        _tree.File("later.txt");
        await m.RescanAsync('Z');
        await m.WaitForIdleAsync();
        Assert.Equal(1, Count(m, "later.txt"));
    }
}
