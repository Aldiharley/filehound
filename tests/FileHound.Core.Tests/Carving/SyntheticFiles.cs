using System.Buffers.Binary;
using System.IO.Hashing;

namespace FileHound.Core.Tests.Carving;

/// <summary>Hand-built, structurally valid files for validator tests (lengths and CRCs computed, content meaningless).</summary>
public static class SyntheticFiles
{
    public static byte[] Jpeg(int width, int height, byte[]? entropy = null)
    {
        entropy ??= [0x12, 0x34, 0xFF, 0x00, 0x56, 0xFF, 0xD0, 0x78, 0x9A];   // includes FF00 stuffing and an RST0 marker
        var ms = new MemoryStream();
        ms.Write([0xFF, 0xD8]);
        Segment(ms, 0xE0, [.. "JFIF\0"u8.ToArray(), 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        var sof = new byte[15];
        sof[0] = 8;
        BinaryPrimitives.WriteUInt16BigEndian(sof.AsSpan(1), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(sof.AsSpan(3), (ushort)width);
        sof[5] = 3;
        for (int c = 0; c < 3; c++) { sof[6 + c * 3] = (byte)(c + 1); sof[7 + c * 3] = 0x11; sof[8 + c * 3] = 0; }
        Segment(ms, 0xC0, sof);
        Segment(ms, 0xDA, [3, 1, 0, 2, 0x11, 3, 0x11, 0, 0x3F, 0]);
        ms.Write(entropy);
        ms.Write([0xFF, 0xD9]);
        return ms.ToArray();
    }

    private static void Segment(MemoryStream ms, byte marker, byte[] payload)
    {
        ms.Write([0xFF, marker]);
        Span<byte> len = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(len, (ushort)(payload.Length + 2));
        ms.Write(len);
        ms.Write(payload);
    }

    public static byte[] Png(int width, int height, bool badCrc = false)
    {
        var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8; ihdr[9] = 2;
        Chunk(ms, "IHDR", ihdr, badCrc);
        Chunk(ms, "IDAT", [0x78, 0x9C, 0x63, 0x60, 0x00, 0x00, 0x00, 0x02, 0x00, 0x01], false);
        Chunk(ms, "IEND", [], false);
        return ms.ToArray();
    }

    private static void Chunk(MemoryStream ms, string type, byte[] data, bool badCrc)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        ms.Write(len);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        ms.Write(typeBytes);
        ms.Write(data);
        uint crc = Crc32.HashToUInt32([.. typeBytes, .. data]);
        if (badCrc) crc ^= 0xFF;
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        ms.Write(c);
    }

    public static byte[] Gif(int width, int height)
    {
        var ms = new MemoryStream();
        ms.Write("GIF89a"u8);
        Span<byte> wh = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(wh, (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(wh[2..], (ushort)height);
        ms.Write(wh);
        ms.Write([0x80, 0, 0]);                       // GCT present, 2 entries
        ms.Write([0, 0, 0, 255, 255, 255]);           // the table
        ms.Write([0x21, 0xF9, 4, 0, 0, 0, 0, 0]);     // graphic control extension
        ms.Write([0x2C, 0, 0, 0, 0]);                 // image descriptor: left/top
        ms.Write(wh);
        ms.Write([0x00]);                             // no LCT
        ms.Write([2, 3, 0x44, 0x01, 0x00, 0]);        // LZW min code 2, one 3-byte sub-block, terminator
        ms.Write([0x3B]);
        return ms.ToArray();
    }

    public static byte[] Bmp(int width, int height)
    {
        int row = (width * 3 + 3) & ~3;
        int size = 54 + row * height;
        var b = new byte[size];
        b[0] = (byte)'B'; b[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(2), (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(10), 54);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(22), height);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(28), 24);
        for (int i = 54; i < size; i++) b[i] = (byte)(i * 7);
        return b;
    }

    public static byte[] Tiff(int width, int height, int stripBytes)
    {
        // II*\0, IFD at 8 with 5 entries, strip data right after the IFD.
        const int entries = 5;
        int ifdSize = 2 + entries * 12 + 4;
        int stripOffset = 8 + ifdSize;
        var b = new byte[stripOffset + stripBytes];
        b[0] = (byte)'I'; b[1] = (byte)'I'; b[2] = 0x2A; b[3] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), entries);
        void Entry(int i, ushort tag, ushort type, uint count, uint value)
        {
            int p = 10 + i * 12;
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p), tag);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p + 2), type);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p + 4), count);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p + 8), value);
        }
        Entry(0, 256, 4, 1, (uint)width);
        Entry(1, 257, 4, 1, (uint)height);
        Entry(2, 273, 4, 1, (uint)stripOffset);
        Entry(3, 278, 4, 1, (uint)height);
        Entry(4, 279, 4, 1, (uint)stripBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(10 + entries * 12), 0);
        for (int i = stripOffset; i < b.Length; i++) b[i] = (byte)i;
        return b;
    }

    public static byte[] WebP(int width, int height)
    {
        const int chunkLen = 20;
        var b = new byte[12 + 8 + chunkLen];
        "RIFF"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)(b.Length - 8));
        "WEBP"u8.CopyTo(b.AsSpan(8));
        "VP8 "u8.CopyTo(b.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), chunkLen);
        b[20] = 0x10; b[21] = 0x02; b[22] = 0x00;     // frame tag
        b[23] = 0x9D; b[24] = 0x01; b[25] = 0x2A;     // start code
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(26), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(28), (ushort)height);
        return b;
    }

    public static byte[] Garbage(int n, int seed = 7)
    {
        var g = new byte[n];
        new Random(seed).NextBytes(g);
        return g;
    }
}
