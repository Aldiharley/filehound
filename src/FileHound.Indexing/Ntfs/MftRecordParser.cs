using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace FileHound.Indexing.Ntfs;

/// <summary>The parts of one NTFS FILE record that FileHound indexes. Spans point into the record buffer.</summary>
public ref struct MftRecord
{
    public bool InUse;
    public bool IsDirectory;
    public bool HasAttributeList;
    public bool HasStandardInfo;
    public bool HasName;
    public bool HasSize;
    /// <summary>Record number of the base record (0 when this is a base record).</summary>
    public long BaseRecord;
    public long ParentRecord;
    public long Size;
    public long ModifiedUtcTicks;
    public uint Attributes;
    public ReadOnlySpan<char> Name;
    /// <summary>Mapping pairs of the unnamed $DATA when it is non-resident and starts at VCN 0.</summary>
    public ReadOnlySpan<byte> UnnamedDataRuns;
}

/// <summary>Parses raw NTFS FILE records (as read from the $MFT) without allocating.</summary>
public static class MftRecordParser
{
    private const int SectorStride = 512; // update sequence stride is always 512 bytes
    private const ulong RecordMask = 0x0000_FFFF_FFFF_FFFF;
    private const long FileTimeToTicks = 504911232000000000; // 1601-01-01 in DateTime ticks

    /// <summary>
    /// Verifies the FILE signature and undoes the update-sequence protection in place.
    /// Returns false for a bad signature or a torn (partially written) record.
    /// </summary>
    public static bool ApplyFixups(Span<byte> record)
    {
        if (record.Length < 48 || !record[..4].SequenceEqual("FILE"u8)) return false;
        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
        if (usaCount < 2 || usaOffset + 2 * usaCount > record.Length || (usaCount - 1) * SectorStride > record.Length) return false;
        ushort usn = BinaryPrimitives.ReadUInt16LittleEndian(record[usaOffset..]);
        for (int i = 1; i < usaCount; i++)
        {
            var tail = record.Slice(i * SectorStride - 2, 2);
            if (BinaryPrimitives.ReadUInt16LittleEndian(tail) != usn) return false;
            record.Slice(usaOffset + 2 * i, 2).CopyTo(tail);
        }
        return true;
    }

    /// <summary>Parses a record whose fixups were already applied. Returns false when the record is malformed.</summary>
    public static bool TryParse(ReadOnlySpan<byte> record, out MftRecord r)
    {
        r = default;
        if (record.Length < 48 || !record[..4].SequenceEqual("FILE"u8)) return false;
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(record[22..]);
        r.InUse = (flags & 0x1) != 0;
        r.IsDirectory = (flags & 0x2) != 0;
        r.BaseRecord = (long)(BinaryPrimitives.ReadUInt64LittleEndian(record[32..]) & RecordMask);
        int offset = BinaryPrimitives.ReadUInt16LittleEndian(record[20..]);
        int limit = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(record[24..]), (uint)record.Length);

        while (offset + 8 <= limit)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
            if (type == 0xFFFFFFFF) return true;
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[(offset + 4)..]);
            if (length < 16 || offset + length > limit) return false;
            var attr = record.Slice(offset, length);
            bool nonResident = attr[8] != 0;
            int nameLength = attr[9];

            switch (type)
            {
                case 0x10 when !nonResident: // $STANDARD_INFORMATION
                {
                    if (!TryResidentValue(attr, out var v) || v.Length < 36) break;
                    long ft = BinaryPrimitives.ReadInt64LittleEndian(v[8..]);
                    r.ModifiedUtcTicks = ft > 0 && ft < DateTime.MaxValue.Ticks - FileTimeToTicks ? ft + FileTimeToTicks : 0;
                    r.Attributes = BinaryPrimitives.ReadUInt32LittleEndian(v[32..]);
                    r.HasStandardInfo = true;
                    break;
                }
                case 0x20: // $ATTRIBUTE_LIST
                    r.HasAttributeList = true;
                    break;
                case 0x30 when !nonResident: // $FILE_NAME
                {
                    if (r.HasName || !TryResidentValue(attr, out var v) || v.Length < 66) break;
                    int chars = v[64];
                    byte nameSpace = v[65];
                    if (nameSpace == 2 || 66 + chars * 2 > v.Length) break; // DOS 8.3 alias: skip
                    r.ParentRecord = (long)(BinaryPrimitives.ReadUInt64LittleEndian(v) & RecordMask);
                    r.Name = MemoryMarshal.Cast<byte, char>(v.Slice(66, chars * 2));
                    r.HasName = true;
                    break;
                }
                case 0x80 when nameLength == 0: // unnamed $DATA
                {
                    if (!nonResident)
                    {
                        if (TryResidentValue(attr, out var v)) { r.Size = v.Length; r.HasSize = true; }
                    }
                    else if (length >= 64 && BinaryPrimitives.ReadInt64LittleEndian(attr[16..]) == 0)
                    {
                        r.Size = BinaryPrimitives.ReadInt64LittleEndian(attr[48..]);
                        r.HasSize = r.Size >= 0;
                        int runsOffset = BinaryPrimitives.ReadUInt16LittleEndian(attr[32..]);
                        if (runsOffset >= 64 && runsOffset < length) r.UnnamedDataRuns = attr[runsOffset..];
                    }
                    break;
                }
            }
            offset += length;
        }
        return true; // no end marker within bytes-in-use: accept what we parsed
    }

    private static bool TryResidentValue(ReadOnlySpan<byte> attr, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (attr.Length < 24) return false;
        int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(attr[16..]);
        int off = BinaryPrimitives.ReadUInt16LittleEndian(attr[20..]);
        if (off < 24 || len < 0 || off + len > attr.Length) return false;
        value = attr.Slice(off, len);
        return true;
    }
}
