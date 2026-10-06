using System.Runtime.CompilerServices;

namespace FileHound.Core.Index;

/// <summary>
/// Compact in-memory index of every file and folder under one root (normally a drive).
/// Struct-of-arrays layout; entry ids are stable until the index is rebuilt or reloaded.
/// </summary>
/// <remarks>
/// Threading: read accessors are lock-free and require the caller to hold <see cref="Lock"/> in read mode
/// (or to be the only user). Structural mutators (<see cref="Add"/>, <see cref="Rename"/>, <see cref="Delete"/>)
/// take the write lock themselves. <see cref="SetMetadata"/> only requires the read lock.
/// </remarks>
public sealed class VolumeIndex
{
    public const int RootEntry = 0;
    private const int MaxChainLength = 4096;

    private int _count;
    private int _liveCount;
    private int[] _parent;
    private int[] _firstChild;
    private int[] _nextSibling;
    private int[] _nameStart;
    private ushort[] _nameLen;
    private EntryFlags[] _flags;
    private FileCategory[] _category;
    private byte[] _depth;
    private ulong[] _mask;
    private long[] _size;
    private long[] _modified;
    private char[] _names;
    private char[] _fold;
    private int _arenaLength;

    private int[]? _recordMap;    // record number -> entry
    private long[]? _entryRecord; // entry -> record number

    public VolumeIndex(string root, IndexMode mode, int capacity = 1024)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        Root = root.EndsWith('\\') ? root : root + "\\";
        RootName = Root.TrimEnd('\\');
        DriveLetter = char.ToUpperInvariant(Root[0]);
        Mode = mode;
        capacity = Math.Max(capacity, 2);
        _parent = new int[capacity];
        _firstChild = new int[capacity];
        _nextSibling = new int[capacity];
        _nameStart = new int[capacity];
        _nameLen = new ushort[capacity];
        _flags = new EntryFlags[capacity];
        _category = new FileCategory[capacity];
        _depth = new byte[capacity];
        _mask = new ulong[capacity];
        _size = new long[capacity];
        _modified = new long[capacity];
        _names = new char[Math.Max(capacity * 16, 64)];
        _fold = new char[_names.Length];
        AppendRaw(-1, RootName, EntryFlags.Directory, 0, 0);
    }

    /// <summary>Root path with trailing backslash, e.g. <c>C:\</c>.</summary>
    public string Root { get; }
    /// <summary>Root without trailing backslash; the name of entry 0, e.g. <c>C:</c>.</summary>
    public string RootName { get; }
    public char DriveLetter { get; }
    public IndexMode Mode { get; set; }
    public uint VolumeSerial { get; set; }
    public ulong UsnJournalId { get; set; }
    public long NextUsn { get; set; }
    public ReaderWriterLockSlim Lock { get; } = new(LockRecursionPolicy.SupportsRecursion);

    private volatile bool _dirty;
    public bool IsDirty { get => _dirty; set => _dirty = value; }

    /// <summary>Number of allocated entries, including the root and deleted entries.</summary>
    public int Count => _count;
    /// <summary>Number of live entries, excluding the root.</summary>
    public int LiveCount => _liveCount;
    public int ArenaLength => _arenaLength;

    // ---------------------------------------------------------------- read accessors

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<char> Name(int e) => new(_names, _nameStart[e], _nameLen[e]);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<char> FoldName(int e) => new(_fold, _nameStart[e], _nameLen[e]);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Parent(int e) => _parent[e];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EntryFlags Flags(int e) => _flags[e];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FileCategory Category(int e) => _category[e];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Depth(int e) => _depth[e];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Mask(int e) => _mask[e];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Size(int e) => _size[e];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ModifiedTicks(int e) => _modified[e];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsLive(int e) => e > 0 && e < _count && (_flags[e] & EntryFlags.Deleted) == 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsDirectory(int e) => (_flags[e] & EntryFlags.Directory) != 0;
    public int FirstChild(int e) => _firstChild[e];
    public int NextSibling(int e) => _nextSibling[e];
    public long RecordOf(int e) => _entryRecord is { } r && e < r.Length ? r[e] : -1;
    public bool HasRecords => _entryRecord is not null;

    // ---------------------------------------------------------------- mutations

    /// <summary>Adds a child of <paramref name="parent"/>. Returns the new entry id.</summary>
    public int Add(int parent, ReadOnlySpan<char> name, EntryFlags flags, long size, long modifiedTicks, long recordNo = -1)
    {
        Lock.EnterWriteLock();
        try
        {
            if ((uint)parent >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(parent));
            int e = AppendRaw(parent, name, flags, size, modifiedTicks);
            LinkChild(parent, e);
            _depth[e] = (byte)Math.Min(255, _depth[parent] + 1);
            if ((_flags[parent] & EntryFlags.Deleted) != 0) _flags[e] |= EntryFlags.Deleted;
            if ((_flags[e] & EntryFlags.Deleted) == 0) _liveCount++;
            if (recordNo >= 0) SetRecordCore(e, recordNo);
            _dirty = true;
            return e;
        }
        finally { Lock.ExitWriteLock(); }
    }

    /// <summary>Renames and/or moves an entry. Descendant depths are updated on move.</summary>
    public void Rename(int e, int newParent, ReadOnlySpan<char> newName)
    {
        Lock.EnterWriteLock();
        try
        {
            if (e <= 0 || e >= _count) throw new ArgumentOutOfRangeException(nameof(e));
            if (!Name(e).SequenceEqual(newName))
            {
                int start = AppendName(newName);
                _nameStart[e] = start;
                _nameLen[e] = (ushort)newName.Length;
                var fold = FoldName(e);
                _category[e] = Categorizer.FromName(fold, IsDirectory(e));
                _mask[e] = CharMask.Of(fold);
            }
            if (newParent != _parent[e])
            {
                UnlinkChild(_parent[e], e);
                _parent[e] = newParent;
                LinkChild(newParent, e);
                _depth[e] = (byte)Math.Min(255, _depth[newParent] + 1);
                RecomputeSubtreeDepth(e);
            }
            _dirty = true;
        }
        finally { Lock.ExitWriteLock(); }
    }

    /// <summary>Marks an entry (and, for directories, its whole subtree) deleted.</summary>
    public void Delete(int e)
    {
        Lock.EnterWriteLock();
        try
        {
            if (e <= 0 || e >= _count) return;
            MarkDeletedSubtree(e);
            _dirty = true;
        }
        finally { Lock.ExitWriteLock(); }
    }

    /// <summary>Sets size and modified time. Caller must hold at least the read lock.</summary>
    public void SetMetadata(int e, long size, long modifiedTicks)
    {
        _size[e] = size;
        _modified[e] = modifiedTicks;
        _flags[e] |= EntryFlags.MetadataKnown;
        _dirty = true;
    }

    /// <summary>Updates attribute-derived flags (hidden/system/reparse) keeping structural flags.</summary>
    public void SetAttributes(int e, EntryFlags attributeFlags)
    {
        const EntryFlags attrMask = EntryFlags.Hidden | EntryFlags.System | EntryFlags.ReparsePoint;
        _flags[e] = (_flags[e] & ~attrMask) | (attributeFlags & attrMask);
    }

    public void SetRecord(int e, long recordNo)
    {
        Lock.EnterWriteLock();
        try { SetRecordCore(e, recordNo); }
        finally { Lock.ExitWriteLock(); }
    }

    /// <summary>Approximate managed bytes held by this index's arrays.</summary>
    public long ApproximateBytes =>
        (long)_parent.Length * (4 + 4 + 4 + 4 + 2 + 2 + 1 + 1 + 8 + 8 + 8) + (long)_names.Length * 4 +
        (_recordMap?.Length ?? 0) * 4L + (_entryRecord?.Length ?? 0) * 8L;

    /// <summary>Shrinks the arrays to their used size (plus a little slack) after a bulk build.</summary>
    public void TrimExcess()
    {
        Lock.EnterWriteLock();
        try
        {
            int cap = _count + 1024;
            if (cap < _parent.Length)
            {
                Array.Resize(ref _parent, cap);
                Array.Resize(ref _firstChild, cap);
                Array.Resize(ref _nextSibling, cap);
                Array.Resize(ref _nameStart, cap);
                Array.Resize(ref _nameLen, cap);
                Array.Resize(ref _flags, cap);
                Array.Resize(ref _category, cap);
                Array.Resize(ref _depth, cap);
                Array.Resize(ref _mask, cap);
                Array.Resize(ref _size, cap);
                Array.Resize(ref _modified, cap);
                if (_entryRecord is not null) Array.Resize(ref _entryRecord, cap);
            }
            int arenaCap = _arenaLength + 16 * 1024;
            if (arenaCap < _names.Length)
            {
                Array.Resize(ref _names, arenaCap);
                Array.Resize(ref _fold, arenaCap);
            }
        }
        finally { Lock.ExitWriteLock(); }
    }

    // ---------------------------------------------------------------- lookups

    /// <summary>Live child of <paramref name="parent"/> with the given name (case-insensitive), or -1.</summary>
    public int FindChild(int parent, ReadOnlySpan<char> name)
    {
        if ((uint)parent >= (uint)_count) return -1;
        Span<char> fold = name.Length <= 512 ? stackalloc char[name.Length] : new char[name.Length];
        name.ToLowerInvariant(fold);
        Lock.EnterReadLock();
        try
        {
            int guard = 0;
            for (int c = _firstChild[parent]; c > 0 && guard++ < int.MaxValue; c = _nextSibling[c])
            {
                if (_nameLen[c] == fold.Length && (_flags[c] & EntryFlags.Deleted) == 0 && FoldName(c).SequenceEqual(fold))
                    return c;
            }
            return -1;
        }
        finally { Lock.ExitReadLock(); }
    }

    /// <summary>Entry for an absolute path under <see cref="Root"/> (case-insensitive), or -1.</summary>
    public int FindByPath(string fullPath)
    {
        var path = fullPath.AsSpan().TrimEnd('\\');
        if (!path.StartsWith(RootName, StringComparison.OrdinalIgnoreCase)) return -1;
        var rest = path[RootName.Length..];
        if (rest.IsEmpty) return RootEntry;
        if (rest[0] != '\\') return -1;
        rest = rest[1..];
        int current = RootEntry;
        foreach (var range in rest.Split('\\'))
        {
            var part = rest[range];
            if (part.IsEmpty) continue;
            current = FindChild(current, part);
            if (current < 0) return -1;
        }
        return current;
    }

    public int FindByRecord(long recordNo)
    {
        var map = _recordMap;
        if (map is null || recordNo < 0 || recordNo >= map.Length) return -1;
        int e = map[recordNo] - 1; // stored +1 so default 0 means "none"
        return e;
    }

    // ---------------------------------------------------------------- internals (builder / serializer)

    internal int AppendRaw(int parent, ReadOnlySpan<char> name, EntryFlags flags, long size, long modifiedTicks)
    {
        if (name.Length > ushort.MaxValue) name = name[..ushort.MaxValue];
        EnsureCapacity(_count + 1);
        int e = _count;
        int start = AppendName(name);
        _parent[e] = parent;
        _firstChild[e] = 0;
        _nextSibling[e] = 0;
        _nameStart[e] = start;
        _nameLen[e] = (ushort)name.Length;
        _flags[e] = flags;
        var fold = new ReadOnlySpan<char>(_fold, start, name.Length);
        _category[e] = Categorizer.FromName(fold, (flags & EntryFlags.Directory) != 0);
        _mask[e] = CharMask.Of(fold);
        _size[e] = size;
        _modified[e] = modifiedTicks;
        _count++;
        return e;
    }

    /// <summary>Builder finalisation: sets parents, links children, computes depth and live counts.</summary>
    internal void FinalizeStructure(int[] parents)
    {
        for (int e = 1; e < _count; e++)
        {
            _parent[e] = parents[e];
            _firstChild[e] = 0;
            _nextSibling[e] = 0;
        }
        _firstChild[0] = 0;
        // Link in reverse so children enumerate in insertion order.
        for (int e = _count - 1; e >= 1; e--)
        {
            int p = _parent[e];
            if (p < 0) { _parent[e] = RootEntry; p = RootEntry; _flags[e] |= EntryFlags.Deleted; }
            _nextSibling[e] = _firstChild[p];
            _firstChild[p] = e;
        }
        ComputeDepthsAndDeadness();
        _liveCount = 0;
        for (int e = 1; e < _count; e++)
            if ((_flags[e] & EntryFlags.Deleted) == 0) _liveCount++;
    }

    private void ComputeDepthsAndDeadness()
    {
        // state: 0 = unknown, 1 = in progress, 2 = done
        var state = new byte[_count];
        state[0] = 2;
        _depth[0] = 0;
        var stack = new Stack<int>();
        for (int e = 1; e < _count; e++)
        {
            if (state[e] == 2) continue;
            int cur = e;
            while (state[cur] == 0)
            {
                state[cur] = 1;
                stack.Push(cur);
                cur = _parent[cur];
            }
            bool cycle = state[cur] == 1;
            while (stack.Count > 0)
            {
                int x = stack.Pop();
                int p = _parent[x];
                if (cycle) { _flags[x] |= EntryFlags.Deleted; _depth[x] = 1; }
                else
                {
                    _depth[x] = (byte)Math.Min(255, _depth[p] + 1);
                    if ((_flags[p] & EntryFlags.Deleted) != 0) _flags[x] |= EntryFlags.Deleted;
                }
                state[x] = 2;
            }
        }
    }

    internal void SetRecordCore(int e, long recordNo)
    {
        if (recordNo < 0 || recordNo >= int.MaxValue) return;
        if (_recordMap is null || recordNo >= _recordMap.Length)
        {
            int newLen = (int)Math.Min(int.MaxValue - 64, Math.Max(recordNo + 1, (_recordMap?.Length ?? 1024) * 2L));
            Array.Resize(ref _recordMap, newLen);
        }
        if (_entryRecord is null) { _entryRecord = new long[_parent.Length]; Array.Fill(_entryRecord, -1); }
        if (_entryRecord.Length < _parent.Length)
        {
            int old = _entryRecord.Length;
            Array.Resize(ref _entryRecord, _parent.Length);
            _entryRecord.AsSpan(old).Fill(-1);
        }
        long previous = _entryRecord[e];
        if (previous >= 0 && previous < _recordMap.Length && _recordMap[previous] == e + 1) _recordMap[previous] = 0;
        _recordMap[recordNo] = e + 1;
        _entryRecord[e] = recordNo;
    }

    internal void SetLiveCountForLoad(int live) => _liveCount = live;

    // raw array access for the snapshot serializer
    internal int[] ParentArray => _parent;
    internal int[] NameStartArray => _nameStart;
    internal ushort[] NameLenArray => _nameLen;
    internal EntryFlags[] FlagsArray => _flags;
    internal long[] SizeArray => _size;
    internal long[] ModifiedArray => _modified;
    internal char[] NamesArena => _names;
    internal long[]? EntryRecordArray => _entryRecord;

    private void LinkChild(int parent, int child)
    {
        _nextSibling[child] = _firstChild[parent];
        _firstChild[parent] = child;
    }

    private void UnlinkChild(int parent, int child)
    {
        if (parent < 0) return;
        if (_firstChild[parent] == child) { _firstChild[parent] = _nextSibling[child]; _nextSibling[child] = 0; return; }
        for (int c = _firstChild[parent]; c > 0; c = _nextSibling[c])
        {
            if (_nextSibling[c] == child) { _nextSibling[c] = _nextSibling[child]; _nextSibling[child] = 0; return; }
        }
    }

    private void MarkDeletedSubtree(int e)
    {
        var stack = new Stack<int>();
        stack.Push(e);
        while (stack.Count > 0)
        {
            int x = stack.Pop();
            if ((_flags[x] & EntryFlags.Deleted) == 0)
            {
                _flags[x] |= EntryFlags.Deleted;
                _liveCount--;
                if (_entryRecord is { } er && x < er.Length && er[x] >= 0 && _recordMap is { } rm && er[x] < rm.Length && rm[er[x]] == x + 1)
                    rm[er[x]] = 0;
            }
            for (int c = _firstChild[x]; c > 0; c = _nextSibling[c])
                if ((_flags[c] & EntryFlags.Deleted) == 0) stack.Push(c);
        }
    }

    private void RecomputeSubtreeDepth(int e)
    {
        var stack = new Stack<int>();
        stack.Push(e);
        while (stack.Count > 0)
        {
            int x = stack.Pop();
            for (int c = _firstChild[x]; c > 0; c = _nextSibling[c])
            {
                _depth[c] = (byte)Math.Min(255, _depth[x] + 1);
                stack.Push(c);
            }
        }
    }

    private int AppendName(ReadOnlySpan<char> name)
    {
        if (_arenaLength + name.Length > _names.Length)
        {
            long newLen = Math.Max(_names.Length * 2L, _arenaLength + (long)name.Length + 1024);
            newLen = Math.Min(newLen, Array.MaxLength);
            Array.Resize(ref _names, (int)newLen);
            Array.Resize(ref _fold, (int)newLen);
        }
        int start = _arenaLength;
        name.CopyTo(_names.AsSpan(start));
        name.ToLowerInvariant(_fold.AsSpan(start, name.Length));
        _arenaLength += name.Length;
        return start;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _parent.Length) return;
        int newCap = (int)Math.Min(Array.MaxLength, Math.Max(needed, _parent.Length * 2L));
        Array.Resize(ref _parent, newCap);
        Array.Resize(ref _firstChild, newCap);
        Array.Resize(ref _nextSibling, newCap);
        Array.Resize(ref _nameStart, newCap);
        Array.Resize(ref _nameLen, newCap);
        Array.Resize(ref _flags, newCap);
        Array.Resize(ref _category, newCap);
        Array.Resize(ref _depth, newCap);
        Array.Resize(ref _mask, newCap);
        Array.Resize(ref _size, newCap);
        Array.Resize(ref _modified, newCap);
        if (_entryRecord is not null)
        {
            int old = _entryRecord.Length;
            Array.Resize(ref _entryRecord, newCap);
            _entryRecord.AsSpan(old).Fill(-1);
        }
    }
}
