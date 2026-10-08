using FileHound.Core.Index;

namespace FileHound.Core.Carving;

/// <summary>
/// The carving table. Magics follow Scalpel/Foremost conventions; every validator is in this assembly. Dispatch is by
/// the first byte of a cluster (<see cref="ByFirstByte"/>); the few formats whose magic sits later in the file
/// (<see cref="AtOffset"/>) are checked at every cluster start.
/// </summary>
public static class Signatures
{
    private const long MB = 1 << 20;

    public static IReadOnlyList<CarveType> All { get; } =
    [
        new("jpg", "JPEG image", ".jpg", FileCategory.Image, 64 * MB, 0, [[0xFF, 0xD8, 0xFF]], ImageValidators.Jpeg),
        new("png", "PNG image", ".png", FileCategory.Image, 64 * MB, 0, [[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]], ImageValidators.Png),
        new("gif", "GIF image", ".gif", FileCategory.Image, 32 * MB, 0, ["GIF87a"u8.ToArray(), "GIF89a"u8.ToArray()], ImageValidators.Gif),
        new("bmp", "Bitmap image", ".bmp", FileCategory.Image, 128 * MB, 0, ["BM"u8.ToArray()], ImageValidators.Bmp),
        new("tif", "TIFF image", ".tif", FileCategory.Image, 512 * MB, 0, [[0x49, 0x49, 0x2A, 0x00], [0x4D, 0x4D, 0x00, 0x2A]], ImageValidators.Tiff),
        new("webp", "WebP image", ".webp", FileCategory.Image, 64 * MB, 0, ["RIFF"u8.ToArray()], ImageValidators.WebP),
        new("wav", "WAV audio", ".wav", FileCategory.Audio, 2048 * MB, 0, ["RIFF"u8.ToArray()], MediaValidators.Riff),
        new("mp4", "MP4 / MOV video", ".mp4", FileCategory.Video, 4096 * MB, 4, ["ftyp"u8.ToArray()], MediaValidators.IsoBmff),
        new("mkv", "Matroska / WebM video", ".mkv", FileCategory.Video, 4096 * MB, 0, [[0x1A, 0x45, 0xDF, 0xA3]], MediaValidators.Mkv),
        new("ogg", "OGG audio", ".ogg", FileCategory.Audio, 512 * MB, 0, ["OggS"u8.ToArray()], MediaValidators.Ogg),
        new("mp3", "MP3 audio", ".mp3", FileCategory.Audio, 512 * MB, 0, ["ID3"u8.ToArray(), [0xFF, 0xFB], [0xFF, 0xFA], [0xFF, 0xF3], [0xFF, 0xF2]], MediaValidators.Mp3),
        new("flac", "FLAC audio", ".flac", FileCategory.Audio, 1024 * MB, 0, ["fLaC"u8.ToArray()], MediaValidators.Flac),
        new("pdf", "PDF document", ".pdf", FileCategory.Document, 512 * MB, 0, ["%PDF-"u8.ToArray()], DocumentValidators.Pdf),
        new("zip", "ZIP archive", ".zip", FileCategory.Archive, 4096 * MB, 0, [[0x50, 0x4B, 0x03, 0x04]], DocumentValidators.Zip),
        new("7z", "7-Zip archive", ".7z", FileCategory.Archive, 4096 * MB, 0, [[0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]], DocumentValidators.SevenZip),
        new("rar", "RAR archive", ".rar", FileCategory.Archive, 4096 * MB, 0, [[0x52, 0x61, 0x72, 0x21, 0x1A, 0x07]], DocumentValidators.Rar),
        new("gz", "GZIP archive", ".gz", FileCategory.Archive, 256 * MB, 0, [[0x1F, 0x8B, 0x08]], DocumentValidators.Gzip),
        new("sqlite", "SQLite database", ".sqlite", FileCategory.Document, 2048 * MB, 0, ["SQLite format 3\0"u8.ToArray()], DocumentValidators.Sqlite),
        new("exe", "Windows program", ".exe", FileCategory.App, 512 * MB, 0, ["MZ"u8.ToArray()], DocumentValidators.Pe),
        new("ole", "Office 97-2003 document", ".doc", FileCategory.Document, 512 * MB, 0, [[0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]], DocumentValidators.Ole2),
        new("rtf", "Rich Text document", ".rtf", FileCategory.Document, 64 * MB, 0, ["{\\rtf"u8.ToArray()], DocumentValidators.Rtf),
        new("pst", "Outlook data file", ".pst", FileCategory.Document, 4096 * MB, 0, ["!BDN"u8.ToArray()], DocumentValidators.Pst),
        new("lnk", "Windows shortcut", ".lnk", FileCategory.Other, 1 * MB, 0, [[0x4C, 0x00, 0x00, 0x00, 0x01, 0x14, 0x02, 0x00]], DocumentValidators.Lnk),
    ];

    /// <summary>The extension to use for a carved file: the validator's subtype (docx, epub, dll…) when it reported one.</summary>
    public static string ExtensionFor(CarveType type, string? info) => type.Id switch
    {
        "zip" when info is "docx" or "xlsx" or "pptx" or "epub" or "odt" or "ods" or "odp" or "jar" => "." + info,
        "ole" when info is "xls" or "ppt" or "msg" => "." + info,
        "exe" when info is not null && info.EndsWith("DLL", StringComparison.Ordinal) => ".dll",
        _ => type.Extension,
    };

    private static readonly CarveType[][] s_byFirstByte = Build();
    private static readonly Dictionary<string, CarveType> s_byId = All.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>Types whose magic starts with the byte (magic at offset 0), in table order.</summary>
    public static IReadOnlyList<CarveType> ByFirstByte(byte b) => s_byFirstByte[b];

    /// <summary>Types whose magic is not at the start (ISO-BMFF's <c>ftyp</c> at 4).</summary>
    public static IReadOnlyList<CarveType> AtOffset { get; } = All.Where(t => t.MagicOffset > 0).ToList();

    public static CarveType? ById(string id) => s_byId.GetValueOrDefault(id);

    private static CarveType[][] Build()
    {
        var lists = new List<CarveType>[256];
        for (int i = 0; i < 256; i++) lists[i] = [];
        foreach (var t in All)
        {
            if (t.MagicOffset != 0) continue;
            foreach (var m in t.Magics) if (!lists[m[0]].Contains(t)) lists[m[0]].Add(t);
        }
        return lists.Select(l => l.ToArray()).ToArray();
    }
}
