using System.Buffers.Binary;
using System.IO.Hashing;

namespace FileHound.Core.Carving;

/// <summary>Image formats. Each validator walks the structure and returns the exact size, or NeedMore when it runs off the span.</summary>
public static class ImageValidators
{
    // ------------------------------------------------------------------ JPEG
    /// <summary>Marker walk: SOI, length-prefixed segments, SOS entropy data (skipping FF00 stuffing and RSTn), EOI.</summary>
    public static CarveResult Jpeg(ReadOnlySpan<byte> d)
    {
        if (d.Length < 4) return CarveResult.NeedMore;
        if (d[0] != 0xFF || d[1] != 0xD8 || d[2] != 0xFF) return CarveResult.Reject;
        string? info = null;
        int p = 2;
        for (int guard = 0; guard < 100_000; guard++)
        {
            if (p + 2 > d.Length) return CarveResult.NeedMore;
            if (d[p] != 0xFF) return CarveResult.Reject;
            byte marker = d[p + 1];
            if (marker == 0xFF) { p++; continue; }                 // fill bytes
            if (marker == 0xD9) return CarveResult.Ok(p + 2, info);   // EOI
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { p += 2; continue; }   // standalone
            if (marker == 0x00) return CarveResult.Reject;
            if (p + 4 > d.Length) return CarveResult.NeedMore;
            int length = BinaryPrimitives.ReadUInt16BigEndian(d[(p + 2)..]);
            if (length < 2) return CarveResult.Reject;
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC) && info is null)
            {
                if (p + 9 > d.Length) return CarveResult.NeedMore;
                int height = BinaryPrimitives.ReadUInt16BigEndian(d[(p + 5)..]), width = BinaryPrimitives.ReadUInt16BigEndian(d[(p + 7)..]);
                info = $"{width}×{height}";
            }
            p += 2 + length;
            if (marker == 0xDA)
            {
                // Entropy-coded data until a marker that is not FF00 or a restart marker.
                for (; ; p++)
                {
                    if (p + 1 >= d.Length) return CarveResult.NeedMore;
                    if (d[p] != 0xFF) continue;
                    byte next = d[p + 1];
                    if (next == 0x00 || (next >= 0xD0 && next <= 0xD7)) { p++; continue; }
                    if (next == 0xFF) continue;
                    break;
                }
            }
        }
        return CarveResult.Reject;
    }

    // ------------------------------------------------------------------ PNG
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Chunk walk with CRC-32 over type + data; IEND ends the file.</summary>
    public static CarveResult Png(ReadOnlySpan<byte> d)
    {
        if (d.Length < 8) return CarveResult.NeedMore;
        if (!d[..8].SequenceEqual(PngSignature)) return CarveResult.Reject;
        string? info = null;
        int p = 8;
        for (int guard = 0; guard < 1_000_000; guard++)
        {
            if (p + 8 > d.Length) return CarveResult.NeedMore;
            uint length = BinaryPrimitives.ReadUInt32BigEndian(d[p..]);
            if (length > 0x7FFF_FFFF) return CarveResult.Reject;
            var type = d.Slice(p + 4, 4);
            foreach (byte c in type) if (!char.IsAsciiLetter((char)c)) return CarveResult.Reject;
            long end = (long)p + 12 + length;
            if (end > d.Length) return CarveResult.NeedMore;
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(d[(int)(end - 4)..]);
            if (Crc32.HashToUInt32(d.Slice(p + 4, 4 + (int)length)) != crc) return CarveResult.Reject;
            if (type.SequenceEqual("IHDR"u8) && length >= 8)
                info = $"{BinaryPrimitives.ReadUInt32BigEndian(d[(p + 8)..])}×{BinaryPrimitives.ReadUInt32BigEndian(d[(p + 12)..])}";
            p = (int)end;
            if (type.SequenceEqual("IEND"u8)) return CarveResult.Ok(p, info);
        }
        return CarveResult.Reject;
    }

    // ------------------------------------------------------------------ GIF
    /// <summary>Header, logical screen (+ global colour table), then image/extension blocks until the trailer.</summary>
    public static CarveResult Gif(ReadOnlySpan<byte> d)
    {
        if (d.Length < 13) return CarveResult.NeedMore;
        if (!d[..6].SequenceEqual("GIF87a"u8) && !d[..6].SequenceEqual("GIF89a"u8)) return CarveResult.Reject;
        string info = $"{BinaryPrimitives.ReadUInt16LittleEndian(d[6..])}×{BinaryPrimitives.ReadUInt16LittleEndian(d[8..])}";
        int p = 13;
        byte packed = d[10];
        if ((packed & 0x80) != 0) p += 3 * (1 << ((packed & 7) + 1));
        for (int guard = 0; guard < 1_000_000; guard++)
        {
            if (p >= d.Length) return CarveResult.NeedMore;
            switch (d[p])
            {
                case 0x3B: return CarveResult.Ok(p + 1, info);
                case 0x2C:
                {
                    if (p + 10 > d.Length) return CarveResult.NeedMore;
                    byte f = d[p + 9];
                    p += 10;
                    if ((f & 0x80) != 0) p += 3 * (1 << ((f & 7) + 1));
                    p++; // LZW minimum code size
                    if (!SubBlocks(d, ref p, out bool more)) return more ? CarveResult.NeedMore : CarveResult.Reject;
                    break;
                }
                case 0x21:
                    p += 2; // introducer + label
                    if (!SubBlocks(d, ref p, out bool m2)) return m2 ? CarveResult.NeedMore : CarveResult.Reject;
                    break;
                default:
                    return CarveResult.Reject;
            }
        }
        return CarveResult.Reject;
    }

    private static bool SubBlocks(ReadOnlySpan<byte> d, ref int p, out bool needMore)
    {
        needMore = false;
        for (int guard = 0; guard < 1_000_000; guard++)
        {
            if (p >= d.Length) { needMore = true; return false; }
            int size = d[p++];
            if (size == 0) return true;
            p += size;
        }
        return false;
    }

    // ------------------------------------------------------------------ BMP
    /// <summary>File size from the header; the DIB header size must be one Windows writes.</summary>
    public static CarveResult Bmp(ReadOnlySpan<byte> d)
    {
        if (d.Length < 30) return CarveResult.NeedMore;
        if (d[0] != 'B' || d[1] != 'M') return CarveResult.Reject;
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(d[2..]);
        uint dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(d[10..]);
        uint dib = BinaryPrimitives.ReadUInt32LittleEndian(d[14..]);
        if (dib is not (12 or 40 or 52 or 56 or 108 or 124)) return CarveResult.Reject;
        if (size < 14 + dib || dataOffset < 14 + dib || dataOffset > size) return CarveResult.Reject;
        string? info = null;
        if (dib >= 40)
        {
            int w = BinaryPrimitives.ReadInt32LittleEndian(d[18..]), h = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(d[22..]));
            if (w <= 0 || h <= 0 || w > 1 << 16 || h > 1 << 16) return CarveResult.Reject;
            info = $"{w}×{h}";
        }
        return size > d.Length ? CarveResult.NeedMore : CarveResult.Ok(size, info);
    }

    // ------------------------------------------------------------------ TIFF
    private static readonly int[] s_tiffTypeSizes = [0, 1, 1, 2, 4, 8, 1, 1, 2, 4, 8, 4, 8, 4];

    /// <summary>IFD walk: the file ends at the furthest byte any entry, strip or tile refers to.</summary>
    public static CarveResult Tiff(ReadOnlySpan<byte> d)
    {
        if (d.Length < 8) return CarveResult.NeedMore;
        bool le;
        if (d[0] == 'I' && d[1] == 'I' && d[2] == 0x2A && d[3] == 0) le = true;
        else if (d[0] == 'M' && d[1] == 'M' && d[2] == 0 && d[3] == 0x2A) le = false;
        else return CarveResult.Reject;
        long extent = 8;
        uint ifd = U32(d, 4, le);
        string? info = null;
        long width = 0, height = 0;
        for (int ifdCount = 0; ifdCount < 64 && ifd != 0; ifdCount++)
        {
            if (ifd < 8 || (ifd & 1) != 0) return CarveResult.Reject;
            if (ifd + 2 > d.Length) return CarveResult.NeedMore;
            int n = U16(d, (int)ifd, le);
            if (n == 0 || n > 4096) return CarveResult.Reject;
            long entriesEnd = ifd + 2 + 12L * n + 4;
            if (entriesEnd > d.Length) return CarveResult.NeedMore;
            extent = Math.Max(extent, entriesEnd);
            ReadOnlySpan<byte> offsetsEntry = default, countsEntry = default;
            for (int i = 0; i < n; i++)
            {
                var e = d.Slice((int)(ifd + 2 + 12 * i), 12);
                int tag = U16(e, 0, le), type = U16(e, 2, le);
                uint count = U32(e, 4, le);
                if (type < 1 || type >= s_tiffTypeSizes.Length) continue;
                long bytes = (long)count * s_tiffTypeSizes[type];
                if (bytes > 4)
                {
                    uint off = U32(e, 8, le);
                    extent = Math.Max(extent, off + bytes);
                }
                switch (tag)
                {
                    case 256: width = count == 1 ? Value(e, type, le) : width; break;
                    case 257: height = count == 1 ? Value(e, type, le) : height; break;
                    case 273 or 324: offsetsEntry = e; break;
                    case 279 or 325: countsEntry = e; break;
                }
            }
            if (!offsetsEntry.IsEmpty && !countsEntry.IsEmpty)
            {
                var r = StripExtent(d, offsetsEntry, countsEntry, le, out long stripExtent);
                if (r != CarveStatus.Ok) return r == CarveStatus.NeedMore ? CarveResult.NeedMore : CarveResult.Reject;
                extent = Math.Max(extent, stripExtent);
            }
            ifd = U32(d, (int)(entriesEnd - 4), le);
        }
        if (width > 0 && height > 0) info = $"{width}×{height}";
        if (extent > d.Length) return CarveResult.NeedMore;
        return CarveResult.Ok(extent, info);
    }

    private static CarveStatus StripExtent(ReadOnlySpan<byte> d, ReadOnlySpan<byte> offsets, ReadOnlySpan<byte> counts, bool le, out long extent)
    {
        extent = 0;
        uint n = U32(offsets, 4, le);
        if (n != U32(counts, 4, le) || n > 1 << 20) return CarveStatus.Reject;
        int ot = U16(offsets, 2, le), ct = U16(counts, 2, le);
        if (ot is not (3 or 4) || ct is not (3 or 4)) return CarveStatus.Reject;
        for (uint i = 0; i < n; i++)
        {
            if (!ArrayValue(d, offsets, ot, n, i, le, out long off) || !ArrayValue(d, counts, ct, n, i, le, out long cnt)) return CarveStatus.NeedMore;
            extent = Math.Max(extent, off + cnt);
        }
        return CarveStatus.Ok;
    }

    /// <summary>Element i of a SHORT/LONG array stored inline (when it fits in 4 bytes) or at the entry's offset.</summary>
    private static bool ArrayValue(ReadOnlySpan<byte> d, ReadOnlySpan<byte> entry, int type, uint n, uint i, bool le, out long value)
    {
        int size = type == 3 ? 2 : 4;
        long pos;
        if (n * size <= 4) pos = (int)(i * size);
        else pos = U32(entry, 8, le) + (long)i * size;
        var src = n * size <= 4 ? entry[8..] : d;
        value = 0;
        if (pos + size > src.Length) return false;
        value = size == 2 ? U16(src, (int)pos, le) : U32(src, (int)pos, le);
        return true;
    }

    private static long Value(ReadOnlySpan<byte> e, int type, bool le) => type == 3 ? U16(e, 8, le) : U32(e, 8, le);
    private static ushort U16(ReadOnlySpan<byte> d, int p, bool le) => le ? BinaryPrimitives.ReadUInt16LittleEndian(d[p..]) : BinaryPrimitives.ReadUInt16BigEndian(d[p..]);
    private static uint U32(ReadOnlySpan<byte> d, int p, bool le) => le ? BinaryPrimitives.ReadUInt32LittleEndian(d[p..]) : BinaryPrimitives.ReadUInt32BigEndian(d[p..]);

    // ------------------------------------------------------------------ WebP
    /// <summary>RIFF container with the WEBP form; size from the RIFF header, dimensions from the first chunk.</summary>
    public static CarveResult WebP(ReadOnlySpan<byte> d)
    {
        if (d.Length < 30) return CarveResult.NeedMore;
        if (!d[..4].SequenceEqual("RIFF"u8) || !d.Slice(8, 4).SequenceEqual("WEBP"u8)) return CarveResult.Reject;
        long size = BinaryPrimitives.ReadUInt32LittleEndian(d[4..]) + 8L;
        if (size < 30 || (size & 1) != 0 && size > 31) { if (size < 30) return CarveResult.Reject; }
        var chunk = d.Slice(12, 4);
        string? info = null;
        if (chunk.SequenceEqual("VP8 "u8) && d.Length >= 30 && d[23] == 0x9D && d[24] == 0x01 && d[25] == 0x2A)
            info = $"{BinaryPrimitives.ReadUInt16LittleEndian(d[26..]) & 0x3FFF}×{BinaryPrimitives.ReadUInt16LittleEndian(d[28..]) & 0x3FFF}";
        else if (chunk.SequenceEqual("VP8L"u8) && d[20] == 0x2F)
        {
            uint bits = BinaryPrimitives.ReadUInt32LittleEndian(d[21..]);
            info = $"{(bits & 0x3FFF) + 1}×{((bits >> 14) & 0x3FFF) + 1}";
        }
        else if (chunk.SequenceEqual("VP8X"u8))
            info = $"{(d[24] | d[25] << 8 | d[26] << 16) + 1}×{(d[27] | d[28] << 8 | d[29] << 16) + 1}";
        else if (!chunk.SequenceEqual("VP8 "u8) && !chunk.SequenceEqual("VP8L"u8)) return CarveResult.Reject;
        return size > d.Length ? CarveResult.NeedMore : CarveResult.Ok(size, info);
    }
}
