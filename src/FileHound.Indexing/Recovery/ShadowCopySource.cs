namespace FileHound.Indexing.Recovery;

/// <summary>One version of a path inside a snapshot (FR-18).</summary>
public sealed record ShadowVersion(ShadowCopy Snapshot, string OriginalPath, string SnapshotPath, long Size, DateTime ModifiedUtc, bool IsDirectory);

/// <summary>
/// Previous versions through <c>\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN\&lt;relative path&gt;</c>: file-level,
/// read-only access to every snapshot of a drive. Writes happen only on the user's explicit Save/Restore.
/// </summary>
public static class ShadowCopySource
{
    /// <summary>Every snapshot in which <paramref name="path"/> exists, newest first.</summary>
    public static IReadOnlyList<ShadowVersion> Versions(char letter, string path)
    {
        string full = Path.GetFullPath(path);
        if (full.Length < 3 || char.ToUpperInvariant(full[0]) != char.ToUpperInvariant(letter) || full[1] != ':' || full[2] != '\\')
            throw new ArgumentException($"The path must be on drive {char.ToUpperInvariant(letter)}:.", nameof(path));
        string relative = full[3..].TrimEnd('\\');
        var result = new List<ShadowVersion>();
        foreach (var snapshot in ShadowCopies.List(letter))
        {
            string snapPath = relative.Length == 0 ? snapshot.Device + "\\" : Path.Combine(snapshot.Device, relative);
            var v = Describe(snapshot, full, snapPath);
            if (v is not null) result.Add(v);
        }
        return result;
    }

    /// <summary>Entries of a directory version (files and folders as they were in that snapshot).</summary>
    public static IReadOnlyList<ShadowVersion> ListDirectory(ShadowVersion dir)
    {
        if (!dir.IsDirectory) throw new ArgumentException("Not a directory version.", nameof(dir));
        var result = new List<ShadowVersion>();
        foreach (var entry in new DirectoryInfo(dir.SnapshotPath).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
        {
            bool isDir = (entry.Attributes & FileAttributes.Directory) != 0;
            long size = entry is FileInfo f ? f.Length : 0;
            result.Add(new ShadowVersion(dir.Snapshot, Path.Combine(dir.OriginalPath, entry.Name), entry.FullName, size, entry.LastWriteTimeUtc, isDir));
        }
        return result.OrderBy(v => !v.IsDirectory).ThenBy(v => Path.GetFileName(v.OriginalPath), StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static ShadowVersion? Describe(ShadowCopy snapshot, string originalPath, string snapshotPath)
    {
        try
        {
            if (File.Exists(snapshotPath))
            {
                var f = new FileInfo(snapshotPath);
                return new ShadowVersion(snapshot, originalPath, snapshotPath, f.Length, f.LastWriteTimeUtc, false);
            }
            if (Directory.Exists(snapshotPath))
                return new ShadowVersion(snapshot, originalPath, snapshotPath, 0, Directory.GetLastWriteTimeUtc(snapshotPath), true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>Copies the version into a folder (any drive), keeping its name; never overwrites.</summary>
    public static string SaveTo(ShadowVersion v, string destinationFolder)
    {
        Directory.CreateDirectory(destinationFolder);
        string target = RecycleBinSource.UniquePath(Path.Combine(destinationFolder, Path.GetFileName(v.OriginalPath.TrimEnd('\\'))), " (recovered)");
        if (v.IsDirectory) RecycleBinSource.CopyTree(v.SnapshotPath, target);
        else File.Copy(v.SnapshotPath, target, overwrite: false);
        return target;
    }

    /// <summary>FR-19: puts the version next to the current file as "name (from &lt;date&gt;).ext"; never overwrites.</summary>
    public static string RestoreInPlace(ShadowVersion v)
    {
        string target = RecycleBinSource.UniquePath(RestoreName(v.OriginalPath, v.Snapshot.CreatedUtc.ToLocalTime()), "");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (v.IsDirectory) RecycleBinSource.CopyTree(v.SnapshotPath, target);
        else File.Copy(v.SnapshotPath, target, overwrite: false);
        return target;
    }

    internal static string RestoreName(string originalPath, DateTime createdLocal)
    {
        string dir = Path.GetDirectoryName(originalPath) ?? "";
        string stem = Path.GetFileNameWithoutExtension(originalPath), ext = Path.GetExtension(originalPath);
        return Path.Combine(dir, $"{stem} (from {createdLocal:yyyy-MM-dd HHmm}){ext}");
    }
}
