using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace FileHound.Core.Carving;

/// <summary>Documents, archives, databases and executables.</summary>
public static class DocumentValidators
{
    // ------------------------------------------------------------------ PDF
    /// <summary>Ends after the last <c>%%EOF</c> seen; asks for more until one appears (incremental updates append new ones).</summary>
    public static CarveResult Pdf(ReadOnlySpan<byte> d)
    {
        if (d.Length < 8) return CarveResult.NeedMore;
        if (!d[..5].SequenceEqual("%PDF-"u8)) return CarveResult.Reject;
        int last = d.LastIndexOf("%%EOF"u8);
        if (last < 0) return CarveResult.NeedMore;
        int end = last + 5;
        while (end < d.Length && (d[end] == '\r' || d[end] == '\n')) end++;
        // Linearized and incrementally updated PDFs carry several %%EOF marks; if what follows still looks like PDF
        // syntax, the file goes on and this is only a usable prefix.
        if (end < d.Length && LooksLikeMorePdf(d[end..])) return CarveResult.NeedMoreAfter(end);
        string? info = null;
        int pages = d.IndexOf("/Type /Pages"u8);
        if (pages < 0) pages = d.IndexOf("/Type/Pages"u8);
        if (pages >= 0)
        {
            var window = d.Slice(pages, Math.Min(200, d.Length - pages));
            int c = window.IndexOf("/Count "u8);
            if (c >= 0)
            {
                int q = c + 7, n = 0;
                while (q < window.Length && char.IsAsciiDigit((char)window[q]) && n < 1_000_000) n = n * 10 + (window[q++] - '0');
                if (n > 0) info = $"{n} page{(n == 1 ? "" : "s")}";
            }
        }
        return CarveResult.Ok(end, info);
    }

    private static bool LooksLikeMorePdf(ReadOnlySpan<byte> rest)
    {
        int i = 0;
        while (i < rest.Length && (rest[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) i++;
        if (i >= rest.Length) return false;
        var r = rest[i..];
        if (r.StartsWith("xref"u8) || r.StartsWith("trailer"u8) || r.StartsWith("startxref"u8) || r.StartsWith("%"u8)) return true;
        int j = 0;
        while (j < r.Length && char.IsAsciiDigit((char)r[j])) j++;
        if (j == 0 || j >= r.Length || r[j] != ' ') return false;
        int k = j + 1;
        while (k < r.Length && char.IsAsciiDigit((char)r[k])) k++;
        return k > j + 1 && r[k..].StartsWith(" obj"u8);
    }

    // ------------------------------------------------------------------ ZIP (+ OOXML / ODF / EPUB / JAR)
    /// <summary>Local headers, then the central directory to the end-of-central-directory record.</summary>
    public static CarveResult Zip(ReadOnlySpan<byte> d)
    {
        if (d.Length < 30) return CarveResult.NeedMore;
        if (d[0] != 'P' || d[1] != 'K' || d[2] != 3 || d[3] != 4) return CarveResult.Reject;
        int p = 0;
        string? firstName = null;
        bool sawCentral = false;
        for (int guard = 0; guard < 1_000_000; guard++)
        {
            if (p + 4 > d.Length) return CarveResult.NeedMore;
            uint sig = BinaryPrimitives.ReadUInt32LittleEndian(d[p..]);
            if (sig == 0x04034B50)   // local file header
            {
                if (p + 30 > d.Length) return CarveResult.NeedMore;
                ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(d[(p + 6)..]);
                uint compressed = BinaryPrimitives.ReadUInt32LittleEndian(d[(p + 18)..]);
                int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(d[(p + 26)..]), extraLen = BinaryPrimitives.ReadUInt16LittleEndian(d[(p + 28)..]);
                if (p + 30 + nameLen > d.Length) return CarveResult.NeedMore;
                firstName ??= Encoding.UTF8.GetString(d.Slice(p + 30, nameLen));
                long next = (long)p + 30 + nameLen + extraLen;
                if ((flags & 0x08) != 0 && compressed == 0)
                {
                    // Data descriptor: the size is not in the header; scan for the next signature.
                    if (next > d.Length) return CarveResult.NeedMore;
                    int rel = d[(int)next..].IndexOf("PK"u8);
                    while (rel >= 0)
                    {
                        int at = (int)next + rel;
                        if (at + 4 <= d.Length && ((d[at + 2] == 3 && d[at + 3] == 4) || (d[at + 2] == 1 && d[at + 3] == 2) || (d[at + 2] == 5 && d[at + 3] == 6))) break;
                        int more = d[(at + 2)..].IndexOf("PK"u8);
                        rel = more < 0 ? -1 : at + 2 + more - (int)next;
                    }
                    if (rel < 0) return CarveResult.NeedMore;
                    p = (int)next + rel;
                    continue;
                }
                next += compressed;
                if (next > d.Length) return CarveResult.NeedTotal(next);
                p = (int)next;
                if ((flags & 0x08) != 0 && p + 4 <= d.Length && BinaryPrimitives.ReadUInt32LittleEndian(d[p..]) == 0x08074B50) p += 16;
            }
            else if (sig == 0x02014B50)   // central directory entry
            {
                sawCentral = true;
                if (p + 46 > d.Length) return CarveResult.NeedMore;
                int n = BinaryPrimitives.ReadUInt16LittleEndian(d[(p + 28)..]), e = BinaryPrimitives.ReadUInt16LittleEndian(d[(p + 30)..]), c = BinaryPrimitives.ReadUInt16LittleEndian(d[(p + 32)..]);
                p += 46 + n + e + c;
            }
            else if (sig == 0x06054B50)   // end of central directory
            {
                if (!sawCentral) return CarveResult.Reject;
                if (p + 22 > d.Length) return CarveResult.NeedMore;
                int comment = BinaryPrimitives.ReadUInt16LittleEndian(d[(p + 20)..]);
                long end = (long)p + 22 + comment;
                return end > d.Length ? CarveResult.NeedTotal(end) : CarveResult.Ok(end, Subtype(firstName, d));
            }
            else if (sig == 0x06064B50 || sig == 0x07064B50)   // zip64 end records
            {
                if (sig == 0x06064B50) { if (p + 12 > d.Length) return CarveResult.NeedMore; p += 12 + (int)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(d[(p + 4)..]), int.MaxValue / 2); }
                else p += 20;
            }
            else return CarveResult.Reject;
        }
        return CarveResult.Reject;
    }

    /// <summary>The container's real kind, from the entry names (OOXML, ODF/EPUB mimetype, JAR), as an extension.</summary>
    internal static string? Subtype(string? firstName, ReadOnlySpan<byte> d)
    {
        if (firstName is null) return null;
        if (firstName == "[Content_Types].xml")
        {
            if (d.IndexOf("word/"u8) >= 0) return "docx";
            if (d.IndexOf("xl/"u8) >= 0) return "xlsx";
            if (d.IndexOf("ppt/"u8) >= 0) return "pptx";
            return "ooxml";
        }
        if (firstName == "mimetype")
        {
            int at = d.IndexOf("application/"u8);
            if (at >= 0)
            {
                var rest = d[(at + 12)..];
                if (rest.StartsWith("epub+zip"u8)) return "epub";
                if (rest.StartsWith("vnd.oasis.opendocument.text"u8)) return "odt";
                if (rest.StartsWith("vnd.oasis.opendocument.spreadsheet"u8)) return "ods";
                if (rest.StartsWith("vnd.oasis.opendocument.presentation"u8)) return "odp";
            }
            return null;
        }
        if (firstName.StartsWith("META-INF/", StringComparison.Ordinal)) return "jar";
        return null;
    }

    // ------------------------------------------------------------------ 7z
    public static CarveResult SevenZip(ReadOnlySpan<byte> d)
    {
        if (d.Length < 32) return CarveResult.NeedMore;
        if (d[0] != 0x37 || d[1] != 0x7A || d[2] != 0xBC || d[3] != 0xAF || d[4] != 0x27 || d[5] != 0x1C) return CarveResult.Reject;
        ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(d[12..]), size = BinaryPrimitives.ReadUInt64LittleEndian(d[20..]);
        if (offset > int.MaxValue || size > int.MaxValue) return CarveResult.Reject;
        long end = 32 + (long)offset + (long)size;
        return end > d.Length ? CarveResult.NeedTotal(end) : CarveResult.Ok(end, null);
    }

    // ------------------------------------------------------------------ RAR
    /// <summary>RAR 4 (block walk to the end-of-archive block) and RAR 5 (vint-sized headers to the end header).</summary>
    public static CarveResult Rar(ReadOnlySpan<byte> d)
    {
        if (d.Length < 8) return CarveResult.NeedMore;
        if (!d[..6].SequenceEqual((ReadOnlySpan<byte>)[0x52, 0x61, 0x72, 0x21, 0x1A, 0x07])) return CarveResult.Reject;
        if (d[6] == 0x00)
        {
            int p = 7;
            for (int guard = 0; guard < 1_000_000; guard++)
            {
                if (p + 7 > d.Length) return CarveResult.NeedMore;
                byte type = d[p + 2];
                ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(d[(p + 3)..]);
                int headSize = BinaryPrimitives.ReadUInt16LittleEndian(d[(p + 5)..]);
                if (headSize < 7) return CarveResult.Reject;
                long add = 0;
                if ((flags & 0x8000) != 0)
                {
                    if (p + 11 > d.Length) return CarveResult.NeedMore;
                    add = BinaryPrimitives.ReadUInt32LittleEndian(d[(p + 7)..]);
                }
                long next = p + headSize + add;
                if (type == 0x7B) return next > d.Length ? CarveResult.NeedTotal(next) : CarveResult.Ok(next, null);
                if (type < 0x72 || type > 0x7B) return CarveResult.Reject;
                if (next > d.Length) return CarveResult.NeedTotal(next);
                p = (int)next;
            }
            return CarveResult.Reject;
        }
        if (d[6] == 0x01 && d[7] == 0x00)
        {
            int p = 8;
            for (int guard = 0; guard < 1_000_000; guard++)
            {
                if (p + 7 > d.Length) return CarveResult.NeedMore;
                int q = p + 4;   // skip the header CRC32
                if (!Vint(d, ref q, out ulong headSize)) return CarveResult.NeedMore;
                int headerBody = q;   // HeaderSize counts from here
                if (!Vint(d, ref q, out ulong type) || !Vint(d, ref q, out ulong flags)) return CarveResult.NeedMore;
                if (headSize > 1 << 24) return CarveResult.Reject;
                long next = headerBody + (long)headSize;
                if ((flags & 0x02) != 0)
                {
                    if ((flags & 0x01) != 0 && !Vint(d, ref q, out _)) return CarveResult.NeedMore;
                    if (!Vint(d, ref q, out ulong dataSize)) return CarveResult.NeedMore;
                    if (dataSize > long.MaxValue / 2) return CarveResult.Reject;
                    next += (long)dataSize;
                }
                if (type == 5) return next > d.Length ? CarveResult.NeedTotal(next) : CarveResult.Ok(next, null);
                if (type < 1 || type > 5) return CarveResult.Reject;
                if (next > d.Length) return CarveResult.NeedTotal(next);
                p = (int)next;
            }
        }
        return CarveResult.Reject;
    }

    private static bool Vint(ReadOnlySpan<byte> d, ref int p, out ulong value)
    {
        value = 0;
        for (int i = 0; i < 10; i++)
        {
            if (p >= d.Length) return false;
            byte b = d[p++];
            value |= (ulong)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ GZIP
    /// <summary>No size field: inflate the member to find where it ends (bounded by the span), then the 8-byte trailer.</summary>
    public static CarveResult Gzip(ReadOnlySpan<byte> d)
    {
        if (d.Length < 18) return CarveResult.NeedMore;
        if (d[0] != 0x1F || d[1] != 0x8B || d[2] != 0x08) return CarveResult.Reject;
        byte flags = d[3];
        int p = 10;
        if ((flags & 0x04) != 0) { if (p + 2 > d.Length) return CarveResult.NeedMore; p += 2 + BinaryPrimitives.ReadUInt16LittleEndian(d[p..]); }
        if ((flags & 0x08) != 0) { int z = d[Math.Min(p, d.Length)..].IndexOf((byte)0); if (z < 0) return CarveResult.NeedMore; p += z + 1; }
        if ((flags & 0x10) != 0) { int z = d[Math.Min(p, d.Length)..].IndexOf((byte)0); if (z < 0) return CarveResult.NeedMore; p += z + 1; }
        if ((flags & 0x02) != 0) p += 2;
        if (p >= d.Length) return CarveResult.NeedMore;
        long deflateEnd = DeflateEnd(d[p..]);
        if (deflateEnd < 0) return d.Length - p < 8 ? CarveResult.NeedMore : CarveResult.Reject;
        long end = p + deflateEnd + 8;
        return end > d.Length ? CarveResult.NeedTotal(end) : CarveResult.Ok(end, null);
    }

    /// <summary>
    /// Length of the deflate stream at the start of <paramref name="data"/>: inflated over the pinned span (no copy)
    /// through a stream that hands out one byte at a time, so the inflater's position is exact at the final block.
    /// </summary>
    private static unsafe long DeflateEnd(ReadOnlySpan<byte> data)
    {
        fixed (byte* ptr = data)
        {
            var counting = new CountingStream(ptr, data.Length);
            try
            {
                using var inflate = new DeflateStream(counting, CompressionMode.Decompress);
                var sink = new byte[1 << 16];
                long total = 0;
                int n;
                while ((n = inflate.Read(sink, 0, sink.Length)) > 0) { total += n; if (total > 1L << 32) return -1; }
            }
            catch (InvalidDataException) { return -1; }
            return counting.ConsumedByDeflate;
        }
    }

    /// <summary>Feeds one byte at a time from pinned memory so the inflater's position is exact at the end of the last block.</summary>
    private sealed unsafe class CountingStream(byte* data, int length) : Stream
    {
        private int _pos;
        public long ConsumedByDeflate => _pos;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => length; public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= length || count == 0) return 0;
            buffer[offset] = data[_pos++];
            return 1;
        }
        public override int Read(Span<byte> buffer)
        {
            if (_pos >= length || buffer.IsEmpty) return 0;
            buffer[0] = data[_pos++];
            return 1;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ------------------------------------------------------------------ SQLite
    public static CarveResult Sqlite(ReadOnlySpan<byte> d)
    {
        if (d.Length < 100) return CarveResult.NeedMore;
        if (!d[..16].SequenceEqual("SQLite format 3\0"u8)) return CarveResult.Reject;
        int pageSize = BinaryPrimitives.ReadUInt16BigEndian(d[16..]);
        if (pageSize == 1) pageSize = 65536;
        if (pageSize < 512 || (pageSize & (pageSize - 1)) != 0) return CarveResult.Reject;
        uint pages = BinaryPrimitives.ReadUInt32BigEndian(d[28..]);
        if (pages == 0) return CarveResult.Reject;
        long size = (long)pages * pageSize;
        return size > d.Length ? CarveResult.NeedTotal(size) : CarveResult.Ok(size, $"{pages:N0} page{(pages == 1 ? "" : "s")} of {pageSize}");
    }

    // ------------------------------------------------------------------ PE (exe / dll)
    public static CarveResult Pe(ReadOnlySpan<byte> d)
    {
        if (d.Length < 64) return CarveResult.NeedMore;
        if (d[0] != 'M' || d[1] != 'Z') return CarveResult.Reject;
        uint lfanew = BinaryPrimitives.ReadUInt32LittleEndian(d[60..]);
        if (lfanew < 64 || lfanew > 4096) return CarveResult.Reject;
        if (lfanew + 24 > d.Length) return CarveResult.NeedMore;
        int pe = (int)lfanew;
        if (!d.Slice(pe, 4).SequenceEqual("PE\0\0"u8)) return CarveResult.Reject;
        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(d[(pe + 4)..]);
        int sections = BinaryPrimitives.ReadUInt16LittleEndian(d[(pe + 6)..]);
        int optSize = BinaryPrimitives.ReadUInt16LittleEndian(d[(pe + 20)..]);
        ushort characteristics = BinaryPrimitives.ReadUInt16LittleEndian(d[(pe + 22)..]);
        if (sections == 0 || sections > 96) return CarveResult.Reject;
        int opt = pe + 24;
        int table = opt + optSize;
        if (table + sections * 40 > d.Length) return CarveResult.NeedMore;
        long end = table + sections * 40L;
        if (optSize >= 2)
        {
            ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(d[opt..]);
            int dirOffset = magic == 0x20B ? 112 : magic == 0x10B ? 96 : -1;
            if (dirOffset > 0 && optSize >= dirOffset + 5 * 8)
            {
                // Certificate table (index 4) is a file offset + size, appended after the sections.
                uint certOff = BinaryPrimitives.ReadUInt32LittleEndian(d[(opt + dirOffset + 32)..]), certSize = BinaryPrimitives.ReadUInt32LittleEndian(d[(opt + dirOffset + 36)..]);
                if (certOff > 0 && certSize > 0 && certOff < 1L << 31) end = Math.Max(end, (long)certOff + certSize);
            }
        }
        for (int i = 0; i < sections; i++)
        {
            int s = table + i * 40;
            uint raw = BinaryPrimitives.ReadUInt32LittleEndian(d[(s + 20)..]), rawSize = BinaryPrimitives.ReadUInt32LittleEndian(d[(s + 16)..]);
            if (raw > 1L << 31 || rawSize > 1L << 31) return CarveResult.Reject;
            end = Math.Max(end, (long)raw + rawSize);
        }
        string arch = machine switch { 0x8664 => "x64", 0x14C => "x86", 0xAA64 => "ARM64", _ => $"machine 0x{machine:X}" };
        string kind = (characteristics & 0x2000) != 0 ? "DLL" : "EXE";
        return end > d.Length ? CarveResult.NeedTotal(end) : CarveResult.Ok(end, $"{arch} {kind}");
    }

    // ------------------------------------------------------------------ OLE2 (doc / xls / ppt / msg)
    private static ReadOnlySpan<byte> OleMagic => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>Counts used sectors through the FAT (header DIFAT + DIFAT sectors); the file is the header plus those sectors.</summary>
    public static CarveResult Ole2(ReadOnlySpan<byte> d)
    {
        if (d.Length < 512) return CarveResult.NeedMore;
        if (!d[..8].SequenceEqual(OleMagic)) return CarveResult.Reject;
        int sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(d[30..]);
        if (sectorShift is not (9 or 12)) return CarveResult.Reject;
        int sector = 1 << sectorShift;
        uint fatSectors = BinaryPrimitives.ReadUInt32LittleEndian(d[44..]);
        uint difatStart = BinaryPrimitives.ReadUInt32LittleEndian(d[68..]), difatCount = BinaryPrimitives.ReadUInt32LittleEndian(d[72..]);
        if (fatSectors == 0 || fatSectors > 1 << 20 || difatCount > 65536) return CarveResult.Reject;
        var fatList = new List<uint>();
        var seen = new HashSet<uint>();
        bool AddFat(uint s)
        {
            if (s >= 0xFFFFFFFA || !seen.Add(s) || fatList.Count >= fatSectors) return false;
            fatList.Add(s);
            return true;
        }
        for (int i = 0; i < 109 && fatList.Count < fatSectors; i++)
        {
            uint s = BinaryPrimitives.ReadUInt32LittleEndian(d[(76 + i * 4)..]);
            if (s == 0xFFFFFFFF) break;
            if (!AddFat(s)) return CarveResult.Reject;   // duplicate or special sector number: not a real FAT chain
        }
        uint difat = difatStart;
        var difatSeen = new HashSet<uint>();
        for (uint k = 0; k < difatCount && difat != 0xFFFFFFFE && difat != 0xFFFFFFFF && fatList.Count < fatSectors; k++)
        {
            if (!difatSeen.Add(difat)) return CarveResult.Reject;   // the DIFAT chain loops
            long at = 512 + (long)difat * sector;
            if (at + sector > d.Length) return CarveResult.NeedMore;
            int perSector = sector / 4 - 1;
            int before = fatList.Count;
            for (int i = 0; i < perSector && fatList.Count < fatSectors; i++)
            {
                uint s = BinaryPrimitives.ReadUInt32LittleEndian(d[(int)(at + i * 4)..]);
                if (s == 0xFFFFFFFF) break;
                if (!AddFat(s)) return CarveResult.Reject;
            }
            if (fatList.Count == before) return CarveResult.Reject;   // a DIFAT sector that lists nothing is corrupt
            difat = BinaryPrimitives.ReadUInt32LittleEndian(d[(int)(at + perSector * 4)..]);
        }
        // The file extends to the highest sector the FAT describes as in use (free holes in the middle still count).
        long highest = -1;
        long index = 0;
        foreach (uint fs in fatList)
        {
            long at = 512 + (long)fs * sector;
            if (at + sector > d.Length) return CarveResult.NeedMore;
            for (int i = 0; i < sector / 4; i++, index++)
                if (BinaryPrimitives.ReadUInt32LittleEndian(d[(int)(at + i * 4)..]) != 0xFFFFFFFF) highest = index;
        }
        if (highest < 0) return CarveResult.Reject;
        long size = 512 + (highest + 1) * sector;
        if (size > d.Length) return CarveResult.NeedTotal(size);
        return CarveResult.Ok(size, OleSubtype(d[..(int)size]));
    }

    /// <summary>Stream names appear as UTF-16 in directory entries; the well-known ones name the application.</summary>
    internal static string? OleSubtype(ReadOnlySpan<byte> d)
    {
        if (d.IndexOf("W\0o\0r\0d\0D\0o\0c\0u\0m\0e\0n\0t\0"u8) >= 0) return "doc";
        if (d.IndexOf("W\0o\0r\0k\0b\0o\0o\0k\0"u8) >= 0 || d.IndexOf("B\0o\0o\0k\0\0\0"u8) >= 0) return "xls";
        if (d.IndexOf("P\0o\0w\0e\0r\0P\0o\0i\0n\0t\0 \0D\0o\0c\0u\0m\0e\0n\0t\0"u8) >= 0) return "ppt";
        if (d.IndexOf("_\0_\0p\0r\0o\0p\0e\0r\0t\0i\0e\0s\0_\0v\0e\0r\0s\0i\0o\0n\0"u8) >= 0) return "msg";
        return null;
    }

    // ------------------------------------------------------------------ RTF
    public static CarveResult Rtf(ReadOnlySpan<byte> d)
    {
        if (d.Length < 5) return CarveResult.NeedMore;
        if (!d[..5].SequenceEqual("{\\rtf"u8)) return CarveResult.Reject;
        int depth = 0;
        for (int p = 0; p < d.Length; p++)
        {
            byte c = d[p];
            if (c == '\\') { p++; continue; }   // escaped brace or control word start; the next byte is never structural
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return CarveResult.Ok(p + 1, null);
                if (depth < 0) return CarveResult.Reject;
            }
            else if (c == 0 && depth == 1 && p > 64) return CarveResult.Reject;   // RTF is text; a NUL this early is not it
        }
        return CarveResult.NeedMore;
    }

    // ------------------------------------------------------------------ PST
    public static CarveResult Pst(ReadOnlySpan<byte> d)
    {
        if (d.Length < 200) return CarveResult.NeedMore;
        if (!d[..4].SequenceEqual("!BDN"u8)) return CarveResult.Reject;
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(d[10..]);
        long size;
        string kind;
        if (version is 14 or 15) { size = BinaryPrimitives.ReadUInt32LittleEndian(d[0xA8..]); kind = "ANSI"; }
        else if (version >= 23) { size = (long)BinaryPrimitives.ReadUInt64LittleEndian(d[0xB8..]); kind = "Unicode"; }
        else return CarveResult.Reject;
        if (size < 512) return CarveResult.Reject;
        return size > d.Length ? CarveResult.NeedTotal(size) : CarveResult.Ok(size, $"{kind} PST");
    }

    // ------------------------------------------------------------------ LNK
    private static ReadOnlySpan<byte> LnkClsid => [0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46];

    public static CarveResult Lnk(ReadOnlySpan<byte> d)
    {
        if (d.Length < 76) return CarveResult.NeedMore;
        if (BinaryPrimitives.ReadUInt32LittleEndian(d) != 0x4C || !d.Slice(4, 16).SequenceEqual(LnkClsid)) return CarveResult.Reject;
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(d[20..]);
        bool unicode = (flags & 0x80) != 0;
        int p = 76;
        if ((flags & 0x01) != 0) { if (p + 2 > d.Length) return CarveResult.NeedMore; p += 2 + BinaryPrimitives.ReadUInt16LittleEndian(d[p..]); }
        if ((flags & 0x02) != 0) { if (p + 4 > d.Length) return CarveResult.NeedMore; uint n = BinaryPrimitives.ReadUInt32LittleEndian(d[p..]); if (n < 28 || n > 1 << 20) return CarveResult.Reject; p += (int)n; }
        for (int bit = 0x04; bit <= 0x40; bit <<= 1)
        {
            if ((flags & bit) == 0) continue;
            if (p + 2 > d.Length) return CarveResult.NeedMore;
            p += 2 + BinaryPrimitives.ReadUInt16LittleEndian(d[p..]) * (unicode ? 2 : 1);
        }
        for (int guard = 0; guard < 64; guard++)
        {
            if (p + 4 > d.Length) return CarveResult.NeedMore;
            uint block = BinaryPrimitives.ReadUInt32LittleEndian(d[p..]);
            if (block < 4) return CarveResult.Ok(p + 4, null);
            if (block > 1 << 20) return CarveResult.Reject;
            p += (int)block;
        }
        return CarveResult.Reject;
    }
}
