using FileHound.Core.Carving;
using FileHound.Indexing.Recovery;

namespace FileHound.Indexing.Tests;

public class CarverTests
{
    private static byte[] Png(int w, int h) => FileHound.Core.Tests.Carving.SyntheticFiles.Png(w, h);
    private static byte[] Jpeg(int w, int h, byte[] entropy) => FileHound.Core.Tests.Carving.SyntheticFiles.Jpeg(w, h, entropy);

    private static void Plant(SyntheticVolume vol, long lcn, byte[] data) => data.CopyTo(vol.Image.AsSpan((int)(lcn * SyntheticVolume.Cluster)));

    private static (SyntheticVolume Vol, byte[] Png, byte[] Jpeg) Setup()
    {
        var vol = new SyntheticVolume(clusters: 1024, records: 64);
        var png = Png(8, 8);
        var entropy = new byte[6000];
        for (int i = 0; i < entropy.Length; i++) entropy[i] = (byte)(i % 251 + 1);   // no 0xFF bytes
        var jpg = Jpeg(640, 480, entropy);
        Plant(vol, 300, png);
        Plant(vol, 400, jpg);                       // spans two clusters
        Plant(vol, 500, png); vol.SetAllocated(500, true);   // inside an allocated cluster: must not be found
        Plant(vol, 1020, jpg); for (long l = 1021; l < 1024; l++) vol.SetAllocated(l, true);   // free run ends before the JPEG does
        return (vol, png, jpg);
    }

    [Fact]
    public void Finds_files_in_free_clusters_with_exact_sizes()
    {
        var (vol, png, jpg) = Setup();
        using var r = vol.OpenReader();
        var carver = new Carver(r, ClusterBitmap.Load(r)) { ChunkClusters = 16 };
        var batches = new List<IReadOnlyList<CarvedFile>>();
        carver.Batch += b => batches.Add(b);
        var sink = new Sink();
        var found = carver.Run(sink, CancellationToken.None);
        var last = sink.Last;

        Assert.Equal(2, found.Count);
        var p = Assert.Single(found, f => f.Type.Id == "png");
        Assert.Equal(300, p.StartLcn); Assert.Equal(png.Length, p.Size); Assert.Equal("8×8", p.Info); Assert.Equal("png_300.png", p.SuggestedName);
        var j = Assert.Single(found, f => f.Type.Id == "jpg");
        Assert.Equal(400, j.StartLcn); Assert.Equal(jpg.Length, j.Size); Assert.Equal("640×480", j.Info);
        Assert.Equal(2, batches.Sum(b => b.Count));
        Assert.NotNull(last);
        Assert.Equal(last!.FreeBytes, last.BytesScanned);
        Assert.Equal(2, last.Found);
    }

    /// <summary>Synchronous progress sink (Progress&lt;T&gt; posts to the pool and may deliver out of order).</summary>
    private sealed class Sink : IProgress<CarveProgress>
    {
        public CarveProgress? Last;
        public void Report(CarveProgress value) => Last = value;
    }

    [Fact]
    public void Open_ended_file_at_a_chunk_edge_is_carved_whole()
    {
        // An MP3 of ~334 KB planted so that it starts in the last cluster of a 16-cluster chunk: the first window is one
        // cluster, the validator keeps asking for more, and the carver must grow until the stream's real end.
        var vol = new SyntheticVolume(clusters: 1024, records: 64);
        var mp3 = FileHound.Core.Tests.Carving.MediaValidatorTests.Mp3(800, withId3: false);
        long lcn = SyntheticVolume.MftLcn + 16 + 15;   // last cluster of the first chunk after the MFT area
        Plant(vol, lcn, mp3);
        using var r = vol.OpenReader();
        var found = new Carver(r, ClusterBitmap.Load(r)) { ChunkClusters = 16, TypeFilter = ["mp3"] }.Run(null, CancellationToken.None);
        var hit = Assert.Single(found);
        Assert.Equal(lcn, hit.StartLcn);
        Assert.Equal(mp3.Length, hit.Size);
    }

    [Fact]
    public void Sized_file_jumps_straight_to_its_total_and_the_window_is_reused()
    {
        var vol = new SyntheticVolume(clusters: 1024, records: 64);
        var wav = FileHound.Core.Tests.Carving.MediaValidatorTests.Wav(300_000);
        Plant(vol, 200, wav);
        var wav2 = FileHound.Core.Tests.Carving.MediaValidatorTests.Wav(250_000);
        Plant(vol, 400, wav2);
        using var r = vol.OpenReader();
        var found = new Carver(r, ClusterBitmap.Load(r)) { ChunkClusters = 4, TypeFilter = ["wav"] }.Run(null, CancellationToken.None);
        Assert.Equal(2, found.Count);
        Assert.Equal(wav.Length, found[0].Size);
        Assert.Equal(wav2.Length, found[1].Size);
    }

    [Fact]
    public void Magic_at_an_offset_is_found_and_a_throwing_validator_is_a_reject()
    {
        var vol = new SyntheticVolume(clusters: 512, records: 64);
        var mp4 = FileHound.Core.Tests.Carving.MediaValidatorTests.Mp4();
        Plant(vol, 150, mp4);
        using var r = vol.OpenReader();
        var found = new Carver(r, ClusterBitmap.Load(r)) { TypeFilter = ["mp4"] }.Run(null, CancellationToken.None);
        Assert.Equal(mp4.Length, Assert.Single(found).Size);
    }

    [Fact]
    public void Type_filter_limits_the_search()
    {
        var (vol, _, _) = Setup();
        using var r = vol.OpenReader();
        var found = new Carver(r, ClusterBitmap.Load(r)) { TypeFilter = ["png"] }.Run(null, CancellationToken.None);
        Assert.Equal("png", Assert.Single(found).Type.Id);
    }

    [Fact]
    public void Window_grows_for_a_file_larger_than_the_chunk()
    {
        var (vol, _, jpg) = Setup();
        using var r = vol.OpenReader();
        var found = new Carver(r, ClusterBitmap.Load(r)) { ChunkClusters = 1, TypeFilter = ["jpg"] }.Run(null, CancellationToken.None);
        Assert.Equal(jpg.Length, Assert.Single(found).Size);
    }

    [Fact]
    public void Cancel_and_pause_resume()
    {
        var (vol, _, _) = Setup();
        using var r = vol.OpenReader();
        var carver = new Carver(r, ClusterBitmap.Load(r)) { ChunkClusters = 1 };
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => carver.Run(null, cts.Token));

        carver.Pause();
        Assert.True(carver.IsPaused);
        var task = Task.Run(() => carver.Run(null, CancellationToken.None));
        Assert.False(task.Wait(300));
        carver.Resume();
        Assert.True(task.Wait(10_000));
        Assert.Equal(2, task.Result.Count);
    }

    [Fact]
    public void Free_runs_are_contiguous_and_cover_the_volume()
    {
        var vol = new SyntheticVolume(clusters: 64);
        vol.SetAllocated(30, true);
        using var r = vol.OpenReader();
        var runs = Carver.FreeRuns(ClusterBitmap.Load(r)).ToList();
        Assert.Equal(2, runs.Count);
        Assert.Equal(30, runs[0].Lcn + runs[0].Clusters);
        Assert.Equal((31L, 33L), runs[1]);
    }
}
