using FileHound.Indexing.Ntfs;
using FileHound.Indexing.Recovery;

namespace FileHound.Indexing.Tests;

public class ClusterBitmapTests
{
    [Fact]
    public void Falls_back_to_bitmap_record_and_reads_bits()
    {
        var vol = new SyntheticVolume(clusters: 256);
        vol.SetAllocated(100, true);
        vol.SetAllocated(101, false);
        vol.SetAllocated(102, true);
        using var r = vol.OpenReader();
        var bm = ClusterBitmap.Load(r);
        Assert.Equal("$Bitmap", bm.Source);
        Assert.Equal(256, bm.TotalClusters);
        Assert.True(bm.IsAllocated(0));          // MFT area
        Assert.True(bm.IsAllocated(100));
        Assert.False(bm.IsAllocated(101));
        Assert.True(bm.IsAllocated(102));
        Assert.True(bm.IsAllocated(999_999));    // out of range: conservative
        var runs = new List<DataRun> { new(0, 100, 3), new(3, -1, 2), new(5, 103, 1) };
        Assert.Equal((2L, 4L), bm.Count(runs, clustersNeeded: 6));
        Assert.Equal((1L, 2L), bm.Count(runs, clustersNeeded: 2));
        Assert.Equal((0L, 0L), bm.Count(runs, clustersNeeded: 0));
    }

    [Fact]
    public void Clusters_outside_the_volume_count_as_allocated_without_walking_them()
    {
        var vol = new SyntheticVolume(clusters: 256);
        using var r = vol.OpenReader();
        var bm = ClusterBitmap.Load(r);
        Assert.Equal((100L, 100L), bm.Count([new DataRun(0, 500, 100)], clustersNeeded: 100));
        Assert.Equal((1L << 40, 1L << 40), bm.Count([new DataRun(0, 300, 1L << 40)], clustersNeeded: 1L << 40));
        Assert.Equal((5L, 10L), bm.Count([new DataRun(0, 251, 10)], clustersNeeded: 10)); // 251..255 free, 256..260 outside
    }
}
