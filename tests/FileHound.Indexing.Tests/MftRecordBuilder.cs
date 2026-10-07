using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace FileHound.Indexing.Tests;

/// <summary>Builds NTFS FILE records (NTFS 3.1 layout) byte-for-byte for parser and reader tests.</summary>
internal sealed class MftRecordBuilder(int size = 1024)
{
    public const ushort Usn = 0x0042;
    private readonly List<byte[]> _attributes = [];
    private ushort _flags;
    private long _baseRecord;

    public static long FileTime(DateTime utc) => utc.ToFileTimeUtc();

    public MftRecordBuilder InUse(bool directory = false)
    {
        _flags = (ushort)(0x1 | (directory ? 0x2 : 0));
        return this;
    }

    public MftRecordBuilder Extension(long baseRecord)
    {
        _baseRecord = baseRecord | (1L << 48); // sequence number in the high 16 bits, like real FRNs
        return this;
    }

    public MftRecordBuilder StandardInfo(DateTime modifiedUtc, uint attributes = 0x20)
    {
        var v = new byte[72];
        BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(0), FileTime(modifiedUtc));
        BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(8), FileTime(modifiedUtc));
        BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(32), attributes);
        return Resident(0x10, v);
    }

    public MftRecordBuilder FileName(long parentRecord, string name, byte nameSpace = 1)
    {
        var v = new byte[66 + name.Length * 2];
        BinaryPrimitives.WriteUInt64LittleEndian(v, (ulong)parentRecord | (3UL << 48));
        v[64] = (byte)name.Length;
        v[65] = nameSpace;
        MemoryMarshal.AsBytes(name.AsSpan()).CopyTo(v.AsSpan(66));
        return Resident(0x30, v);
    }

    public MftRecordBuilder ResidentData(int length, string? streamName = null) => Resident(0x80, new byte[length], streamName);

    public MftRecordBuilder AttributeList() => Resident(0x20, new byte[32]);

    public MftRecordBuilder NonResidentData(long dataSize, byte[] runs, long startVcn = 0, string? streamName = null)
    {
        int nameBytes = (streamName?.Length ?? 0) * 2;
        int runsOffset = Align8(64 + nameBytes);
        int length = Align8(runsOffset + runs.Length);
        var a = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(a, 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(4), (uint)length);
        a[8] = 1;
        a[9] = (byte)(streamName?.Length ?? 0);
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(10), 64);
        if (streamName is not null) MemoryMarshal.AsBytes(streamName.AsSpan()).CopyTo(a.AsSpan(64));
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(16), startVcn);
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(32), (ushort)runsOffset);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(40), (dataSize + 4095) / 4096 * 4096);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(48), dataSize);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(56), dataSize);
        runs.CopyTo(a.AsSpan(runsOffset));
        _attributes.Add(a);
        return this;
    }

    /// <summary>Raw attribute bytes (for malformed-attribute tests).</summary>
    public MftRecordBuilder Raw(byte[] attribute)
    {
        _attributes.Add(attribute);
        return this;
    }

    private MftRecordBuilder Resident(uint type, byte[] value, string? name = null)
    {
        int nameBytes = (name?.Length ?? 0) * 2;
        int valueOffset = Align8(24 + nameBytes);
        int length = Align8(valueOffset + value.Length);
        var a = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(a, type);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(4), (uint)length);
        a[9] = (byte)(name?.Length ?? 0);
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(10), 24);
        if (name is not null) MemoryMarshal.AsBytes(name.AsSpan()).CopyTo(a.AsSpan(24));
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(16), (uint)value.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(20), (ushort)valueOffset);
        value.CopyTo(a.AsSpan(valueOffset));
        _attributes.Add(a);
        return this;
    }

    /// <summary>Returns the record as stored on disk (with update-sequence protection applied).</summary>
    public byte[] Build()
    {
        var r = new byte[size];
        "FILE"u8.CopyTo(r);
        int usaCount = size / 512 + 1;
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(4), 48);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(6), (ushort)usaCount);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(18), 1);
        int first = Align8(48 + 2 * usaCount);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(20), (ushort)first);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(22), _flags);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(28), (uint)size);
        BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(32), (ulong)_baseRecord);
        int pos = first;
        foreach (var a in _attributes)
        {
            a.CopyTo(r.AsSpan(pos));
            pos += a.Length;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(pos), 0xFFFFFFFF);
        pos += 8;
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(24), (uint)pos);

        // Update sequence protection: save each sector's last two bytes into the array, stamp the USN there.
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(48), Usn);
        for (int i = 1; i < usaCount; i++)
        {
            int end = i * 512 - 2;
            r.AsSpan(end, 2).CopyTo(r.AsSpan(48 + 2 * i));
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(end), Usn);
        }
        return r;
    }

    private static int Align8(int n) => (n + 7) & ~7;
}
