using System.Runtime.InteropServices;
using System.Security.Principal;
using FileHound.Core.Recovery;

namespace FileHound.Indexing.Recovery;

/// <summary>One recycled item: the <c>$I</c> metadata file and (usually) its <c>$R</c> data file or folder.</summary>
public sealed record RecycleBinItem(
    char Drive, string Sid, string? Account, string? MetadataPath, string? DataPath, RecycleBinMetadata? Meta,
    string DisplayName, bool IsDirectory, long Size)
{
    public bool HasData => DataPath is not null;
    public bool HasMetadata => Meta is not null;
}

/// <summary>
/// Lists and restores items in <c>X:\$Recycle.Bin\&lt;SID&gt;</c>. The current user's bin is readable without elevation;
/// other users' bins need administrator rights. Restoring moves <c>$R</c> back to the recorded path and never overwrites.
/// </summary>
public static class RecycleBinSource
{
    private const string BinFolder = "$Recycle.Bin";

    public static IReadOnlyList<RecycleBinItem> Enumerate(IEnumerable<DriveDescriptor> drives, bool allUsers)
    {
        string? mySid = WindowsIdentity.GetCurrent().User?.Value;
        var items = new List<RecycleBinItem>();
        foreach (var drive in drives)
        {
            var bin = Path.Combine(drive.Root, BinFolder);
            if (!Directory.Exists(bin)) continue;
            IEnumerable<string> sidFolders;
            try { sidFolders = Directory.EnumerateDirectories(bin); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var sidFolder in sidFolders)
            {
                string sid = Path.GetFileName(sidFolder);
                if (!sid.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)) continue;
                if (!allUsers && !string.Equals(sid, mySid, StringComparison.OrdinalIgnoreCase)) continue;
                try { EnumerateSidFolder(drive.Letter, sid, sidFolder, items); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return items;
    }

    private static void EnumerateSidFolder(char letter, string sid, string sidFolder, List<RecycleBinItem> items)
    {
        string? account = AccountName(sid);
        var metas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // id → $I path
        var datas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // id → $R path
        foreach (var entry in new DirectoryInfo(sidFolder).EnumerateFileSystemInfos("$*", SearchOption.TopDirectoryOnly))
        {
            string name = entry.Name;
            if (name.Length < 3) continue;
            string id = name[2..];
            if (name.StartsWith("$I", StringComparison.OrdinalIgnoreCase) && entry is FileInfo) metas[id] = entry.FullName;
            else if (name.StartsWith("$R", StringComparison.OrdinalIgnoreCase)) datas[id] = entry.FullName;
        }
        foreach (var (id, metaPath) in metas)
        {
            RecycleBinMetadata? meta = null;
            try
            {
                var bytes = File.ReadAllBytes(metaPath);
                RecycleBinMetadata.TryParse(bytes, out meta);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            datas.Remove(id, out var dataPath);
            bool isDir = dataPath is not null && Directory.Exists(dataPath);
            long size = meta?.Size ?? (dataPath is not null && !isDir ? SafeLength(dataPath) : 0);
            string display = meta is not null ? Path.GetFileName(meta.OriginalPath.TrimEnd('\\')) : Path.GetFileName(dataPath ?? metaPath);
            items.Add(new RecycleBinItem(letter, sid, account, metaPath, dataPath, meta, display, isDir, size));
        }
        foreach (var (id, dataPath) in datas)   // $R without $I: orphaned
        {
            bool isDir = Directory.Exists(dataPath);
            items.Add(new RecycleBinItem(letter, sid, account, null, dataPath, null, Path.GetFileName(dataPath), isDir, isDir ? 0 : SafeLength(dataPath)));
        }
    }

    public static IEnumerable<RecoveryCandidate> ToCandidates(IEnumerable<RecycleBinItem> items)
    {
        foreach (var it in items)
        {
            string? folder = it.Meta is null ? null : Path.GetDirectoryName(it.Meta.OriginalPath.TrimEnd('\\'));
            var grade = it.HasData && it.HasMetadata ? RecoveryGrade.Excellent : RecoveryGrade.Unknown;
            string? detail = !it.HasData ? "data file missing" : !it.HasMetadata ? "no metadata — original name unknown" : it.Account;
            yield return new RecoveryCandidate(RecoverySource.RecycleBin, it.DisplayName, folder, it.Size, null, it.Meta?.DeletedUtc, grade, grade == RecoveryGrade.Excellent ? 100 : 0, it.IsDirectory, detail, it);
        }
    }

    /// <summary>Moves the item back to its original path. Returns the final path. Throws <see cref="IOException"/> when the target exists and <paramref name="keepBoth"/> is false.</summary>
    public static string Restore(RecycleBinItem item, bool keepBoth)
    {
        if (item.DataPath is null) throw new FileNotFoundException("The item's data file is missing from the Recycle Bin.");
        if (item.Meta is null) throw new InvalidOperationException("The item has no metadata, so its original location is unknown.");
        string target = item.Meta.OriginalPath.TrimEnd('\\');
        string? folder = Path.GetDirectoryName(target);
        if (folder is not null) Directory.CreateDirectory(folder);
        if (File.Exists(target) || Directory.Exists(target))
        {
            if (!keepBoth) throw new IOException($"'{target}' already exists.");
            target = UniquePath(target, " (restored)");
        }
        if (item.IsDirectory) Directory.Move(item.DataPath, target);
        else File.Move(item.DataPath, target, overwrite: false);
        if (item.MetadataPath is not null) TryDelete(item.MetadataPath);
        return target;
    }

    /// <summary>Copies the item's data to a folder, keeping its original name. The bin is left untouched.</summary>
    public static string CopyTo(RecycleBinItem item, string destinationFolder)
    {
        if (item.DataPath is null) throw new FileNotFoundException("The item's data file is missing from the Recycle Bin.");
        Directory.CreateDirectory(destinationFolder);
        string target = UniquePath(Path.Combine(destinationFolder, item.DisplayName), " (recovered)");
        if (item.IsDirectory) CopyTree(item.DataPath, target);
        else File.Copy(item.DataPath, target, overwrite: false);
        return target;
    }

    /// <summary>Appends <paramref name="suffix"/> (then " 2", " 3", …) to the file stem until the path is free.</summary>
    internal static string UniquePath(string path, string suffix)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path) ?? "";
        string stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(dir, $"{stem}{suffix}{(i == 1 ? "" : " " + i)}{ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    /// <summary>Copies a tree without following junctions or symlinks (a reparse point inside a recycled folder could point anywhere, even at the drive root).</summary>
    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var f in Directory.EnumerateFiles(from, "*", options)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: false);
        foreach (var d in Directory.EnumerateDirectories(from, "*", options)) CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>"DOMAIN\user" for a SID, or null when it cannot be resolved (deleted account, offline domain).</summary>
    public static string? AccountName(string sid)
    {
        try { return new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value; }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException) { return null; }
    }
}
