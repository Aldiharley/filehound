using System.Text;
using FileHound.Core.Recovery;

namespace FileHound.Core.Tests;

public static class RecycleBinFixtures
{
    /// <summary>Builds a version-2 $I file exactly as Windows 10/11 writes it.</summary>
    public static byte[] V2(string path, long size, DateTime deletedUtc)
    {
        var b = new byte[28 + (path.Length + 1) * 2];
        BitConverter.GetBytes(2L).CopyTo(b, 0);
        BitConverter.GetBytes(size).CopyTo(b, 8);
        BitConverter.GetBytes(deletedUtc.ToFileTimeUtc()).CopyTo(b, 16);
        BitConverter.GetBytes((uint)(path.Length + 1)).CopyTo(b, 24);
        Encoding.Unicode.GetBytes(path).CopyTo(b, 28);
        return b;
    }
}

public class RecycleBinMetadataTests
{
    [Fact]
    public void Parses_v2()
    {
        var when = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(RecycleBinMetadata.TryParse(RecycleBinFixtures.V2(@"C:\Users\x\report.docx", 523551, when), out var m));
        Assert.Equal(2, m!.Version);
        Assert.Equal(523551, m.Size);
        Assert.Equal(when, m.DeletedUtc);
        Assert.Equal(@"C:\Users\x\report.docx", m.OriginalPath);
    }

    [Fact]
    public void Parses_real_header_seen_on_this_pc()
    {
        // 02 00 00 00 00 00 00 00 | 1f fa 07 00 00 00 00 00 | 70 68 3c 00 52 2d dd 01 | 56 00 00 00 | 43 00 3a 00 ... (200 bytes)
        var b = RecycleBinFixtures.V2(@"C:\" + new string('a', 82), 0x7FA1F, DateTime.FromFileTimeUtc(0x01DD2D52003C6870));
        Assert.Equal(200, b.Length);
        Assert.True(RecycleBinMetadata.TryParse(b, out var m));
        Assert.Equal(0x7FA1F, m!.Size);
        Assert.Equal(86u, BitConverter.ToUInt32(b, 24));
    }

    [Fact]
    public void Parses_v1()
    {
        var b = new byte[544];
        BitConverter.GetBytes(1L).CopyTo(b, 0);
        BitConverter.GetBytes(77L).CopyTo(b, 8);
        BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(b, 16);
        Encoding.Unicode.GetBytes(@"D:\old.txt").CopyTo(b, 24);
        Assert.True(RecycleBinMetadata.TryParse(b, out var m));
        Assert.Equal(1, m!.Version);
        Assert.Equal(@"D:\old.txt", m.OriginalPath);
        Assert.Equal(77, m.Size);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(27)]
    public void Rejects_truncated(int len) => Assert.False(RecycleBinMetadata.TryParse(new byte[len], out _));

    [Fact]
    public void Rejects_bad_version()
    {
        var b = RecycleBinFixtures.V2(@"C:\a", 1, DateTime.UtcNow);
        BitConverter.GetBytes(9L).CopyTo(b, 0);
        Assert.False(RecycleBinMetadata.TryParse(b, out _));
    }

    [Fact]
    public void Rejects_oversized_char_count()
    {
        var b = RecycleBinFixtures.V2(@"C:\a", 1, DateTime.UtcNow);
        BitConverter.GetBytes(9999u).CopyTo(b, 24);
        Assert.False(RecycleBinMetadata.TryParse(b, out _));
    }

    [Fact]
    public void Rejects_v1_shorter_than_544() => Assert.False(RecycleBinMetadata.TryParse(new byte[100].AsSpan(), out _));

    [Fact]
    public void Candidate_original_path_joins() =>
        Assert.Equal(@"C:\d\f.txt", new RecoveryCandidate(RecoverySource.RecycleBin, "f.txt", @"C:\d", 1, null, null, RecoveryGrade.Excellent, 100, false, null, 0).OriginalPath);
}
