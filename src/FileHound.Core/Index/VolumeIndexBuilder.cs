namespace FileHound.Core.Index;

/// <summary>
/// Bulk, lock-free construction of a <see cref="VolumeIndex"/> from (record, parentRecord) pairs that may arrive in any
/// order, as produced by MFT enumeration. Entries whose parent cannot be resolved become deleted orphans.
/// </summary>
public sealed class VolumeIndexBuilder
{
    private readonly VolumeIndex _index;
    private readonly List<long> _records;
    private readonly List<long> _parentRecords;

    public VolumeIndexBuilder(string root, IndexMode mode, int capacityHint = 1 << 16)
    {
        _index = new VolumeIndex(root, mode, capacityHint + 1);
        _records = new List<long>(capacityHint + 1) { -1 };
        _parentRecords = new List<long>(capacityHint + 1) { -1 };
    }

    public int Count => _records.Count - 1;

    public void AddRecord(long recordNo, long parentRecordNo, ReadOnlySpan<char> name, EntryFlags flags, long size = 0, long modifiedTicks = 0)
    {
        _index.AppendRaw(-1, name, flags, size, modifiedTicks);
        _records.Add(recordNo);
        _parentRecords.Add(parentRecordNo);
    }

    /// <summary>Resolves parents and returns the finished index. The builder must not be used afterwards.</summary>
    public VolumeIndex Build(long rootRecordNo = 5)
    {
        var v = _index;
        int n = v.Count;
        v.SetRecordCore(VolumeIndex.RootEntry, rootRecordNo);
        for (int e = 1; e < n; e++)
        {
            long rec = _records[e];
            if (rec == rootRecordNo) continue; // never shadow the root
            v.SetRecordCore(e, rec);
        }

        var parents = new int[n];
        parents[0] = -1;
        for (int e = 1; e < n; e++)
        {
            long pr = _parentRecords[e];
            int p = pr == rootRecordNo ? VolumeIndex.RootEntry : v.FindByRecord(pr);
            if (_records[e] == rootRecordNo || p == e) p = -1;
            parents[e] = p; // -1 => orphan, handled by FinalizeStructure
        }
        v.FinalizeStructure(parents);
        v.UnmapDeletedRecords();
        v.TrimExcess();
        v.IsDirty = true;
        return v;
    }
}
