using System.Diagnostics;
using FileHound.Core.Carving;

namespace FileHound.Indexing.Recovery;

/// <summary>
/// FR-21/FR-25: walks the volume's free clusters (contiguous runs from the bitmap) in large chunks, tries every
/// signature at each cluster start, and lets a validator measure the file. When a validator needs more bytes the
/// window grows (doubling) up to the type's maximum or the end of the free run. Reads only; pause/resume/cancel.
/// </summary>
public sealed class Carver(VolumeReader reader, ClusterBitmap bitmap)
{
    private const long MaxWindow = 512L << 20;
    private readonly ManualResetEventSlim _gate = new(true);

    /// <summary>Clusters per read (default 4 MB worth).</summary>
    public int ChunkClusters { get; init; } = Math.Max(1, (4 << 20) / reader.Geometry.BytesPerCluster);
    /// <summary>Type ids to look for; null = every type in the table.</summary>
    public IReadOnlyCollection<string>? TypeFilter { get; init; }
    /// <summary>Raised from the scan thread with up to 200 new finds at a time.</summary>
    public event Action<IReadOnlyList<CarvedFile>>? Batch;

    public bool IsPaused => !_gate.IsSet;
    public void Pause() => _gate.Reset();
    public void Resume() => _gate.Set();

    /// <summary>Contiguous runs of free clusters.</summary>
    internal static IEnumerable<(long Lcn, long Clusters)> FreeRuns(ClusterBitmap bitmap)
    {
        long start = -1;
        for (long lcn = 0; lcn < bitmap.TotalClusters; lcn++)
        {
            bool free = !bitmap.IsAllocated(lcn);
            if (free && start < 0) start = lcn;
            else if (!free && start >= 0) { yield return (start, lcn - start); start = -1; }
        }
        if (start >= 0) yield return (start, bitmap.TotalClusters - start);
    }

    public List<CarvedFile> Run(IProgress<CarveProgress>? progress, CancellationToken ct)
    {
        var types = TypeFilter is null ? Signatures.All : Signatures.All.Where(t => TypeFilter.Contains(t.Id)).ToList();
        var byFirst = new List<CarveType>[256];
        for (int i = 0; i < 256; i++) byFirst[i] = [];
        var atOffset = new List<CarveType>();
        foreach (var t in types)
        {
            if (t.MagicOffset == 0) { foreach (var m in t.Magics) if (!byFirst[m[0]].Contains(t)) byFirst[m[0]].Add(t); }
            else atOffset.Add(t);
        }

        int cluster = reader.Geometry.BytesPerCluster;
        var runs = FreeRuns(bitmap).ToList();
        long freeBytes = runs.Sum(r => r.Clusters) * cluster;
        var found = new List<CarvedFile>();
        var pending = new List<CarvedFile>();
        var chunkBuffer = new byte[(long)ChunkClusters * cluster];
        long scanned = 0;
        var clock = Stopwatch.StartNew();
        long lastReport = 0;

        foreach (var run in runs)
        {
            long pos = 0;           // cluster index within the run
            long nextAllowed = 0;   // clusters below this are covered by a file already found
            while (pos < run.Clusters)
            {
                ct.ThrowIfCancellationRequested();
                _gate.Wait(ct);
                int chunk = (int)Math.Min(ChunkClusters, run.Clusters - pos);
                var span = chunkBuffer.AsSpan(0, chunk * cluster);
                reader.ReadClusters(run.Lcn + pos, chunk, span);
                for (int c = 0; c < chunk; c++)
                {
                    long index = pos + c;
                    if (index < nextAllowed) continue;
                    var head = span[(c * cluster)..];
                    var hit = Match(head, byFirst[head[0]], atOffset, run, index, span.Length - c * cluster, chunkBuffer, c * cluster);
                    if (hit is null) continue;
                    found.Add(hit);
                    pending.Add(hit);
                    nextAllowed = index + Math.Max(1, (hit.Size + cluster - 1) / cluster);
                    if (pending.Count >= 200) { Batch?.Invoke(pending.ToArray()); pending.Clear(); }
                }
                pos += chunk;
                scanned += (long)chunk * cluster;
                if (progress is not null && clock.ElapsedMilliseconds - lastReport > 200)
                {
                    lastReport = clock.ElapsedMilliseconds;
                    progress.Report(new CarveProgress(scanned, freeBytes, found.Count, Eta(scanned, freeBytes, clock.Elapsed)));
                }
            }
        }
        if (pending.Count > 0) Batch?.Invoke(pending.ToArray());
        progress?.Report(new CarveProgress(freeBytes, freeBytes, found.Count, TimeSpan.Zero));
        return found;
    }

    private static TimeSpan? Eta(long done, long total, TimeSpan elapsed)
    {
        if (done <= 0 || elapsed.TotalSeconds < 1) return null;
        double rate = done / elapsed.TotalSeconds;
        return rate <= 0 ? null : TimeSpan.FromSeconds((total - done) / rate);
    }

    /// <summary>Tries the candidate types at one cluster; grows the window on NeedMore. First type (table order) that validates wins.</summary>
    private CarvedFile? Match(ReadOnlySpan<byte> head, List<CarveType> byFirst, List<CarveType> atOffset, (long Lcn, long Clusters) run, long index,
        int availableInChunk, byte[] chunkBuffer, int chunkOffset)
    {
        int cluster = reader.Geometry.BytesPerCluster;
        long remainingBytes = (run.Clusters - index) * cluster;
        foreach (var list in new[] { byFirst, atOffset })
        {
            foreach (var type in list)
            {
                if (!type.MatchesMagic(head)) continue;
                var r = type.Validate(head[..Math.Min(head.Length, (int)Math.Min(type.MaxSize, availableInChunk))]);
                long window = availableInChunk;
                long limit = Math.Min(Math.Min(type.MaxSize, remainingBytes), MaxWindow);
                byte[]? grown = null;
                while (r.Status == CarveStatus.NeedMore && window < limit)
                {
                    window = Math.Min(limit, Math.Max(window * 2, (long)cluster * 64));
                    int clusters = (int)((window + cluster - 1) / cluster);
                    if (grown is null || grown.Length < clusters * (long)cluster) grown = new byte[(long)clusters * cluster];
                    reader.ReadClusters(run.Lcn + index, clusters, grown);
                    r = type.Validate(grown.AsSpan(0, (int)Math.Min(window, (long)clusters * cluster)));
                }
                if (r.Status == CarveStatus.Ok && r.Size > 0 && r.Size <= remainingBytes)
                    return new CarvedFile(type, run.Lcn + index, r.Size, r.Info);
            }
        }
        return null;
    }
}
