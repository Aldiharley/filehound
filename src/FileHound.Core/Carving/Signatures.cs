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
    ];

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
