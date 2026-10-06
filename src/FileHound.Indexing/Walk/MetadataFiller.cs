using System.IO.Enumeration;

namespace FileHound.Indexing;

/// <summary>
/// Fills size and last-write time for an index built from the MFT (which only yields names and parents)
/// by enumerating each indexed directory once, in parallel.
/// </summary>
public sealed class MetadataFiller
{
    private readonly record struct Meta(string Name, long Length, long ModifiedTicks, bool IsDirectory);

    /// <summary>True when any live entry still lacks size/date (e.g. a snapshot saved while filling).</summary>
    public static bool HasIncompleteMetadata(VolumeIndex index)
    {
        index.Lock.EnterReadLock();
        try
        {
            for (int e = 1; e < index.Count; e++)
                if (index.IsLive(e) && (index.Flags(e) & EntryFlags.MetadataKnown) == 0) return true;
            return false;
        }
        finally { index.Lock.ExitReadLock(); }
    }

    /// <param name="onlyIncomplete">Visit only directories that have live children without metadata.</param>
    public async Task FillAsync(VolumeIndex index, IProgress<double>? progress, CancellationToken ct, bool onlyIncomplete = false)
    {
        var dirs = new List<int>();
        index.Lock.EnterReadLock();
        try
        {
            bool[]? needs = null;
            if (onlyIncomplete)
            {
                needs = new bool[index.Count];
                for (int e = 1; e < index.Count; e++)
                    if (index.IsLive(e) && (index.Flags(e) & EntryFlags.MetadataKnown) == 0) needs[index.Parent(e)] = true;
            }
            if (index.FirstChild(VolumeIndex.RootEntry) > 0 && (needs is null || needs[VolumeIndex.RootEntry])) dirs.Add(VolumeIndex.RootEntry);
            for (int e = 1; e < index.Count; e++)
                if (index.IsLive(e) && index.IsDirectory(e) && (index.Flags(e) & EntryFlags.ReparsePoint) == 0 && (needs is null || needs[e]))
                    dirs.Add(e);
        }
        finally { index.Lock.ExitReadLock(); }

        int done = 0;
        long lastReport = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0, BufferSize = 64 * 1024 };
        await Parallel.ForEachAsync(dirs, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8) }, (dir, token) =>
        {
            FillDirectory(index, dir, options);
            int d = Interlocked.Increment(ref done);
            long now = Environment.TickCount64;
            if (progress is not null && now - Interlocked.Read(ref lastReport) > 250)
            {
                Interlocked.Exchange(ref lastReport, now);
                progress.Report((double)d / dirs.Count);
            }
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
        progress?.Report(1.0);
    }

    private static void FillDirectory(VolumeIndex index, int dir, EnumerationOptions options)
    {
        string path;
        var children = new List<int>();
        index.Lock.EnterReadLock();
        try
        {
            if (dir != VolumeIndex.RootEntry && !index.IsLive(dir)) return;
            path = PathBuilder.GetFullPath(index, dir);
            for (int c = index.FirstChild(dir); c > 0; c = index.NextSibling(c))
                if (index.IsLive(c)) children.Add(c);
        }
        finally { index.Lock.ExitReadLock(); }
        if (children.Count == 0) return;

        List<Meta> found;
        try
        {
            found = new FileSystemEnumerable<Meta>(path,
                (ref FileSystemEntry e) => new Meta(e.FileName.ToString(), e.IsDirectory ? 0 : e.Length, e.LastWriteTimeUtc.UtcTicks, e.IsDirectory),
                options).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return;
        }

        index.Lock.EnterReadLock();
        try
        {
            if (children.Count <= 16)
            {
                foreach (var m in found)
                    foreach (int c in children)
                        if (index.Name(c).Equals(m.Name, StringComparison.OrdinalIgnoreCase)) { index.SetMetadata(c, m.Length, m.ModifiedTicks); break; }
            }
            else
            {
                var byName = new Dictionary<string, int>(children.Count, StringComparer.OrdinalIgnoreCase);
                foreach (int c in children) byName.TryAdd(index.Name(c).ToString(), c);
                foreach (var m in found)
                    if (byName.TryGetValue(m.Name, out int c)) index.SetMetadata(c, m.Length, m.ModifiedTicks);
            }
        }
        finally { index.Lock.ExitReadLock(); }
    }
}
