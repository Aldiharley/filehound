using System.Security.Cryptography;
using FileHound.Core.Recovery;

namespace FileHound.Indexing.Recovery;

/// <summary>Copies a carved file's byte range to a destination (never overwriting), hashing on the way; marks executables as from the web.</summary>
public static class CarveWriter
{
    private const int MaxClustersPerRead = 1024;
    private static readonly HashSet<string> s_executable = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".dll", ".scr", ".msi", ".bat", ".cmd", ".ps1", ".js", ".vbs", ".lnk", ".com", ".jar", ".hta" };

    /// <returns>Bytes written, SHA-256, byte runs, and the final path (renamed to <c>.recovered</c> when a program could not be marked).</returns>
    public static (long Bytes, string Sha256, IReadOnlyList<ByteRun> Runs, string FinalPath) Recover(VolumeReader reader, CarvedFile file, string destPath, CancellationToken ct)
    {
        long cluster = reader.Geometry.BytesPerCluster;
        if (file.Size <= 0 || file.StartLcn < 0 || file.StartLcn * cluster + file.Size > reader.Geometry.TotalClusters * cluster)
            throw new InvalidDataException("The carved range lies outside the volume.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var output = new FileStream(destPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        try
        {
            var buffer = new byte[MaxClustersPerRead * cluster];
            long done = 0;
            while (done < file.Size)
            {
                ct.ThrowIfCancellationRequested();
                long remaining = file.Size - done;
                int clusters = (int)Math.Min(MaxClustersPerRead, (remaining + cluster - 1) / cluster);
                reader.ReadClusters(file.StartLcn + done / cluster, clusters, buffer);
                int take = (int)Math.Min(clusters * cluster, remaining);
                output.Write(buffer, 0, take);
                hash.AppendData(buffer, 0, take);
                done += take;
            }
            output.Dispose();
        }
        catch
        {
            output.Dispose();
            try { File.Delete(destPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
        string finalPath = destPath;
        if (s_executable.Contains(Path.GetExtension(destPath)) && !TryMarkOfTheWeb(destPath))
        {
            // No alternate data streams here (exFAT/FAT32 stick): make the file inert instead, so it cannot be double-clicked into running.
            finalPath = RecycleBinSource.UniquePath(destPath + ".recovered", "");
            File.Move(destPath, finalPath);
        }
        return (file.Size, Convert.ToHexStringLower(hash.GetHashAndReset()), [new ByteRun(0, file.Size, file.StartLcn * cluster)], finalPath);
    }

    /// <summary>§8: SmartScreen treats the recovered program as untrusted (Zone.Identifier = Internet). False when the destination has no streams.</summary>
    public static bool TryMarkOfTheWeb(string path)
    {
        try { File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n"); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            IndexManager.Log?.Invoke($"Zone.Identifier not written for {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>FR-23: a carved file that starts where an undelete candidate's data starts is the same file; the named record wins.</summary>
    public static List<CarvedFile> Deduplicate(IEnumerable<CarvedFile> carved, IEnumerable<RecoveryCandidate> undelete)
    {
        var starts = new HashSet<long>();
        foreach (var c in undelete)
        {
            if (c.Key is UndeleteRecord r && !r.DataIsResident)
            {
                var first = r.Runs.FirstOrDefault(x => x.Lcn >= 0);
                if (first.Clusters > 0) starts.Add(first.Lcn);
            }
        }
        return carved.Where(f => !starts.Contains(f.StartLcn)).ToList();
    }
}
