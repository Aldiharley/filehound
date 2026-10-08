namespace FileHound.Indexing.Recovery;

/// <summary>"Does the first cluster look like a file of this type?" for the undelete grade (FR-14). Magic numbers only.</summary>
public static class ContentCheck
{
    private static readonly Dictionary<string, byte[][]> s_magics = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = [[0xFF, 0xD8, 0xFF]], [".jpeg"] = [[0xFF, 0xD8, 0xFF]],
        [".png"] = [[0x89, 0x50, 0x4E, 0x47]],
        [".gif"] = ["GIF8"u8.ToArray()],
        [".bmp"] = ["BM"u8.ToArray()],
        [".webp"] = ["RIFF"u8.ToArray()], [".wav"] = ["RIFF"u8.ToArray()], [".avi"] = ["RIFF"u8.ToArray()],
        [".tif"] = [[0x49, 0x49, 0x2A, 0x00], [0x4D, 0x4D, 0x00, 0x2A]], [".tiff"] = [[0x49, 0x49, 0x2A, 0x00], [0x4D, 0x4D, 0x00, 0x2A]],
        [".pdf"] = ["%PDF"u8.ToArray()],
        [".zip"] = ["PK\x03\x04"u8.ToArray()], [".docx"] = ["PK\x03\x04"u8.ToArray()], [".xlsx"] = ["PK\x03\x04"u8.ToArray()], [".pptx"] = ["PK\x03\x04"u8.ToArray()],
        [".jar"] = ["PK\x03\x04"u8.ToArray()], [".apk"] = ["PK\x03\x04"u8.ToArray()], [".epub"] = ["PK\x03\x04"u8.ToArray()], [".odt"] = ["PK\x03\x04"u8.ToArray()],
        [".7z"] = [[0x37, 0x7A, 0xBC, 0xAF]], [".rar"] = ["Rar!"u8.ToArray()], [".gz"] = [[0x1F, 0x8B]],
        [".exe"] = ["MZ"u8.ToArray()], [".dll"] = ["MZ"u8.ToArray()],
        [".mp3"] = ["ID3"u8.ToArray(), [0xFF, 0xFB], [0xFF, 0xF3], [0xFF, 0xF2]],
        [".flac"] = ["fLaC"u8.ToArray()], [".ogg"] = ["OggS"u8.ToArray()],
        [".mkv"] = [[0x1A, 0x45, 0xDF, 0xA3]], [".webm"] = [[0x1A, 0x45, 0xDF, 0xA3]],
        [".sqlite"] = ["SQLite format 3"u8.ToArray()], [".db"] = ["SQLite format 3"u8.ToArray()],
        [".doc"] = [[0xD0, 0xCF, 0x11, 0xE0]], [".xls"] = [[0xD0, 0xCF, 0x11, 0xE0]], [".ppt"] = [[0xD0, 0xCF, 0x11, 0xE0]], [".msg"] = [[0xD0, 0xCF, 0x11, 0xE0]],
        [".rtf"] = ["{\\rtf"u8.ToArray()],
        [".psd"] = ["8BPS"u8.ToArray()],
    };
    private static readonly string[] s_isoBmff = [".mp4", ".m4a", ".m4v", ".mov", ".heic", ".heif", ".3gp"];

    /// <summary>null = no opinion (unknown extension or too few bytes), true = header matches, false = it does not.</summary>
    public static bool? LooksLike(string extension, ReadOnlySpan<byte> firstBytes)
    {
        if (s_isoBmff.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return firstBytes.Length < 8 ? null : firstBytes.Slice(4, 4).SequenceEqual("ftyp"u8);
        if (!s_magics.TryGetValue(extension, out var magics)) return null;
        foreach (var m in magics)
        {
            if (firstBytes.Length < m.Length) return null;
            if (firstBytes[..m.Length].SequenceEqual(m)) return true;
        }
        return false;
    }

    public static bool IsAllZero(ReadOnlySpan<byte> bytes) => bytes.IndexOfAnyExcept((byte)0) < 0;
}
