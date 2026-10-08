using FileHound.Core.Carving;

namespace FileHound.Core.Tests.Carving;

public class ImageValidatorTests
{
    public static IEnumerable<object[]> Samples() =>
    [
        ["jpg", SyntheticFiles.Jpeg(640, 480), "640×480"],
        ["png", SyntheticFiles.Png(1920, 1080), "1920×1080"],
        ["gif", SyntheticFiles.Gif(32, 16), "32×16"],
        ["bmp", SyntheticFiles.Bmp(5, 3), "5×3"],
        ["tif", SyntheticFiles.Tiff(100, 50, 300), "100×50"],
        ["webp", SyntheticFiles.WebP(300, 200), "300×200"],
    ];

    private static CarveResult Run(string id, ReadOnlySpan<byte> data) => Signatures.ById(id)!.Validate(data);

    [Theory, MemberData(nameof(Samples))]
    public void Valid_file_reports_exact_size_and_info(string id, byte[] file, string info)
    {
        var r = Run(id, file);
        Assert.Equal(CarveStatus.Ok, r.Status);
        Assert.Equal(file.Length, r.Size);
        Assert.Equal(info, r.Info);
    }

    [Theory, MemberData(nameof(Samples))]
    public void Garbage_after_the_file_does_not_change_its_size(string id, byte[] file, string _)
    {
        var r = Run(id, [.. file, .. SyntheticFiles.Garbage(777)]);
        Assert.Equal(CarveStatus.Ok, r.Status);
        Assert.Equal(file.Length, r.Size);
    }

    [Theory, MemberData(nameof(Samples))]
    public void Truncated_file_asks_for_more(string id, byte[] file, string _)
    {
        for (int cut = 1; cut < Math.Min(file.Length, 40); cut += 3)
        {
            var r = Run(id, file.AsSpan(0, file.Length - cut));
            Assert.True(r.Status == CarveStatus.NeedMore, $"{id} cut {cut}: {r.Status}");
        }
    }

    [Theory, MemberData(nameof(Samples))]
    public void Magic_dispatch_finds_the_type(string id, byte[] file, string _)
    {
        var type = Signatures.ById(id)!;
        Assert.True(type.MatchesMagic(file));
        Assert.Contains(type, Signatures.ByFirstByte(file[0]));
        foreach (var other in Signatures.All.Where(t => t.Id != id && !t.Magics.Any(m => type.Magics.Any(x => x.AsSpan().SequenceEqual(m)))))
            Assert.False(other.MatchesMagic(file), $"{other.Id} matched {id}");
    }

    [Fact]
    public void Png_with_bad_crc_is_rejected() =>
        Assert.Equal(CarveStatus.Reject, ImageValidators.Png(SyntheticFiles.Png(4, 4, badCrc: true)).Status);

    [Fact]
    public void Jpeg_with_a_non_marker_byte_between_segments_is_rejected()
    {
        var f = SyntheticFiles.Jpeg(8, 8);
        f[2] = 0x00;
        Assert.Equal(CarveStatus.Reject, ImageValidators.Jpeg(f).Status);
    }

    [Fact]
    public void Jpeg_entropy_data_with_embedded_ff00_and_rst_is_walked()
    {
        var f = SyntheticFiles.Jpeg(8, 8, entropy: [0xFF, 0x00, 0xFF, 0xD3, 0xFF, 0x00, 0x01]);
        Assert.Equal(f.Length, ImageValidators.Jpeg(f).Size);
    }

    [Fact]
    public void Gif_with_an_unknown_block_is_rejected()
    {
        var f = SyntheticFiles.Gif(4, 4);
        f[13 + 6] = 0x7E; // where the extension introducer was
        Assert.Equal(CarveStatus.Reject, ImageValidators.Gif(f).Status);
    }

    [Fact]
    public void Bmp_with_an_unknown_dib_header_is_rejected()
    {
        var f = SyntheticFiles.Bmp(2, 2);
        f[14] = 99;
        Assert.Equal(CarveStatus.Reject, ImageValidators.Bmp(f).Status);
    }

    [Fact]
    public void Tiff_with_an_odd_ifd_offset_is_rejected()
    {
        var f = SyntheticFiles.Tiff(2, 2, 12);
        f[4] = 9;
        Assert.Equal(CarveStatus.Reject, ImageValidators.Tiff(f).Status);
    }

    [Fact]
    public void Tiff_big_endian_is_accepted()
    {
        var f = SyntheticFiles.Tiff(2, 2, 12);
        // Rewrite as MM: flip the header and every 16/32-bit field this file uses.
        var mm = (byte[])f.Clone();
        mm[0] = (byte)'M'; mm[1] = (byte)'M'; mm[2] = 0; mm[3] = 0x2A;
        Array.Reverse(mm, 4, 4);
        Array.Reverse(mm, 8, 2);
        for (int i = 0; i < 5; i++)
        {
            int p = 10 + i * 12;
            Array.Reverse(mm, p, 2); Array.Reverse(mm, p + 2, 2); Array.Reverse(mm, p + 4, 4); Array.Reverse(mm, p + 8, 4);
        }
        var r = ImageValidators.Tiff(mm);
        Assert.Equal(CarveStatus.Ok, r.Status);
        Assert.Equal(f.Length, r.Size);
    }

    [Fact]
    public void Webp_with_another_riff_form_is_rejected()
    {
        var f = SyntheticFiles.WebP(2, 2);
        "WAVE"u8.CopyTo(f.AsSpan(8));
        Assert.Equal(CarveStatus.Reject, ImageValidators.WebP(f).Status);
    }

    [Fact]
    public void Random_bytes_never_throw_or_report_sizes_past_the_span()
    {
        var rng = new Random(123);
        foreach (var type in Signatures.All)
        {
            for (int i = 0; i < 300; i++)
            {
                var sample = SampleFor(type.Id);
                var mutated = (byte[])sample.Clone();
                int flips = rng.Next(1, 6);
                for (int k = 0; k < flips; k++) mutated[rng.Next(mutated.Length)] = (byte)rng.Next(256);
                var r = type.Validate(mutated);
                if (r.Status == CarveStatus.Ok) Assert.True(r.Size > 0 && r.Size <= mutated.Length, $"{type.Id}: size {r.Size} of {mutated.Length}");
            }
        }
    }

    private static byte[] SampleFor(string id) => id switch
    {
        "jpg" => SyntheticFiles.Jpeg(16, 16),
        "png" => SyntheticFiles.Png(16, 16),
        "gif" => SyntheticFiles.Gif(16, 16),
        "bmp" => SyntheticFiles.Bmp(4, 4),
        "tif" => SyntheticFiles.Tiff(4, 4, 48),
        "webp" => SyntheticFiles.WebP(16, 16),
        _ => SyntheticFiles.Garbage(256),
    };
}

public class SignaturesTests
{
    [Fact]
    public void Ids_and_extensions_are_unique_and_every_type_dispatches()
    {
        Assert.Equal(Signatures.All.Count, Signatures.All.Select(t => t.Id).Distinct().Count());
        foreach (var t in Signatures.All)
        {
            Assert.StartsWith(".", t.Extension);
            Assert.True(t.MaxSize > 0);
            if (t.MagicOffset == 0) foreach (var m in t.Magics) Assert.Contains(t, Signatures.ByFirstByte(m[0]));
            else Assert.Contains(t, Signatures.AtOffset);
        }
        Assert.Null(Signatures.ById("nope"));
    }
}
