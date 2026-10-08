using System.Buffers.Binary;
using System.Runtime.InteropServices;
using FileHound.Indexing.Interop;

namespace FileHound.Indexing.Ntfs;

/// <summary>A parsed USN_RECORD_V2/V3 view over a buffer. <see cref="Name"/> points into that buffer.</summary>
public readonly ref struct UsnRecord
{
    public UsnRecord(int length, long recordNo, ushort sequence, long parentRecordNo, long usn, long timestampFileTime, UsnReason reason, uint attributes, ReadOnlySpan<char> name)
    {
        Length = length; RecordNo = recordNo; Sequence = sequence; ParentRecordNo = parentRecordNo; Usn = usn; TimestampFileTime = timestampFileTime;
        Reason = reason; Attributes = attributes; Name = name;
    }

    /// <summary>Total record length in bytes (advance by this to reach the next record).</summary>
    public int Length { get; }
    /// <summary>MFT record number (low 48 bits of the file reference number).</summary>
    public long RecordNo { get; }
    /// <summary>The record's sequence number (high 16 bits of the file reference); tells a reused record from the original.</summary>
    public ushort Sequence { get; }
    public long ParentRecordNo { get; }
    public long Usn { get; }
    /// <summary>FILETIME of the change (0 when unknown).</summary>
    public long TimestampFileTime { get; }
    public DateTime? TimestampUtc => TimestampFileTime > 0 && TimestampFileTime < DateTime.MaxValue.ToFileTimeUtc() ? DateTime.FromFileTimeUtc(TimestampFileTime) : null;
    public UsnReason Reason { get; }
    public uint Attributes { get; }
    public ReadOnlySpan<char> Name { get; }
    public bool IsDirectory => (Attributes & 0x10) != 0;
}

public static class UsnRecordParser
{
    private const ulong RecordMask = 0x0000_FFFF_FFFF_FFFF;

    /// <summary>Parses the record at the start of <paramref name="buffer"/>. Returns false on a malformed or unknown record.</summary>
    public static bool TryRead(ReadOnlySpan<byte> buffer, out UsnRecord record)
    {
        record = default;
        if (buffer.Length < 8) return false;
        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        if (length < 60 || length > buffer.Length) return false;
        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(buffer[4..]);
        ulong frn, parent;
        long usn, timestamp;
        uint reason, attrs;
        int nameLen, nameOffset;
        switch (major)
        {
            case 2:
                frn = BinaryPrimitives.ReadUInt64LittleEndian(buffer[8..]);
                parent = BinaryPrimitives.ReadUInt64LittleEndian(buffer[16..]);
                usn = BinaryPrimitives.ReadInt64LittleEndian(buffer[24..]);
                timestamp = BinaryPrimitives.ReadInt64LittleEndian(buffer[32..]);
                reason = BinaryPrimitives.ReadUInt32LittleEndian(buffer[40..]);
                attrs = BinaryPrimitives.ReadUInt32LittleEndian(buffer[52..]);
                nameLen = BinaryPrimitives.ReadUInt16LittleEndian(buffer[56..]);
                nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(buffer[58..]);
                break;
            case 3:
                if (length < 76) return false;
                // FILE_ID_128: for NTFS the meaningful id is in the low 8 bytes; a non-zero high part means ReFS-style ids.
                frn = BinaryPrimitives.ReadUInt64LittleEndian(buffer[8..]);
                if (BinaryPrimitives.ReadUInt64LittleEndian(buffer[16..]) != 0) return false;
                parent = BinaryPrimitives.ReadUInt64LittleEndian(buffer[24..]);
                usn = BinaryPrimitives.ReadInt64LittleEndian(buffer[40..]);
                timestamp = BinaryPrimitives.ReadInt64LittleEndian(buffer[48..]);
                reason = BinaryPrimitives.ReadUInt32LittleEndian(buffer[56..]);
                attrs = BinaryPrimitives.ReadUInt32LittleEndian(buffer[68..]);
                nameLen = BinaryPrimitives.ReadUInt16LittleEndian(buffer[72..]);
                nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(buffer[74..]);
                break;
            default:
                return false;
        }
        if (nameOffset + nameLen > length || (nameLen & 1) != 0) return false;
        var name = MemoryMarshal.Cast<byte, char>(buffer.Slice(nameOffset, nameLen));
        record = new UsnRecord(length, (long)(frn & RecordMask), (ushort)(frn >> 48), (long)(parent & RecordMask), usn, timestamp, (UsnReason)reason, attrs, name);
        return true;
    }
}
