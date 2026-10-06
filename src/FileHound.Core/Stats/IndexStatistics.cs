using FileHound.Core.Index;

namespace FileHound.Core.Stats;

public sealed record CategoryCount(FileCategory Category, long Count);

public sealed record LargeFile(VolumeIndex Volume, int Entry, long Size);

/// <summary>Aggregate numbers for the dashboard: counts, file-type breakdown and the largest files.</summary>
public sealed record IndexStatistics(long Files, long Folders, long TotalBytes, IReadOnlyList<CategoryCount> Categories, IReadOnlyList<LargeFile> Largest)
{
    public static readonly IndexStatistics Empty = new(0, 0, 0, [], []);

    public static IndexStatistics Compute(IReadOnlyList<VolumeIndex> volumes, int largestCount = 5)
    {
        long files = 0, folders = 0, bytes = 0;
        var cats = new long[Enum.GetValues<FileCategory>().Length];
        var largest = new PriorityQueue<LargeFile, long>(); // min-heap on size
        foreach (var v in volumes)
        {
            v.Lock.EnterReadLock();
            try
            {
                int count = v.Count;
                for (int e = 1; e < count; e++)
                {
                    var flags = v.Flags(e);
                    if ((flags & EntryFlags.Deleted) != 0) continue;
                    if ((flags & EntryFlags.Directory) != 0) { folders++; continue; }
                    files++;
                    cats[(int)v.Category(e)]++;
                    if ((flags & EntryFlags.MetadataKnown) == 0) continue;
                    long size = v.Size(e);
                    bytes += size;
                    if (largestCount <= 0) continue;
                    if (largest.Count < largestCount) largest.Enqueue(new LargeFile(v, e, size), size);
                    else if (largest.TryPeek(out _, out long min) && size > min) largest.EnqueueDequeue(new LargeFile(v, e, size), size);
                }
            }
            finally { v.Lock.ExitReadLock(); }
        }

        var top = new List<LargeFile>(largest.Count);
        while (largest.Count > 0) top.Add(largest.Dequeue());
        top.Reverse();
        var categories = cats.Select((c, i) => new CategoryCount((FileCategory)i, c))
            .Where(c => c.Count > 0 && c.Category != FileCategory.Folder)
            .OrderByDescending(c => c.Count)
            .ToList();
        return new IndexStatistics(files, folders, bytes, categories, top);
    }
}
