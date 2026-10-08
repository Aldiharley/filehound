using FileHound.Indexing.Ntfs;

namespace FileHound.Indexing.Recovery;

/// <summary>
/// Everything recovery needs from one deleted FILE record, copied out of the record buffer. The <c>Key</c> of an
/// Undelete <see cref="FileHound.Core.Recovery.RecoveryCandidate"/>.
/// </summary>
public sealed record UndeleteRecord(
    long RecordNo, ushort Sequence, long ParentRecordNo, ushort ParentSequence, string Name, bool IsDirectory,
    long RealSize, long InitializedSize, long AllocatedSize, ushort DataFlags, byte CompressionUnit, bool DataIsResident,
    byte[]? ResidentData, byte[]? DataRunBytes, long CreatedUtcTicks, long ModifiedUtcTicks)
{
    private IReadOnlyList<DataRun>? _runs;

    /// <summary>Decoded mapping pairs (empty for resident data or a directory). Malformed runs decode to empty.</summary>
    public IReadOnlyList<DataRun> Runs
    {
        get
        {
            if (_runs is null)
            {
                try { _runs = DataRunBytes is null ? [] : DataRuns.Decode(DataRunBytes); }
                catch (InvalidDataException) { _runs = []; }
            }
            return _runs;
        }
    }

    public bool IsCompressed => (DataFlags & MftRecord.DataCompressed) != 0;
    public bool IsEncrypted => (DataFlags & MftRecord.DataEncrypted) != 0;
    public bool IsSparse => (DataFlags & MftRecord.DataSparse) != 0;
    /// <summary>NTFS only ever uses 16-cluster units for LZNT1 (unit exponent 4, or 0 on some records).</summary>
    public bool HasSupportedCompressionUnit => !IsCompressed || CompressionUnit is 0 or 4;
    /// <summary>Clusters per compression unit.</summary>
    public int ClustersPerUnit => 16;

    /// <summary>
    /// The runs, checked against the volume: every run must lie inside it and the VCNs must not exceed its size, so a
    /// corrupt record cannot make the grade or the copy loop over absurd lengths. False leaves <paramref name="runs"/> empty.
    /// </summary>
    public bool TryGetRuns(long totalClusters, out IReadOnlyList<DataRun> runs)
    {
        runs = Runs;
        long vcns = 0;
        foreach (var r in runs)
        {
            if (r.Clusters <= 0 || r.Clusters > totalClusters || (r.Lcn >= 0 && r.Lcn > totalClusters - r.Clusters) || vcns > totalClusters - r.Clusters)
            {
                runs = [];
                return false;
            }
            vcns += r.Clusters;
        }
        return true;
    }

    /// <summary>Bytes the runs can actually hold; sizes from the record are clamped to this.</summary>
    public static long Capacity(IReadOnlyList<DataRun> runs, long bytesPerCluster) => runs.Sum(r => r.Clusters) * bytesPerCluster;

    /// <summary>Whether <paramref name="child"/>'s parent reference points at this (deleted) directory record. NTFS bumps a record's sequence when it is freed, so the reference may be one behind.</summary>
    public bool IsParentOf(UndeleteRecord child) =>
        IsDirectory && child.ParentRecordNo == RecordNo && child.RecordNo != RecordNo && (Sequence == child.ParentSequence || Sequence == child.ParentSequence + 1);

    public static UndeleteRecord From(long recordNo, in MftRecord r) => new(
        recordNo, r.Sequence, r.ParentRecord, r.ParentSequence, r.Name.ToString(), r.IsDirectory,
        r.IsDirectory ? 0 : Math.Max(r.RealSize, r.HasSize ? r.Size : 0), r.InitializedSize, r.AllocatedSize, r.DataFlags, r.CompressionUnit, r.DataIsResident,
        r.DataIsResident ? r.ResidentData.ToArray() : null, r.UnnamedDataRuns.IsEmpty ? null : r.UnnamedDataRuns.ToArray(),
        r.CreatedUtcTicks, r.ModifiedUtcTicks != 0 ? r.ModifiedUtcTicks : r.NameModifiedUtcTicks);
}
