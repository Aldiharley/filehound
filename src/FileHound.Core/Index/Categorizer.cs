using System.Collections.Frozen;

namespace FileHound.Core.Index;

/// <summary>Maps file extensions to <see cref="FileCategory"/> buckets used by filter chips and statistics.</summary>
public static class Categorizer
{
    private static readonly Dictionary<FileCategory, string[]> s_extensions = new()
    {
        [FileCategory.Document] = ["pdf", "doc", "docx", "xls", "xlsx", "xlsm", "ppt", "pptx", "odt", "ods", "odp", "rtf", "txt", "md", "csv", "epub", "pages", "numbers", "key", "one", "tex", "log"],
        [FileCategory.Image] = ["jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "webp", "heic", "heif", "avif", "svg", "ico", "raw", "cr2", "cr3", "nef", "arw", "dng", "psd", "ai", "xcf"],
        [FileCategory.Video] = ["mp4", "mkv", "avi", "mov", "wmv", "webm", "m4v", "mpg", "mpeg", "flv", "3gp", "mts", "m2ts", "vob"],
        [FileCategory.Audio] = ["mp3", "flac", "wav", "aac", "m4a", "ogg", "opus", "wma", "aiff", "aif", "mid", "midi", "alac"],
        [FileCategory.Archive] = ["zip", "7z", "rar", "tar", "gz", "tgz", "bz2", "xz", "zst", "iso", "cab", "lz", "lzma", "wim", "vhd", "vhdx"],
        [FileCategory.App] = ["exe", "msi", "bat", "cmd", "ps1", "com", "scr", "lnk", "appx", "msix", "appxbundle", "msixbundle", "jar", "apk"],
        [FileCategory.Code] = ["cs", "c", "cpp", "cc", "h", "hpp", "py", "js", "mjs", "ts", "tsx", "jsx", "java", "go", "rs", "rb", "php", "swift", "kt", "sql", "sh", "json", "xml", "yaml", "yml", "toml", "html", "htm", "css", "scss", "vue", "xaml", "csproj", "sln", "lua", "dart"],
    };

    private static readonly FrozenDictionary<string, FileCategory> s_byExt = BuildMap();
    private static readonly FrozenDictionary<string, FileCategory>.AlternateLookup<ReadOnlySpan<char>> s_lookup =
        s_byExt.GetAlternateLookup<ReadOnlySpan<char>>();

    private static readonly FrozenDictionary<string, FileCategory> s_aliases = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase)
    {
        ["doc"] = FileCategory.Document, ["docs"] = FileCategory.Document, ["document"] = FileCategory.Document, ["documents"] = FileCategory.Document,
        ["image"] = FileCategory.Image, ["images"] = FileCategory.Image, ["pic"] = FileCategory.Image, ["pics"] = FileCategory.Image, ["picture"] = FileCategory.Image, ["pictures"] = FileCategory.Image, ["photo"] = FileCategory.Image, ["photos"] = FileCategory.Image,
        ["video"] = FileCategory.Video, ["videos"] = FileCategory.Video, ["movie"] = FileCategory.Video, ["movies"] = FileCategory.Video,
        ["audio"] = FileCategory.Audio, ["music"] = FileCategory.Audio, ["sound"] = FileCategory.Audio,
        ["archive"] = FileCategory.Archive, ["archives"] = FileCategory.Archive, ["zip"] = FileCategory.Archive, ["compressed"] = FileCategory.Archive,
        ["app"] = FileCategory.App, ["apps"] = FileCategory.App, ["exe"] = FileCategory.App, ["program"] = FileCategory.App, ["programs"] = FileCategory.App,
        ["code"] = FileCategory.Code, ["source"] = FileCategory.Code,
        ["folder"] = FileCategory.Folder, ["folders"] = FileCategory.Folder, ["dir"] = FileCategory.Folder,
        ["other"] = FileCategory.Other,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static FrozenDictionary<string, FileCategory> BuildMap()
    {
        var map = new Dictionary<string, FileCategory>(StringComparer.Ordinal);
        // Order matters: App is registered after Code so ps1/bat/cmd stay App.
        foreach (var cat in new[] { FileCategory.Code, FileCategory.Document, FileCategory.Image, FileCategory.Video, FileCategory.Audio, FileCategory.Archive, FileCategory.App })
            foreach (var ext in s_extensions[cat])
                map[ext] = cat;
        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>Category for an already-lowercased file name.</summary>
    public static FileCategory FromName(ReadOnlySpan<char> foldName, bool isDirectory)
    {
        if (isDirectory) return FileCategory.Folder;
        var ext = Extension(foldName);
        if (ext.IsEmpty || ext.Length > 12) return FileCategory.Other;
        return s_lookup.TryGetValue(ext, out var c) ? c : FileCategory.Other;
    }

    /// <summary>Text after the last '.', empty when there is none, the name starts with its only dot, or the dot is last.</summary>
    public static ReadOnlySpan<char> Extension(ReadOnlySpan<char> name)
    {
        int dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1) return [];
        return name[(dot + 1)..];
    }

    public static IReadOnlyList<string> ExtensionsOf(FileCategory c) =>
        s_extensions.TryGetValue(c, out var list) ? list : [];

    public static bool TryParseCategory(string token, out FileCategory category) =>
        s_aliases.TryGetValue(token.Trim(), out category);
}
