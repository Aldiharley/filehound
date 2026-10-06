using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using FileHound.Core.Index;

namespace FileHound.Core.Persistence;

/// <summary>
/// Binary snapshot of a <see cref="VolumeIndex"/> for near-instant startup.
/// Layout: header, raw little-endian arrays, XxHash64 trailer. Deleted entries are compacted away on save;
/// derived data (fold names, categories, masks, depth, child links) is rebuilt on load.
/// </summary>
public static class SnapshotSerializer
{
    private static ReadOnlySpan<byte> Magic => "FHX1"u8;
    private const ushort Version = 1;

    public static string FileNameFor(char letter, uint serial) => $"{char.ToUpperInvariant(letter)}_{serial:X8}.fhx";

    public static void Save(VolumeIndex v, string path)
    {
        // 1) Copy a compacted image under the read lock (fast), 2) write outside the lock.
        int[] parents; ushort[] lens; ushort[] flags; long[] sizes; long[] mods; long[]? records; char[] names; int n;
        IndexMode mode; uint serial; ulong journal; long nextUsn;
        v.Lock.EnterReadLock();
        try
        {
            mode = v.Mode; serial = v.VolumeSerial; journal = v.UsnJournalId; nextUsn = v.NextUsn;
            int count = v.Count;
            var newId = new int[count];
            n = 0;
            int arena = 0;
            for (int e = 0; e < count; e++)
            {
                if (e == 0 || v.IsLive(e)) { newId[e] = n++; arena += v.Name(e).Length; }
                else newId[e] = -1;
            }
            parents = new int[n]; lens = new ushort[n]; flags = new ushort[n]; sizes = new long[n]; mods = new long[n];
            records = v.HasRecords ? new long[n] : null;
            names = new char[arena];
            int pos = 0;
            for (int e = 0; e < count; e++)
            {
                int id = newId[e];
                if (id < 0) continue;
                parents[id] = e == 0 ? -1 : newId[v.Parent(e)];
                var name = v.Name(e);
                name.CopyTo(names.AsSpan(pos));
                pos += name.Length;
                lens[id] = (ushort)name.Length;
                flags[id] = (ushort)v.Flags(e);
                sizes[id] = v.Size(e);
                mods[id] = v.ModifiedTicks(e);
                if (records is not null) records[id] = v.RecordOf(e);
            }
        }
        finally { v.Lock.ExitReadLock(); }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            var hash = new XxHash64();
            void Write(ReadOnlySpan<byte> bytes) { fs.Write(bytes); hash.Append(bytes); }

            var header = new MemoryStream();
            using (var bw = new BinaryWriter(header, Encoding.Unicode, leaveOpen: true))
            {
                bw.Write(Magic);
                bw.Write(Version);
                bw.Write((byte)mode);
                bw.Write((byte)(records is not null ? 1 : 0));
                bw.Write((ushort)v.Root.Length);
                bw.Write(MemoryMarshal.AsBytes(v.Root.AsSpan()));
                bw.Write(serial);
                bw.Write(journal);
                bw.Write(nextUsn);
                bw.Write(DateTime.UtcNow.Ticks);
                bw.Write(n);
                bw.Write(names.Length);
            }
            Write(header.ToArray());
            Write(MemoryMarshal.AsBytes(parents.AsSpan()));
            Write(MemoryMarshal.AsBytes(lens.AsSpan()));
            Write(MemoryMarshal.AsBytes(flags.AsSpan()));
            Write(MemoryMarshal.AsBytes(sizes.AsSpan()));
            Write(MemoryMarshal.AsBytes(mods.AsSpan()));
            if (records is not null) Write(MemoryMarshal.AsBytes(records.AsSpan()));
            Write(MemoryMarshal.AsBytes(names.AsSpan()));
            Span<byte> trailer = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(trailer, hash.GetCurrentHashAsUInt64());
            fs.Write(trailer);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Loads a snapshot, or returns null (deleting the file) when it is missing, corrupt or incompatible.</summary>
    public static VolumeIndex? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            var v = Parse(bytes);
            if (v is null) TryDelete(path);
            return v;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException or ArgumentException or InvalidDataException or OutOfMemoryException)
        {
            TryDelete(path);
            return null;
        }
    }

    private static VolumeIndex? Parse(byte[] bytes)
    {
        if (bytes.Length < 16 || !bytes.AsSpan(0, 4).SequenceEqual(Magic)) return null;
        var body = bytes.AsSpan(0, bytes.Length - 8);
        ulong expected = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(bytes.Length - 8));
        if (XxHash64.HashToUInt64(body) != expected) return null;

        int p = 4;
        ushort version = Read<ushort>(body, ref p);
        if (version != Version) return null;
        var mode = (IndexMode)Read<byte>(body, ref p);
        bool hasRecords = Read<byte>(body, ref p) == 1;
        int rootLen = Read<ushort>(body, ref p);
        string root = new(MemoryMarshal.Cast<byte, char>(body.Slice(p, rootLen * 2)));
        p += rootLen * 2;
        uint serial = Read<uint>(body, ref p);
        ulong journal = Read<ulong>(body, ref p);
        long nextUsn = Read<long>(body, ref p);
        _ = Read<long>(body, ref p); // saved time
        int n = Read<int>(body, ref p);
        int arena = Read<int>(body, ref p);
        if (n < 1 || arena < 0) return null;

        var parents = Slice<int>(body, ref p, n);
        var lens = Slice<ushort>(body, ref p, n);
        var flags = Slice<ushort>(body, ref p, n);
        var sizes = Slice<long>(body, ref p, n);
        var mods = Slice<long>(body, ref p, n);
        var records = hasRecords ? Slice<long>(body, ref p, n) : default;
        var names = Slice<char>(body, ref p, arena);
        if (p != body.Length) return null;

        var v = new VolumeIndex(root, mode, n + 1024) { VolumeSerial = serial, UsnJournalId = journal, NextUsn = nextUsn };
        var parentArr = new int[n];
        parentArr[0] = -1;
        int pos = lens[0];
        for (int e = 1; e < n; e++)
        {
            int len = lens[e];
            if (pos + len > names.Length) return null;
            int parent = parents[e];
            if (parent < 0 || parent >= n || parent == e) return null;
            v.AppendRaw(-1, names.Slice(pos, len), (EntryFlags)flags[e], sizes[e], mods[e]);
            parentArr[e] = parent;
            pos += len;
        }
        v.FinalizeStructure(parentArr);
        if (hasRecords)
            for (int e = 0; e < n; e++)
                if (records[e] >= 0) v.SetRecordCore(e, records[e]);
        v.IsDirty = false;
        return v;
    }

    private static T Read<T>(ReadOnlySpan<byte> s, ref int p) where T : unmanaged
    {
        int size = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        if (p + size > s.Length) throw new InvalidDataException("Truncated snapshot header.");
        var value = MemoryMarshal.Read<T>(s.Slice(p, size));
        p += size;
        return value;
    }

    private static ReadOnlySpan<T> Slice<T>(ReadOnlySpan<byte> s, ref int p, int count) where T : unmanaged
    {
        long bytes = (long)count * System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        if (p + bytes > s.Length) throw new InvalidDataException("Truncated snapshot body.");
        var result = MemoryMarshal.Cast<byte, T>(s.Slice(p, (int)bytes));
        p += (int)bytes;
        return result;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
