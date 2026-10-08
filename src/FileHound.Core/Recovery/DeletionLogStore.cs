using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace FileHound.Core.Recovery;

public enum DeletionKind : byte
{
    /// <summary>Removed from the volume (Shift+Delete, program delete, emptied bin).</summary>
    Deleted,
    /// <summary>Moved into the Recycle Bin; restorable from there.</summary>
    Recycled,
    /// <summary>A new file with the same name and folder appeared right after (save-by-replace); hidden by default.</summary>
    Replaced,
}

/// <summary>One deletion seen in the USN journal, captured while the parent path was still resolvable.</summary>
public sealed record DeletionEntry(
    long RecordNo,
    ushort Sequence,
    long ParentRecordNo,
    string Name,
    string ParentPath,
    long Size,
    bool IsDirectory,
    long ModifiedUtcTicks,
    long DeletedUtcTicks,
    DeletionKind Kind,
    long Usn);

/// <summary>
/// Bounded, persisted history of deletions for one volume: a ring buffer saved as
/// <c>FHDL</c> magic, u16 version, i32 count, entries, XxHash64 trailer (atomic tmp+move, like snapshots).
/// </summary>
public sealed class DeletionLogStore
{
    private static ReadOnlySpan<byte> Magic => "FHDL"u8;
    private const ushort Version = 1;

    private readonly DeletionEntry[] _ring;
    private int _start; // index of the oldest entry
    private int _count;
    private readonly HashSet<(long RecordNo, ushort Sequence, long Usn)> _keys = [];
    private readonly object _gate = new();

    public DeletionLogStore(int capacity = 50_000)
    {
        _ring = new DeletionEntry[Math.Max(1, capacity)];
    }

    public int Count { get { lock (_gate) return _count; } }
    public long LastUsn { get; private set; }
    /// <summary>The lowest USN ever added (evicted entries included), so a history replay stops where the log begins.</summary>
    public long FirstUsn { get; private set; }
    public bool IsDirty { get; private set; }

    /// <summary>Adds an entry; returns false for a duplicate (same record, sequence and USN), which happens when a journal replay overlaps live capture.</summary>
    public bool Add(DeletionEntry e)
    {
        lock (_gate)
        {
            if (!_keys.Add((e.RecordNo, e.Sequence, e.Usn))) return false;
            if (_count == _ring.Length)
            {
                var old = _ring[_start];
                _keys.Remove((old.RecordNo, old.Sequence, old.Usn));
                _ring[_start] = e;
                _start = (_start + 1) % _ring.Length;
            }
            else
            {
                _ring[(_start + _count) % _ring.Length] = e;
                _count++;
            }
            if (e.Usn > LastUsn) LastUsn = e.Usn;
            if (FirstUsn == 0 || e.Usn < FirstUsn) FirstUsn = e.Usn;
            IsDirty = true;
            return true;
        }
    }

    /// <summary>Retags the most recent entry for the given record and sequence number.</summary>
    public bool TryMark(long recordNo, ushort sequence, DeletionKind kind)
    {
        lock (_gate)
        {
            for (int i = _count - 1; i >= 0; i--)
            {
                int idx = (_start + i) % _ring.Length;
                var e = _ring[idx];
                if (e.RecordNo != recordNo || e.Sequence != sequence) continue;
                if (e.Kind != kind) { _ring[idx] = e with { Kind = kind }; IsDirty = true; }
                return true;
            }
            return false;
        }
    }

    /// <summary>Entries newest first.</summary>
    public IReadOnlyList<DeletionEntry> Snapshot()
    {
        lock (_gate)
        {
            var list = new DeletionEntry[_count];
            for (int i = 0; i < _count; i++) list[_count - 1 - i] = _ring[(_start + i) % _ring.Length];
            return list;
        }
    }

    public void Save(string path)
    {
        DeletionEntry[] entries;
        lock (_gate)
        {
            entries = new DeletionEntry[_count];
            for (int i = 0; i < _count; i++) entries[i] = _ring[(_start + i) % _ring.Length];
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.Unicode, leaveOpen: true))
        {
            bw.Write(Magic);
            bw.Write(Version);
            bw.Write(entries.Length);
            foreach (var e in entries)
            {
                bw.Write(e.RecordNo); bw.Write(e.Sequence); bw.Write(e.ParentRecordNo);
                bw.Write(e.Name); bw.Write(e.ParentPath);
                bw.Write(e.Size); bw.Write(e.IsDirectory); bw.Write(e.ModifiedUtcTicks); bw.Write(e.DeletedUtcTicks);
                bw.Write((byte)e.Kind); bw.Write(e.Usn);
            }
        }
        var body = ms.ToArray();
        Span<byte> trailer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(trailer, XxHash64.HashToUInt64(body));
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(body);
            fs.Write(trailer);
        }
        File.Move(tmp, path, overwrite: true);
        // Only after the write succeeded; a failed save keeps the store dirty for the next attempt.
        lock (_gate) IsDirty = false;
    }

    /// <summary>Loads a store; returns an empty one when the file is missing or corrupt.</summary>
    public static DeletionLogStore Load(string path, int capacity = 50_000)
    {
        var store = new DeletionLogStore(capacity);
        if (!File.Exists(path)) return store;
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 18 || !bytes.AsSpan(0, 4).SequenceEqual(Magic)) return store;
            var body = bytes.AsSpan(0, bytes.Length - 8);
            if (XxHash64.HashToUInt64(body) != BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(bytes.Length - 8))) return store;
            using var br = new BinaryReader(new MemoryStream(bytes, 4, body.Length - 4), Encoding.Unicode);
            if (br.ReadUInt16() != Version) return store;
            int n = br.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                store.Add(new DeletionEntry(
                    br.ReadInt64(), br.ReadUInt16(), br.ReadInt64(), br.ReadString(), br.ReadString(),
                    br.ReadInt64(), br.ReadBoolean(), br.ReadInt64(), br.ReadInt64(), (DeletionKind)br.ReadByte(), br.ReadInt64()));
            }
            store.IsDirty = false;
            return store;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or ArgumentException)
        {
            return new DeletionLogStore(capacity);
        }
    }
}
