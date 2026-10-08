using FileHound.Core.Carving;
using FileHound.Core.Recovery;
using FileHound.Core.Tests.Carving;
using FileHound.Indexing.Recovery;

namespace FileHound.Indexing.Tests;

public sealed class CarveWriterTests : IDisposable
{
    private readonly string _dest = Directory.CreateTempSubdirectory("fh-carve-").FullName;
    public void Dispose() => Directory.Delete(_dest, true);

    private static string Sha(byte[] b) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(b));

    [Fact]
    public void Recovers_the_exact_range_with_hash_and_byte_run()
    {
        var vol = new SyntheticVolume(clusters: 256);
        var png = SyntheticFiles.Png(3, 3);
        png.CopyTo(vol.Image.AsSpan(120 * SyntheticVolume.Cluster));
        using var r = vol.OpenReader();
        var carved = new CarvedFile(Signatures.ById("png")!, 120, png.Length, "3×3");
        var path = Path.Combine(_dest, carved.SuggestedName);
        var (bytes, sha, runs, finalPath) = CarveWriter.Recover(r, carved, path, CancellationToken.None);
        Assert.Equal(path, finalPath);
        Assert.Equal(png.Length, bytes);
        Assert.Equal(Sha(png), sha);
        Assert.Equal(png, File.ReadAllBytes(path));
        Assert.Equal(new ByteRun(0, png.Length, 120L * 4096), Assert.Single(runs));
        Assert.False(File.Exists(path + ":Zone.Identifier"));
    }

    [Fact]
    public void Executables_get_the_mark_of_the_web_and_never_overwrite()
    {
        var vol = new SyntheticVolume(clusters: 256);
        var exe = DocumentValidatorTests.Pe(dll: false);
        exe.CopyTo(vol.Image.AsSpan(130 * SyntheticVolume.Cluster));
        using var r = vol.OpenReader();
        var carved = new CarvedFile(Signatures.ById("exe")!, 130, exe.Length, "x64 EXE");
        var path = Path.Combine(_dest, carved.SuggestedName);
        Assert.EndsWith(".exe", path);
        CarveWriter.Recover(r, carved, path, CancellationToken.None);
        Assert.Equal("[ZoneTransfer]\r\nZoneId=3\r\n", File.ReadAllText(path + ":Zone.Identifier"));
        Assert.Throws<IOException>(() => CarveWriter.Recover(r, carved, path, CancellationToken.None));
        Assert.Equal(exe, File.ReadAllBytes(path));
    }

    [Fact]
    public void Range_outside_the_volume_is_refused_without_leaving_a_file()
    {
        var vol = new SyntheticVolume(clusters: 64);
        using var r = vol.OpenReader();
        var path = Path.Combine(_dest, "bad.bin");
        Assert.Throws<InvalidDataException>(() => CarveWriter.Recover(r, new CarvedFile(Signatures.ById("png")!, 60, 5 * 4096, null), path, CancellationToken.None));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Carved_files_that_start_where_an_undelete_candidate_starts_are_dropped()
    {
        var type = Signatures.ById("png")!;
        var carved = new List<CarvedFile> { new(type, 100, 10, null), new(type, 200, 10, null) };
        var rec = new UndeleteRecord(9, 1, 5, 1, "a.png", false, 10, 10, 4096, 0, 0, false, null, [0x21, 0x01, 0x64, 0x00, 0x00], 0, 0);   // one cluster at LCN 100
        var undelete = new List<RecoveryCandidate> { new(RecoverySource.Undelete, "a.png", null, 10, null, null, RecoveryGrade.Excellent, 100, false, null, rec) };
        var kept = CarveWriter.Deduplicate(carved, undelete);
        Assert.Equal(200, Assert.Single(kept).StartLcn);
    }

    [Fact]
    public void Suggested_name_uses_the_subtype_extension() =>
        Assert.Equal("zip_77.docx", new CarvedFile(Signatures.ById("zip")!, 77, 1, "docx").SuggestedName);
}
