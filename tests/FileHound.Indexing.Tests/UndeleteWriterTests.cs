using FileHound.Core.Recovery;
using FileHound.Indexing.Ntfs;
using FileHound.Indexing.Recovery;

namespace FileHound.Indexing.Tests;

public sealed class UndeleteWriterTests : IDisposable
{
    private readonly string _dest = Directory.CreateTempSubdirectory("fh-undel-").FullName;
    public void Dispose() => Directory.Delete(_dest, true);

    private static string Sha(byte[] b) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(b));

    [Fact]
    public void Recovers_non_resident_file_with_sparse_run_truncated_to_real_size()
    {
        var vol = new SyntheticVolume(clusters: 512, records: 64);
        var a = new byte[4096]; Array.Fill(a, (byte)7); vol.WriteCluster(100, a);
        var b = new byte[4096]; Array.Fill(b, (byte)9); vol.WriteCluster(101, b);
        vol.SetAllocated(100, false); vol.SetAllocated(101, false);
        // runs: 2 clusters @100, then 1 sparse cluster; real size 10000 cuts into the sparse cluster
        byte[] runs = [0x21, 0x02, 0x64, 0x00, 0x01, 0x01, 0x00];
        var rec = new UndeleteRecord(30, 1, 5, 1, "f.bin", false, 10000, 10000, 12288, 0, 0, false, null, runs, 0, 0);
        using var r = vol.OpenReader();
        var path = Path.Combine(_dest, "f.bin");
        var (bytes, sha, grade, byteRuns) = UndeleteWriter.Recover(r, ClusterBitmap.Load(r), rec, RecoveryGrade.Excellent, path, CancellationToken.None);
        var data = File.ReadAllBytes(path);
        Assert.Equal(10000, bytes);
        Assert.Equal(10000, data.Length);
        Assert.Equal(7, data[0]);
        Assert.Equal(9, data[4096]);
        Assert.Equal(0, data[9999]);
        Assert.Equal(Sha(data), sha);
        Assert.Equal(RecoveryGrade.Excellent, grade);
        Assert.Equal(new ByteRun(0, 8192, 100 * 4096), byteRuns[0]);
        Assert.Single(byteRuns);
    }

    [Fact]
    public void Compressed_unit_is_decompressed()
    {
        var vol = new SyntheticVolume(clusters: 512, records: 64);
        // One 16-cluster unit: 1 allocated cluster holding an LZNT1 chunk ("ABC" + back-reference → "ABCABCABC"), 15 sparse.
        var chunk = new byte[4096];
        byte[] body = [0b0000_1000, (byte)'A', (byte)'B', (byte)'C', 0x03, 0x20];
        BitConverter.GetBytes((ushort)(0xB000 | (body.Length - 1))).CopyTo(chunk, 0);
        body.CopyTo(chunk, 2);
        vol.WriteCluster(200, chunk);
        vol.SetAllocated(200, false);
        byte[] runs = [0x21, 0x01, 0xC8, 0x00, 0x01, 0x0F, 0x00];
        var rec = new UndeleteRecord(31, 1, 5, 1, "c.txt", false, 9, 9, 65536, MftRecord.DataCompressed, 4, false, null, runs, 0, 0);
        using var r = vol.OpenReader();
        var path = Path.Combine(_dest, "c.txt");
        var (bytes, _, grade, byteRuns) = UndeleteWriter.Recover(r, ClusterBitmap.Load(r), rec, RecoveryGrade.Excellent, path, CancellationToken.None);
        Assert.Equal(9, bytes);
        Assert.Equal("ABCABCABC", File.ReadAllText(path));
        Assert.Equal(RecoveryGrade.Excellent, grade);
        Assert.Equal(200 * 4096, byteRuns.Single().ImageOffset);
    }

    [Fact]
    public void Fully_stored_compression_unit_is_copied_raw()
    {
        var vol = new SyntheticVolume(clusters: 512, records: 64);
        var data = new byte[16 * 4096];
        new Random(1).NextBytes(data);
        for (int i = 0; i < 16; i++) { vol.WriteCluster(240 + i, data.AsSpan(i * 4096, 4096)); vol.SetAllocated(240 + i, false); }
        byte[] runs = [0x21, 0x10, 0xF0, 0x00, 0x00];
        var rec = new UndeleteRecord(34, 1, 5, 1, "raw.bin", false, data.Length, data.Length, data.Length, MftRecord.DataCompressed, 4, false, null, runs, 0, 0);
        using var r = vol.OpenReader();
        var path = Path.Combine(_dest, "raw.bin");
        var (_, sha, _, _) = UndeleteWriter.Recover(r, ClusterBitmap.Load(r), rec, RecoveryGrade.Excellent, path, CancellationToken.None);
        Assert.Equal(Sha(data), sha);
    }

    [Fact]
    public void Clusters_reused_since_the_scan_downgrade_the_grade()
    {
        var vol = new SyntheticVolume(clusters: 512, records: 64);
        vol.SetAllocated(300, false); vol.SetAllocated(301, true);
        byte[] runs = [0x21, 0x02, 0x2C, 0x01, 0x00];
        var rec = new UndeleteRecord(32, 1, 5, 1, "d.bin", false, 8192, 8192, 8192, 0, 0, false, null, runs, 0, 0);
        using var r = vol.OpenReader();
        var (bytes, _, grade, _) = UndeleteWriter.Recover(r, ClusterBitmap.Load(r), rec, RecoveryGrade.Excellent, Path.Combine(_dest, "d.bin"), CancellationToken.None);
        Assert.Equal(RecoveryGrade.Partial, grade);
        Assert.Equal(8192, bytes);
    }

    [Fact]
    public void Resident_data_and_timestamps()
    {
        var vol = new SyntheticVolume();
        var created = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var rec = new UndeleteRecord(33, 1, 5, 1, "r.txt", false, 5, 5, 0, 0, 0, true, "hello!!"u8.ToArray(), null, created.Ticks, created.Ticks);
        using var r = vol.OpenReader();
        var path = Path.Combine(_dest, "r.txt");
        var (bytes, _, _, byteRuns) = UndeleteWriter.Recover(r, ClusterBitmap.Load(r), rec, RecoveryGrade.Excellent, path, CancellationToken.None);
        Assert.Equal(5, bytes);
        Assert.Equal("hello", File.ReadAllText(path));
        Assert.Equal(created, File.GetLastWriteTimeUtc(path));
        Assert.Empty(byteRuns);
    }

    [Fact]
    public void Never_overwrites_an_existing_destination()
    {
        var vol = new SyntheticVolume();
        var rec = new UndeleteRecord(35, 1, 5, 1, "x.txt", false, 1, 1, 0, 0, 0, true, [1], null, 0, 0);
        using var r = vol.OpenReader();
        var path = Path.Combine(_dest, "x.txt");
        File.WriteAllText(path, "keep");
        Assert.Throws<IOException>(() => UndeleteWriter.Recover(r, ClusterBitmap.Load(r), rec, RecoveryGrade.Excellent, path, CancellationToken.None));
        Assert.Equal("keep", File.ReadAllText(path));
    }

    [Fact]
    public void Run_past_the_volume_end_is_rejected()
    {
        var vol = new SyntheticVolume(clusters: 256);
        byte[] runs = [0x31, 0x01, 0x00, 0x00, 0x10, 0x00]; // 1 cluster at LCN 0x100000
        var rec = new UndeleteRecord(36, 1, 5, 1, "bad.bin", false, 100, 100, 4096, 0, 0, false, null, runs, 0, 0);
        using var r = vol.OpenReader();
        Assert.Throws<InvalidDataException>(() => UndeleteWriter.Recover(r, ClusterBitmap.Load(r), rec, RecoveryGrade.Excellent, Path.Combine(_dest, "bad.bin"), CancellationToken.None));
    }
}
