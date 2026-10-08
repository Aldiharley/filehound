using FileHound.Core.Recovery;
using FileHound.Indexing.Ntfs;

namespace FileHound.Indexing.Recovery;

public sealed record UndeleteProgress(long RecordsScanned, long RecordsTotal, int Found);

/// <summary>
/// Undelete (FR-11…FR-14, FR-16): walks the $MFT through the <see cref="VolumeReader"/>, keeps every not-in-use base
/// record that still carries a name, rebuilds its folder from the live index and other deleted records, and grades
/// its data against the cluster bitmap.
/// </summary>
public sealed class MftUndeleteSource(VolumeReader reader, ClusterBitmap bitmap, VolumeIndex? liveIndex, IReadOnlyList<DeletionEntry>? deletionLog)
{
    private const long RootRecord = 5;
    private const int FirstUserRecord = 16;
    private const int MaxDepth = 64;
    private const int ChunkBytes = 4 << 20;

    private readonly Dictionary<long, UndeleteRecord> _deleted = [];
    private readonly Dictionary<(long Record, ushort Sequence), string?> _folderCache = [];

    public List<RecoveryCandidate> Scan(IProgress<UndeleteProgress>? progress, CancellationToken ct)
    {
        _deleted.Clear();
        _folderCache.Clear();
        var geometry = reader.Geometry;
        long total = reader.RecordCount;
        int recordSize = geometry.RecordSize;
        int chunk = Math.Max(recordSize, ChunkBytes / geometry.BytesPerCluster * geometry.BytesPerCluster);
        var buffer = new byte[chunk];
        long scanned = 0, lastReport = Environment.TickCount64;

        foreach (var ext in reader.MftExtents)
        {
            long extStart = ext.Vcn * geometry.BytesPerCluster;
            if (extStart >= geometry.MftValidDataLength) break;
            if (ext.Lcn < 0) continue;
            long extBytes = Math.Min(ext.Clusters * geometry.BytesPerCluster, geometry.MftValidDataLength - extStart);
            for (long done = 0; done < extBytes; done += chunk)
            {
                ct.ThrowIfCancellationRequested();
                int len = (int)Math.Min(chunk, extBytes - done);
                len -= len % recordSize;
                if (len <= 0) break;
                var span = buffer.AsSpan(0, len);
                reader.ReadBytes(ext.Lcn * geometry.BytesPerCluster + done, span);
                long first = (extStart + done) / recordSize;
                for (int i = 0; i < len / recordSize; i++)
                {
                    long recordNo = first + i;
                    if (recordNo >= FirstUserRecord) Consider(span.Slice(i * recordSize, recordSize), recordNo);
                }
                scanned += len / recordSize;
                if (progress is not null && Environment.TickCount64 - lastReport > 200)
                {
                    lastReport = Environment.TickCount64;
                    progress.Report(new UndeleteProgress(scanned, total, _deleted.Count));
                }
            }
        }
        progress?.Report(new UndeleteProgress(total, total, _deleted.Count));

        var logByRecord = deletionLog?.Where(e => e.Kind != DeletionKind.Recycled).ToDictionary(e => (e.RecordNo, e.Sequence), e => e) ?? [];
        var result = new List<RecoveryCandidate>(_deleted.Count);
        var firstCluster = new byte[geometry.BytesPerCluster];
        foreach (var r in _deleted.Values)
        {
            ct.ThrowIfCancellationRequested();
            string? folder = ResolveFolder(r);
            DateTime? deleted = null;
            string? detail = null;
            if (logByRecord.TryGetValue((r.RecordNo, r.Sequence), out var logEntry))
            {
                folder ??= logEntry.ParentPath.Length > 0 ? logEntry.ParentPath : null;
                if (logEntry.DeletedUtcTicks > 0) deleted = new DateTime(logEntry.DeletedUtcTicks, DateTimeKind.Utc);
                detail = "also in Recently deleted";
            }
            var (grade, percent, gradeDetail) = Grade(r, ReadFirstCluster(r, firstCluster));
            if (gradeDetail is not null) detail = detail is null ? gradeDetail : $"{gradeDetail}; {detail}";
            result.Add(new RecoveryCandidate(RecoverySource.Undelete, r.Name, folder, r.IsDirectory ? 0 : r.RealSize,
                r.ModifiedUtcTicks > 0 ? new DateTime(r.ModifiedUtcTicks, DateTimeKind.Utc) : null, deleted,
                grade, percent, r.IsDirectory, detail, r));
        }
        return result;
    }

    private void Consider(Span<byte> record, long recordNo)
    {
        if (!MftRecordParser.ApplyFixups(record) || !MftRecordParser.TryParse(record, out var r)) return;
        if (r.InUse || !r.IsBaseRecord || !r.HasName) return;
        if (r.ParentRecord == RootRecord && r.Name.Length > 0 && r.Name[0] == '$') return; // NTFS metafiles
        _deleted[recordNo] = UndeleteRecord.From(recordNo, r);
    }

    private ReadOnlySpan<byte> ReadFirstCluster(UndeleteRecord r, byte[] buffer)
    {
        if (r.IsDirectory || r.DataIsResident) return default;
        var run = r.Runs.FirstOrDefault(x => x.Lcn >= 0);
        if (run.Clusters == 0 || run.Lcn < 0 || run.Lcn >= reader.Geometry.TotalClusters) return default;
        try { reader.ReadClusters(run.Lcn, 1, buffer); }
        catch (IOException) { return default; }
        return buffer;
    }

    /// <summary>FR-13: live parent when it is in use; deleted parents when their sequence matches the reference (or +1); else null.</summary>
    internal string? ResolveFolder(UndeleteRecord r) => ResolveFolder(r.ParentRecordNo, r.ParentSequence, 0);

    private string? ResolveFolder(long parentRecord, ushort parentSequence, int depth)
    {
        if (parentRecord == RootRecord) return liveIndex?.Root ?? null;
        if (depth > MaxDepth) return null;
        if (_folderCache.TryGetValue((parentRecord, parentSequence), out var cached)) return cached;
        string? folder = null;
        if (liveIndex is not null)
        {
            liveIndex.Lock.EnterReadLock();
            try
            {
                int e = liveIndex.FindByRecord(parentRecord);
                if (e > 0 && liveIndex.IsLive(e) && liveIndex.IsDirectory(e)) folder = PathBuilder.GetFullPath(liveIndex, e);
            }
            finally { liveIndex.Lock.ExitReadLock(); }
        }
        if (folder is null && _deleted.TryGetValue(parentRecord, out var parent) && parent.IsDirectory
            && (parent.Sequence == parentSequence || parent.Sequence == parentSequence + 1))
        {
            var grand = ResolveFolder(parent.ParentRecordNo, parent.ParentSequence, depth + 1);
            if (grand is not null) folder = Path.Combine(grand, parent.Name);
        }
        _folderCache[(parentRecord, parentSequence)] = folder;
        return folder;
    }

    /// <summary>FR-14.</summary>
    internal (RecoveryGrade Grade, int Percent, string? Detail) Grade(UndeleteRecord r, ReadOnlySpan<byte> firstCluster)
    {
        if (r.IsEncrypted) return (RecoveryGrade.Encrypted, 0, "EFS-encrypted");
        if (r.IsDirectory) return (RecoveryGrade.Excellent, 100, null);
        if (r.DataIsResident) return (RecoveryGrade.Excellent, 100, null);
        var runs = r.Runs;
        if (runs.Count == 0) return r.RealSize == 0 ? (RecoveryGrade.Excellent, 100, null) : (RecoveryGrade.Unknown, 0, "no data runs in the record");
        string? detail = r.IsCompressed ? "Compressed (LZNT1)" : null;
        long cluster = reader.Geometry.BytesPerCluster;
        // Only clusters that were ever written matter; a compressed file's runs are all meaningful.
        long needed = r.IsCompressed ? runs.Sum(x => x.Clusters) : ((r.InitializedSize > 0 ? r.InitializedSize : r.RealSize) + cluster - 1) / cluster;
        var (allocated, total) = bitmap.Count(runs, needed);
        if (total == 0) return (RecoveryGrade.Excellent, 100, detail);                 // only sparse runs
        if (allocated == total) return (RecoveryGrade.Overwritten, 0, detail);
        if (allocated > 0) return (RecoveryGrade.Partial, (int)(100 * (total - allocated) / total), detail);
        if (!firstCluster.IsEmpty && r.InitializedSize > 0 && ContentCheck.IsAllZero(firstCluster[..(int)Math.Min(firstCluster.Length, Math.Max(16, r.InitializedSize))]))
            return (RecoveryGrade.Zeroed, 0, detail);
        if (!r.IsCompressed && !firstCluster.IsEmpty && ContentCheck.LooksLike(Path.GetExtension(r.Name), firstCluster) == false)
            return (RecoveryGrade.Good, 100, Join(detail, "header does not match the file type"));
        return (RecoveryGrade.Excellent, 100, detail);
    }

    private static string Join(string? a, string b) => a is null ? b : $"{a}; {b}";
}
