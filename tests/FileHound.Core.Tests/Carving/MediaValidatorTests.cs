using System.Buffers.Binary;
using FileHound.Core.Carving;

namespace FileHound.Core.Tests.Carving;

public class MediaValidatorTests
{
    // ---- builders -------------------------------------------------------------------------------------------------
    public static byte[] Wav(int dataBytes)
    {
        var b = new byte[44 + dataBytes];
        "RIFF"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)(b.Length - 8));
        "WAVE"u8.CopyTo(b.AsSpan(8));
        "fmt "u8.CopyTo(b.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(22), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24), 44100);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(28), 44100 * 4);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(32), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(34), 16);
        "data"u8.CopyTo(b.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(40), (uint)dataBytes);
        return b;
    }

    private static byte[] Box(string type, params byte[][] payload)
    {
        int len = 8 + payload.Sum(p => p.Length);
        var b = new byte[len];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)len);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
        int p = 8;
        foreach (var x in payload) { x.CopyTo(b, p); p += x.Length; }
        return b;
    }

    public static byte[] Mp4()
    {
        var mvhd = new byte[8 + 100];
        BinaryPrimitives.WriteUInt32BigEndian(mvhd, 108);
        "mvhd"u8.CopyTo(mvhd.AsSpan(4));
        BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(20), 1000);   // timescale
        BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(24), 90_000); // duration = 90 s
        var tkhd = new byte[92];
        BinaryPrimitives.WriteUInt32BigEndian(tkhd, 92);
        "tkhd"u8.CopyTo(tkhd.AsSpan(4));
        BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(84), 1280 << 16);
        BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(88), 720 << 16);
        return [.. Box("ftyp", "isom"u8.ToArray(), new byte[4], "isomiso2"u8.ToArray()), .. Box("moov", mvhd, Box("trak", tkhd)), .. Box("mdat", new byte[500])];
    }

    private static byte[] Vint(long value, int length)
    {
        var b = new byte[length];
        for (int i = length - 1; i >= 0; i--) { b[i] = (byte)value; value >>= 8; }
        b[0] |= (byte)(0x80 >> (length - 1));
        return b;
    }

    public static byte[] Mkv()
    {
        byte[] ebmlHeader = [0x1A, 0x45, 0xDF, 0xA3, .. Vint(4, 1), 0x42, 0x86, 0x81, 0x01];           // EBMLVersion = 1
        byte[] timecodeScale = [0x2A, 0xD7, 0xB1, 0x83, 0x0F, 0x42, 0x40];                            // 1,000,000
        var dur = new byte[2 + 1 + 4]; dur[0] = 0x44; dur[1] = 0x89; dur[2] = 0x84; BinaryPrimitives.WriteSingleBigEndian(dur.AsSpan(3), 125_000f); // 125 s in ms
        byte[] infoBody = [.. timecodeScale, .. dur];
        byte[] info = [0x15, 0x49, 0xA9, 0x66, .. Vint(infoBody.Length, 1), .. infoBody];
        byte[] cluster = [0x1F, 0x43, 0xB6, 0x75, .. Vint(10, 1), .. new byte[10]];
        byte[] segBody = [.. info, .. cluster];
        byte[] segment = [0x18, 0x53, 0x80, 0x67, .. Vint(segBody.Length, 2), .. segBody];
        return [.. ebmlHeader, .. segment];
    }

    public static byte[] Ogg(int pages)
    {
        var ms = new MemoryStream();
        for (int i = 0; i < pages; i++)
        {
            var page = new byte[27 + 1 + 50];
            "OggS"u8.CopyTo(page);
            page[5] = (byte)(i == 0 ? 0x02 : i == pages - 1 ? 0x04 : 0);
            page[26] = 1;
            page[27] = 50;
            ms.Write(page);
        }
        return ms.ToArray();
    }

    public static byte[] Mp3(int frames, bool withId3 = true, bool withTag = false)
    {
        var ms = new MemoryStream();
        if (withId3) ms.Write([(byte)'I', (byte)'D', (byte)'3', 3, 0, 0, 0, 0, 0, 20, .. new byte[20]]);
        // MPEG-1 Layer III, 128 kbps, 44.1 kHz, no padding → 417 bytes
        for (int i = 0; i < frames; i++)
        {
            var f = new byte[417];
            f[0] = 0xFF; f[1] = 0xFB; f[2] = 0x90; f[3] = 0x00;
            ms.Write(f);
        }
        if (withTag) { var tag = new byte[128]; "TAG"u8.CopyTo(tag); ms.Write(tag); }
        return ms.ToArray();
    }

    public static byte[] Flac(int frames)
    {
        var ms = new MemoryStream();
        ms.Write("fLaC"u8);
        var si = new byte[34];
        si[10] = 0x0A; si[11] = 0xC4; si[12] = 0x42;   // 44100 Hz, 2 channels, 16-bit
        BinaryPrimitives.WriteUInt32BigEndian(si.AsSpan(14), 441_000);   // 10 s
        ms.Write([0x80, 0, 0, 34]);
        ms.Write(si);
        for (int i = 0; i < frames; i++)
        {
            byte[] header = [0xFF, 0xF8, 0x69, 0x18, (byte)i];   // fixed blocksize 4096 (0x6), 44.1 kHz (0x9), stereo, 16-bit; frame number i
            byte crc = 0;
            foreach (var x in header) { crc ^= x; for (int k = 0; k < 8; k++) crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1); }
            ms.Write(header);
            ms.WriteByte(crc);
            ms.Write(new byte[200]);
        }
        return ms.ToArray();
    }

    // ---- tests ----------------------------------------------------------------------------------------------------
    public static IEnumerable<object[]> Samples() =>
    [
        ["wav", Wav(1000), "44.1 kHz · 16-bit · stereo"],
        ["mp4", Mp4(), "1280×720 · 1:30"],
        ["mkv", Mkv(), "2:05"],
        ["ogg", Ogg(3), null!],
        ["mp3", Mp3(6, withTag: true), "128 kbps · 44.1 kHz"],
    ];

    [Theory, MemberData(nameof(Samples))]
    public void Valid_file_reports_exact_size_and_info(string id, byte[] file, string? info)
    {
        var r = Signatures.ById(id)!.Validate(file);
        // A container with no terminator (MP4) that ends exactly at the span end is a valid prefix that may continue.
        Assert.True(r.Status == CarveStatus.Ok || (id == "mp4" && r.Status == CarveStatus.NeedMore), r.Status.ToString());
        Assert.Equal(file.Length, r.Size);
        Assert.Equal(info, r.Info);
    }

    [Theory, MemberData(nameof(Samples))]
    public void Garbage_after_the_file_does_not_change_its_size(string id, byte[] file, string? _)
    {
        var r = Signatures.ById(id)!.Validate([.. file, .. SyntheticFiles.Garbage(999)]);
        Assert.Equal(CarveStatus.Ok, r.Status);
        Assert.Equal(file.Length, r.Size);
    }

    [Theory]
    [InlineData("wav")] [InlineData("mp4")] [InlineData("mkv")] [InlineData("ogg")]
    public void Truncated_container_asks_for_more(string id)
    {
        var file = (byte[])Samples().First(s => (string)s[0] == id)[1];
        var r = Signatures.ById(id)!.Validate(file.AsSpan(0, file.Length - 7));
        Assert.Equal(CarveStatus.NeedMore, r.Status);
    }

    [Fact]
    public void Mp3_ends_at_the_last_whole_frame_and_includes_an_id3v1_tag()
    {
        var file = Mp3(5, withTag: true);
        Assert.Equal(file.Length, MediaValidators.Mp3([.. file, 0x00, 0x11]).Size);
        Assert.Equal(CarveStatus.NeedMore, MediaValidators.Mp3(Mp3(2, withId3: false)).Status);   // fewer than four frames so far
        Assert.Equal(CarveStatus.Reject, MediaValidators.Mp3([0xFF, 0xFB, 0x90, 0x00, .. new byte[413], 0x00, 0x00, 0x00, 0x00]).Status);
    }

    [Fact]
    public void Open_ended_streams_report_the_valid_prefix_and_ask_for_more()
    {
        // An MP3 cut mid-frame: everything up to the last whole frame is usable, but the carver should read on.
        var mp3 = Mp3(8, withId3: false);
        var r = MediaValidators.Mp3(mp3.AsSpan(0, mp3.Length - 100));
        Assert.Equal(CarveStatus.NeedMore, r.Status);
        Assert.Equal(417 * 7, r.Size);
        // A FLAC whose last frame runs to the span end.
        var flac = Flac(4);
        var f = MediaValidators.Flac(flac);
        Assert.Equal(CarveStatus.NeedMore, f.Status);
        Assert.True(f.Size > 0 && f.Size < flac.Length);
        // A WAV that states its size asks for exactly that.
        var wav = Wav(100_000);
        var w = MediaValidators.Riff(wav.AsSpan(0, 5000));
        Assert.Equal(CarveStatus.NeedMore, w.Status);
        Assert.Equal(wav.Length, w.Required);
    }

    [Fact]
    public void Hostile_riff_chunk_length_is_rejected_not_thrown()
    {
        var wav = Wav(10);
        "LIST"u8.CopyTo(wav.AsSpan(12));                                   // a chunk the walk must step over …
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16), 0x7FFFFFF7);   // … whose length would wrap an int
        Assert.Equal(CarveStatus.Reject, MediaValidators.Riff(wav).Status);
    }

    [Fact]
    public void Flac_walks_frames_by_sync_and_crc()
    {
        var file = Flac(4);
        var r = MediaValidators.Flac([.. file, .. new byte[1 << 20]]);   // a megabyte without a sync: the last frame ended somewhere in there
        Assert.Equal(CarveStatus.Ok, r.Status);
        Assert.StartsWith("44.1 kHz · stereo · 0:10", r.Info);
        Assert.True(r.Size >= file.Length - 206 && r.Size <= file.Length, $"size {r.Size} of {file.Length}");
        Assert.Equal(CarveStatus.Reject, MediaValidators.Flac([.. "fLaC"u8.ToArray(), 0x81, 0, 0, 4, 1, 2, 3, 4, .. new byte[40]]).Status);
    }

    [Fact]
    public void Mp4_with_a_zero_size_box_or_unknown_top_level_box_is_handled()
    {
        var file = Mp4();
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(file.Length - 508), 0);   // a zero-size box after a complete moov: the file ends before it
        var z = MediaValidators.IsoBmff(file);
        Assert.Equal(CarveStatus.Ok, z.Status);
        Assert.Equal(file.Length - 508, z.Size);
        Assert.Equal(CarveStatus.Reject, MediaValidators.IsoBmff([.. file.AsSpan(0, 24), 0, 0, 0, 0, (byte)'m', (byte)'d', (byte)'a', (byte)'t']).Status);   // zero-size box with nothing before it
        var ok = Mp4();
        var r = MediaValidators.IsoBmff([.. ok, .. Box("zzzz", new byte[4])]);
        Assert.Equal(ok.Length, r.Size);
    }

    [Fact]
    public void Random_mutations_never_throw()
    {
        var rng = new Random(99);
        foreach (var s in Samples().Concat([["flac", Flac(3), null!]]))
        {
            var type = Signatures.ById((string)s[0])!;
            var sample = (byte[])s[1];
            for (int i = 0; i < 300; i++)
            {
                var m = (byte[])sample.Clone();
                for (int k = rng.Next(1, 6); k > 0; k--) m[rng.Next(m.Length)] = (byte)rng.Next(256);
                var r = type.Validate(m);
                if (r.Status == CarveStatus.Ok) Assert.True(r.Size > 0 && r.Size <= m.Length, $"{type.Id}: {r.Size}/{m.Length}");
            }
        }
    }
}
