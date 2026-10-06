using System.Buffers.Binary;
using System.Runtime.InteropServices;
using FileHound.Indexing.Interop;
using FileHound.Indexing.Ntfs;

namespace FileHound.Indexing.Tests;

public class UsnRecordParserTests
{
    /// <summary>Builds a USN_RECORD_V2 the way NTFS lays it out.</summary>
    internal static byte[] V2(ulong frn, ulong parent, string name, UsnReason reason, uint attrs = 0, long usn = 1000)
    {
        int nameBytes = name.Length * 2;
        int len = (60 + nameBytes + 7) & ~7;
        var b = new byte[len];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)len);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), frn);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), parent);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(24), usn);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(40), (uint)reason);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(52), attrs);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(56), (ushort)nameBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(58), 60);
        MemoryMarshal.AsBytes(name.AsSpan()).CopyTo(b.AsSpan(60));
        return b;
    }

    private static byte[] V3(ulong frn, ulong parent, string name, UsnReason reason, uint attrs = 0)
    {
        int nameBytes = name.Length * 2;
        int len = (76 + nameBytes + 7) & ~7;
        var b = new byte[len];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)len);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), frn);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(24), parent);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(40), 77);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(56), (uint)reason);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(68), attrs);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(72), (ushort)nameBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(74), 76);
        MemoryMarshal.AsBytes(name.AsSpan()).CopyTo(b.AsSpan(76));
        return b;
    }

    [Fact]
    public void Parses_v2_record()
    {
        var buf = V2(0x0003000000000123, 0x0005000000000005, "hello.txt", UsnReason.FileCreate, 0x10);
        Assert.True(UsnRecordParser.TryRead(buf, out var r));
        Assert.Equal(0x123, r.RecordNo);
        Assert.Equal(5, r.ParentRecordNo);
        Assert.Equal("hello.txt", r.Name.ToString());
        Assert.Equal(UsnReason.FileCreate, r.Reason);
        Assert.True(r.IsDirectory);
        Assert.Equal(1000, r.Usn);
        Assert.Equal(buf.Length, r.Length);
    }

    [Fact]
    public void Parses_v3_record()
    {
        var buf = V3(0x0002000000000456, 0x0005000000000005, "é.doc", UsnReason.FileDelete | UsnReason.Close);
        Assert.True(UsnRecordParser.TryRead(buf, out var r));
        Assert.Equal(0x456, r.RecordNo);
        Assert.Equal("é.doc", r.Name.ToString());
        Assert.True(r.Reason.HasFlag(UsnReason.FileDelete));
        Assert.Equal(77, r.Usn);
    }

    [Fact]
    public void Rejects_truncated_and_unknown_versions()
    {
        var buf = V2(1, 5, "x", UsnReason.FileCreate);
        Assert.False(UsnRecordParser.TryRead(buf.AsSpan(0, 30), out _));
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), 9);
        Assert.False(UsnRecordParser.TryRead(buf, out _));
    }
}

public class DriveDiscoveryTests
{
    [Fact]
    public void Finds_system_drive()
    {
        var drives = DriveDiscovery.GetDrives();
        Assert.Contains(drives, d => d.Letter == 'C' && d.Root == @"C:\" && d.Serial != 0);
    }
}
