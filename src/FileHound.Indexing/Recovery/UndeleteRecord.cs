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
    /// <summary>Clusters per compression unit (16 when the record says 0 but the file is compressed).</summary>
    public int ClustersPerUnit => CompressionUnit == 0 ? 16 : 1 << CompressionUnit;

    public static UndeleteRecord From(long recordNo, in MftRecord r) => new(
        recordNo, r.Sequence, r.ParentRecord, r.ParentSequence, r.Name.ToString(), r.IsDirectory,
        r.IsDirectory ? 0 : Math.Max(r.RealSize, r.HasSize ? r.Size : 0), r.InitializedSize, r.AllocatedSize, r.DataFlags, r.CompressionUnit, r.DataIsResident,
        r.DataIsResident ? r.ResidentData.ToArray() : null, r.UnnamedDataRuns.IsEmpty ? null : r.UnnamedDataRuns.ToArray(),
        r.CreatedUtcTicks, r.ModifiedUtcTicks != 0 ? r.ModifiedUtcTicks : r.NameModifiedUtcTicks);
}
