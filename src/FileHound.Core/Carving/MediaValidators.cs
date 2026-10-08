using System.Buffers.Binary;

namespace FileHound.Core.Carving;

/// <summary>Audio/video containers. Sizes come from the container structure; streaming formats are walked frame by frame.</summary>
public static class MediaValidators
{
    // ------------------------------------------------------------------ RIFF (WAV, AVI)
    public static CarveResult Riff(ReadOnlySpan<byte> d)
    {
        if (d.Length < 12) return CarveResult.NeedMore;
        if (!d[..4].SequenceEqual("RIFF"u8)) return CarveResult.Reject;
        var form = d.Slice(8, 4);
        long size = BinaryPrimitives.ReadUInt32LittleEndian(d[4..]) + 8L;
        if (size < 12) return CarveResult.Reject;
        string? info = null;
        if (form.SequenceEqual("WAVE"u8))
        {
            int p = 12;
            while (p + 8 <= d.Length && p + 8 <= size)
            {
                var id = d.Slice(p, 4);
                uint len = BinaryPrimitives.ReadUInt32LittleEndian(d[(p + 4)..]);
                if (id.SequenceEqual("fmt "u8) && len >= 16 && p + 8 + 16 <= d.Length)
                {
                    int channels = BinaryPrimitives.ReadUInt16LittleEndian(d[(p + 10)..]);
                    uint rate = BinaryPrimitives.ReadUInt32LittleEndian(d[(p + 12)..]);
                    int bits = BinaryPrimitives.ReadUInt16LittleEndian(d[(p + 22)..]);
                    info = $"{rate / 1000.0:0.#} kHz · {bits}-bit · {(channels == 1 ? "mono" : channels == 2 ? "stereo" : channels + " ch")}";
                    break;
                }
                if (len > int.MaxValue - 8) return CarveResult.Reject;
                p += 8 + (int)len + (int)(len & 1);
            }
        }
        else if (form.SequenceEqual("AVI "u8))
        {
            // LIST hdrl → avih (56 bytes): microseconds per frame @0, total frames @16, width @32, height @36
            if (d.Length >= 12 + 12 + 8 + 40 && d.Slice(12, 4).SequenceEqual("LIST"u8) && d.Slice(20, 4).SequenceEqual("hdrl"u8) && d.Slice(24, 4).SequenceEqual("avih"u8))
            {
                uint usPerFrame = BinaryPrimitives.ReadUInt32LittleEndian(d[32..]);
                uint frames = BinaryPrimitives.ReadUInt32LittleEndian(d[48..]);
                uint w = BinaryPrimitives.ReadUInt32LittleEndian(d[64..]), h = BinaryPrimitives.ReadUInt32LittleEndian(d[68..]);
                info = $"{w}×{h}" + (usPerFrame > 0 ? $" · {Duration(frames * (usPerFrame / 1_000_000.0))}" : "");
            }
        }
        else return CarveResult.Reject;
        return size > d.Length ? CarveResult.NeedMore : CarveResult.Ok(size, info);
    }

    // ------------------------------------------------------------------ ISO-BMFF (MP4, MOV, M4A, HEIC)
    private static readonly string[] s_topLevel = ["ftyp", "moov", "mdat", "free", "skip", "wide", "meta", "uuid", "moof", "mfra", "sidx", "styp", "pdin", "junk", "pnot", "PICT"];

    /// <summary>Box walk from <c>ftyp</c>; the file ends after the last top-level box.</summary>
    public static CarveResult IsoBmff(ReadOnlySpan<byte> d)
    {
        if (d.Length < 12) return CarveResult.NeedMore;
        if (!d.Slice(4, 4).SequenceEqual("ftyp"u8)) return CarveResult.Reject;
        string? info = null;
        long p = 0;
        bool sawMoov = false, sawMdat = false;
        for (int guard = 0; guard < 100_000; guard++)
        {
            if (p + 8 > d.Length) return p > 8 && (sawMoov || sawMdat) ? CarveResult.Ok(p, info) : CarveResult.NeedMore;
            long size = BinaryPrimitives.ReadUInt32BigEndian(d[(int)p..]);
            var type = d.Slice((int)p + 4, 4);
            string typeName = System.Text.Encoding.ASCII.GetString(type);
            if (size == 1)
            {
                if (p + 16 > d.Length) return CarveResult.NeedMore;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(d[(int)(p + 8)..]);
                if (size < 16) return CarveResult.Reject;
            }
            else if (size == 0)
            {
                // "to end of file" is only legal for the last box; without an outer bound we cannot size it.
                return CarveResult.Reject;
            }
            else if (size < 8) return CarveResult.Reject;
            if (!s_topLevel.Contains(typeName)) return p > 0 && (sawMoov || sawMdat) ? CarveResult.Ok(p, info) : CarveResult.Reject;
            if (typeName == "moov")
            {
                sawMoov = true;
                if (p + size <= d.Length) info = MoovInfo(d.Slice((int)p + 8, (int)Math.Min(size - 8, int.MaxValue)));
            }
            if (typeName == "mdat") sawMdat = true;
            p += size;
            if (p > d.Length)
            {
                return CarveResult.NeedMore;
            }
            if (p == d.Length && (sawMoov || sawMdat)) return CarveResult.Ok(p, info);
        }
        return CarveResult.Reject;
    }

    private static string? MoovInfo(ReadOnlySpan<byte> moov)
    {
        string? duration = null, dims = null;
        int p = 0;
        while (p + 8 <= moov.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(moov[p..]);
            if (size < 8 || p + size > moov.Length) break;
            var type = moov.Slice(p + 4, 4);
            if (type.SequenceEqual("mvhd"u8) && size >= 32)
            {
                byte version = moov[p + 8];
                if (version == 0 && size >= 8 + 24)
                {
                    uint scale = BinaryPrimitives.ReadUInt32BigEndian(moov[(p + 20)..]);
                    uint dur = BinaryPrimitives.ReadUInt32BigEndian(moov[(p + 24)..]);
                    if (scale > 0) duration = Duration(dur / (double)scale);
                }
                else if (version == 1 && size >= 8 + 36)
                {
                    uint scale = BinaryPrimitives.ReadUInt32BigEndian(moov[(p + 28)..]);
                    ulong dur = BinaryPrimitives.ReadUInt64BigEndian(moov[(p + 32)..]);
                    if (scale > 0) duration = Duration(dur / (double)scale);
                }
            }
            else if (type.SequenceEqual("trak"u8) && dims is null)
            {
                var trak = moov.Slice(p + 8, (int)size - 8);
                int q = 0;
                while (q + 8 <= trak.Length)
                {
                    long s2 = BinaryPrimitives.ReadUInt32BigEndian(trak[q..]);
                    if (s2 < 8 || q + s2 > trak.Length) break;
                    if (trak.Slice(q + 4, 4).SequenceEqual("tkhd"u8) && s2 >= 92)
                    {
                        int end = q + (int)s2;
                        uint w = BinaryPrimitives.ReadUInt32BigEndian(trak[(end - 8)..]) >> 16, h = BinaryPrimitives.ReadUInt32BigEndian(trak[(end - 4)..]) >> 16;
                        if (w > 0 && h > 0) dims = $"{w}×{h}";
                        break;
                    }
                    q += (int)s2;
                }
            }
            p += (int)size;
        }
        return dims is null && duration is null ? null : dims is null ? duration : duration is null ? dims : $"{dims} · {duration}";
    }

    // ------------------------------------------------------------------ Matroska / WebM
    /// <summary>EBML header then the Segment; the file ends where the Segment ends (unknown-size segments are walked child by child).</summary>
    public static CarveResult Mkv(ReadOnlySpan<byte> d)
    {
        if (d.Length < 8) return CarveResult.NeedMore;
        if (d[0] != 0x1A || d[1] != 0x45 || d[2] != 0xDF || d[3] != 0xA3) return CarveResult.Reject;
        if (!ReadVint(d, 4, out long headerSize, out int hl, out _)) return d.Length < 12 ? CarveResult.NeedMore : CarveResult.Reject;
        long p = 4 + hl + headerSize;
        if (p + 4 > d.Length) return CarveResult.NeedMore;
        if (d[(int)p] != 0x18 || d[(int)p + 1] != 0x53 || d[(int)p + 2] != 0x80 || d[(int)p + 3] != 0x67) return CarveResult.Reject;   // Segment
        if (!ReadVint(d, (int)p + 4, out long segSize, out int sl, out bool unknown)) return CarveResult.NeedMore;
        long segStart = p + 4 + sl;
        string? info = null;
        if (!unknown)
        {
            long end = segStart + segSize;
            if (end > d.Length) return CarveResult.NeedMore;
            info = SegmentInfo(d.Slice((int)segStart, (int)Math.Min(segSize, 1 << 20)));
            return CarveResult.Ok(end, info);
        }
        // Unknown size: walk top-level children while they are known Segment children.
        long q = segStart;
        for (int guard = 0; guard < 1_000_000; guard++)
        {
            if (q + 2 > d.Length) return q > segStart ? CarveResult.Ok(q, info) : CarveResult.NeedMore;
            uint id = ReadId(d, (int)q, out int idLen);
            if (idLen == 0 || !IsSegmentChild(id)) return q > segStart ? CarveResult.Ok(q, info) : CarveResult.Reject;
            if (!ReadVint(d, (int)q + idLen, out long size, out int sl2, out bool unk2)) return CarveResult.NeedMore;
            if (unk2) return CarveResult.Reject;
            if (id == 0x1549A966 && q + idLen + sl2 + size <= d.Length) info ??= SegmentInfo(d.Slice((int)q, (int)(idLen + sl2 + size)));
            q += idLen + sl2 + size;
            if (q > d.Length) return CarveResult.NeedMore;
        }
        return CarveResult.Reject;
    }

    private static bool IsSegmentChild(uint id) => id is 0x114D9B74 or 0x1549A966 or 0x1F43B675 or 0x1654AE6B or 0x1C53BB6B or 0x1941A469 or 0x1043A770 or 0x1254C367 or 0xEC;

    private static string? SegmentInfo(ReadOnlySpan<byte> seg)
    {
        // Find the Info element (0x1549A966) and inside it TimecodeScale (0x2AD7B1) and Duration (0x4489).
        int p = 0;
        while (p + 4 <= seg.Length)
        {
            uint id = ReadId(seg, p, out int idLen);
            if (idLen == 0 || !ReadVint(seg, p + idLen, out long size, out int sl, out bool unk) || unk) return null;
            int start = p + idLen + sl;
            if (start + size > seg.Length) return null;
            if (id == 0x1549A966)
            {
                double scale = 1_000_000, duration = 0;
                int q = start;
                while (q < start + size)
                {
                    uint cid = ReadId(seg, q, out int cl);
                    if (cl == 0 || !ReadVint(seg, q + cl, out long cs, out int csl, out _)) break;
                    int vs = q + cl + csl;
                    if (vs + cs > seg.Length) break;
                    if (cid == 0x2AD7B1) scale = ReadUInt(seg.Slice(vs, (int)cs));
                    else if (cid == 0x4489) duration = cs == 4 ? BinaryPrimitives.ReadSingleBigEndian(seg[vs..]) : cs == 8 ? BinaryPrimitives.ReadDoubleBigEndian(seg[vs..]) : 0;
                    q = vs + (int)cs;
                }
                return duration > 0 ? Duration(duration * scale / 1e9) : null;
            }
            p = start + (int)size;
        }
        return null;
    }

    private static ulong ReadUInt(ReadOnlySpan<byte> b) { ulong v = 0; foreach (var x in b) v = (v << 8) | x; return v; }

    private static uint ReadId(ReadOnlySpan<byte> d, int p, out int length)
    {
        length = 0;
        if (p >= d.Length) return 0;
        byte b = d[p];
        int len = b >= 0x80 ? 1 : b >= 0x40 ? 2 : b >= 0x20 ? 3 : b >= 0x10 ? 4 : 0;
        if (len == 0 || p + len > d.Length) return 0;
        uint id = 0;
        for (int i = 0; i < len; i++) id = (id << 8) | d[p + i];
        length = len;
        return id;
    }

    private static bool ReadVint(ReadOnlySpan<byte> d, int p, out long value, out int length, out bool unknown)
    {
        value = 0; length = 0; unknown = false;
        if (p >= d.Length) return false;
        byte b = d[p];
        int len = 1;
        while (len <= 8 && (b & (0x80 >> (len - 1))) == 0) len++;
        if (len > 8 || p + len > d.Length) return false;
        long v = b & (0xFF >> len);
        bool allOnes = v == (0xFF >> len);
        for (int i = 1; i < len; i++) { v = (v << 8) | d[p + i]; allOnes &= d[p + i] == 0xFF; }
        value = v; length = len; unknown = allOnes;
        return true;
    }

    // ------------------------------------------------------------------ OGG
    /// <summary>Page walk until a page carrying the end-of-stream flag.</summary>
    public static CarveResult Ogg(ReadOnlySpan<byte> d)
    {
        if (d.Length < 27) return CarveResult.NeedMore;
        if (!d[..4].SequenceEqual("OggS"u8)) return CarveResult.Reject;
        int p = 0;
        for (int guard = 0; guard < 10_000_000; guard++)
        {
            if (p + 27 > d.Length) return CarveResult.NeedMore;
            if (!d.Slice(p, 4).SequenceEqual("OggS"u8) || d[p + 4] != 0) return p > 0 ? CarveResult.Reject : CarveResult.Reject;
            byte flags = d[p + 5];
            int segments = d[p + 26];
            if (p + 27 + segments > d.Length) return CarveResult.NeedMore;
            int body = 0;
            for (int i = 0; i < segments; i++) body += d[p + 27 + i];
            int next = p + 27 + segments + body;
            if (next > d.Length) return CarveResult.NeedMore;
            p = next;
            if ((flags & 0x04) != 0) return CarveResult.Ok(p, null);
        }
        return CarveResult.Reject;
    }

    // ------------------------------------------------------------------ MP3
    private static readonly int[,] s_bitrates =
    {
        // V1 L1, V1 L2, V1 L3, V2 L1, V2 L2/L3
        { 0, 0, 0, 0, 0 }, { 32, 32, 32, 32, 8 }, { 64, 48, 40, 48, 16 }, { 96, 56, 48, 56, 24 }, { 128, 64, 56, 64, 32 }, { 160, 80, 64, 80, 40 },
        { 192, 96, 80, 96, 48 }, { 224, 112, 96, 112, 56 }, { 256, 128, 112, 128, 64 }, { 288, 160, 128, 144, 80 }, { 320, 192, 160, 160, 96 },
        { 352, 224, 192, 176, 112 }, { 384, 256, 224, 192, 128 }, { 416, 320, 256, 224, 144 }, { 448, 384, 320, 256, 160 }, { 0, 0, 0, 0, 0 },
    };
    private static readonly int[,] s_sampleRates = { { 44100, 48000, 32000 }, { 22050, 24000, 16000 }, { 11025, 12000, 8000 } };

    /// <summary>Optional ID3v2 tag, then at least four consecutive valid frames; ends at the last valid frame (plus an ID3v1 tag if present).</summary>
    public static CarveResult Mp3(ReadOnlySpan<byte> d)
    {
        if (d.Length < 10) return CarveResult.NeedMore;
        int p = 0;
        if (d[..3].SequenceEqual("ID3"u8))
        {
            if ((d[6] | d[7] | d[8] | d[9]) >= 0x80) return CarveResult.Reject;
            int tag = 10 + ((d[6] << 21) | (d[7] << 14) | (d[8] << 7) | d[9]) + ((d[5] & 0x10) != 0 ? 10 : 0);
            p = tag;
        }
        else if (d[0] != 0xFF || (d[1] & 0xE0) != 0xE0) return CarveResult.Reject;
        int frames = 0;
        string? info = null;
        for (int guard = 0; guard < 10_000_000; guard++)
        {
            if (p + 4 > d.Length) return frames >= 4 ? CarveResult.Ok(p, info) : CarveResult.NeedMore;
            int len = FrameLength(d[p..], out string? frameInfo);
            if (len <= 0)
            {
                if (frames < 4) return frames == 0 && p == 0 ? CarveResult.Reject : CarveResult.Reject;
                if (p + 128 <= d.Length && d.Slice(p, 3).SequenceEqual("TAG"u8)) p += 128;
                return CarveResult.Ok(p, info);
            }
            info ??= frameInfo;
            if (p + len > d.Length) return frames >= 4 ? CarveResult.Ok(p, info) : CarveResult.NeedMore;
            p += len;
            frames++;
        }
        return CarveResult.Ok(p, info);
    }

    private static int FrameLength(ReadOnlySpan<byte> h, out string? info)
    {
        info = null;
        if (h.Length < 4 || h[0] != 0xFF || (h[1] & 0xE0) != 0xE0) return 0;
        int versionBits = (h[1] >> 3) & 3, layerBits = (h[1] >> 1) & 3;
        if (versionBits == 1 || layerBits == 0) return 0;
        int bitrateIndex = h[2] >> 4, rateIndex = (h[2] >> 2) & 3, padding = (h[2] >> 1) & 1;
        if (bitrateIndex is 0 or 15 || rateIndex == 3) return 0;
        int layer = 4 - layerBits;                       // 1..3
        bool v1 = versionBits == 3;
        int column = v1 ? layer - 1 : layer == 1 ? 3 : 4;
        int bitrate = s_bitrates[bitrateIndex, column] * 1000;
        int rateRow = v1 ? 0 : versionBits == 2 ? 1 : 2;
        int sampleRate = s_sampleRates[rateRow, rateIndex];
        if (bitrate == 0) return 0;
        int length = layer == 1 ? (12 * bitrate / sampleRate + padding) * 4 : (v1 || layer == 2 ? 144 : 72) * bitrate / sampleRate + padding;
        info = $"{bitrate / 1000} kbps · {sampleRate / 1000.0:0.#} kHz";
        return length;
    }

    // ------------------------------------------------------------------ FLAC
    /// <summary>Metadata blocks (STREAMINFO first), then frames whose sync code and header CRC-8 check out.</summary>
    public static CarveResult Flac(ReadOnlySpan<byte> d)
    {
        if (d.Length < 42) return CarveResult.NeedMore;
        if (!d[..4].SequenceEqual("fLaC"u8)) return CarveResult.Reject;
        int p = 4;
        string? info = null;
        for (int guard = 0; guard < 1024; guard++)
        {
            if (p + 4 > d.Length) return CarveResult.NeedMore;
            byte header = d[p];
            int type = header & 0x7F;
            int len = (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3];
            if (guard == 0 && (type != 0 || len != 34)) return CarveResult.Reject;
            if (type == 0 && p + 4 + 34 <= d.Length)
            {
                var si = d.Slice(p + 4, 34);
                uint rate = (uint)((si[10] << 12) | (si[11] << 4) | (si[12] >> 4));
                int channels = ((si[12] >> 1) & 7) + 1;
                ulong samples = ((ulong)(si[13] & 0x0F) << 32) | BinaryPrimitives.ReadUInt32BigEndian(si[14..]);
                if (rate > 0) info = $"{rate / 1000.0:0.#} kHz · {(channels == 1 ? "mono" : channels == 2 ? "stereo" : channels + " ch")}" + (samples > 0 ? $" · {Duration(samples / (double)rate)}" : "");
            }
            p += 4 + len;
            if ((header & 0x80) != 0) break;
        }
        int frames = 0;
        for (int guard = 0; guard < 10_000_000; guard++)
        {
            if (p + 16 > d.Length) return frames > 0 ? CarveResult.Ok(p, info) : CarveResult.NeedMore;
            int next = NextFlacFrame(d, p);
            if (next < 0) return frames > 0 ? CarveResult.Ok(p, info) : CarveResult.Reject;
            if (next == p) break;
            p = next;
            frames++;
        }
        return frames > 0 ? CarveResult.Ok(p, info) : CarveResult.Reject;
    }

    /// <summary>Validates the frame header at p (sync + CRC-8) and returns the start of the next frame (the next valid sync), or -1.</summary>
    private static int NextFlacFrame(ReadOnlySpan<byte> d, int p)
    {
        if (!FlacHeaderOk(d, p)) return -1;
        // Frames carry no length: advance to the next position whose header validates, scanning at most 1 MB.
        int limit = Math.Min(d.Length - 2, p + (1 << 20));
        for (int q = p + 16; q < limit; q++)
        {
            if (d[q] == 0xFF && (d[q + 1] & 0xFE) == 0xF8 && FlacHeaderOk(d, q)) return q;
        }
        return limit >= d.Length - 2 ? d.Length : -1;   // ran off the span: the last frame ends at the span end
    }

    private static bool FlacHeaderOk(ReadOnlySpan<byte> d, int p)
    {
        if (p + 6 > d.Length || d[p] != 0xFF || (d[p + 1] & 0xFE) != 0xF8) return false;
        // Header length: 4 fixed bytes + UTF-8 coded number (1..7 bytes) + optional block size (1-2) + optional sample rate (1-2) + CRC-8.
        int q = p + 4;
        byte first = d[q];
        int utf = first < 0x80 ? 1 : first >= 0xFE ? 7 : first >= 0xFC ? 6 : first >= 0xF8 ? 5 : first >= 0xF0 ? 4 : first >= 0xE0 ? 3 : first >= 0xC0 ? 2 : 0;
        if (utf == 0) return false;
        q += utf;
        int bs = d[p + 2] >> 4, sr = d[p + 2] & 0x0F;
        if (bs == 0 || sr == 15) return false;
        if (bs == 6) q += 1; else if (bs == 7) q += 2;
        if (sr == 12) q += 1; else if (sr is 13 or 14) q += 2;
        if (q + 1 > d.Length) return false;
        byte crc = 0;
        for (int i = p; i < q; i++)
        {
            crc ^= d[i];
            for (int k = 0; k < 8; k++) crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
        }
        return crc == d[q];
    }

    internal static string Duration(double seconds)
    {
        // Header fields are untrusted: anything past a year is noise, not a duration.
        if (seconds < 0 || double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds > 366 * 86400) return "";
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}
