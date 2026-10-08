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

    // ---- fields below are read for recovery (deleted records); the indexer ignores them.
    /// <summary>Record sequence number (header @0x10), incremented each time the record is reused.</summary>
    public ushort Sequence;
    /// <summary>Sequence number the $FILE_NAME parent reference expects (high 16 bits).</summary>
    public ushort ParentSequence;
    /// <summary>$STANDARD_INFORMATION creation time, or $FILE_NAME's when the former is missing.</summary>
    public long CreatedUtcTicks;
    public long NameModifiedUtcTicks;
    public long MftChangedUtcTicks;
    public long AccessedUtcTicks;
    /// <summary>$FILE_NAME real size: what the file system last recorded for the name (0 for directories).</summary>
    public long RealSize;
    /// <summary>Unnamed $DATA attribute flags: <see cref="DataCompressed"/>, <see cref="DataEncrypted"/>, <see cref="DataSparse"/>.</summary>
    public ushort DataFlags;
    /// <summary>log2 of clusters per compression unit (4 → 16 clusters); 0 when not compressed.</summary>
    public byte CompressionUnit;
    /// <summary>Non-resident $DATA: bytes actually written (data beyond this up to the size reads as zeros).</summary>
    public long InitializedSize;
    public long AllocatedSize;
    public bool DataIsResident;
    /// <summary>The unnamed $DATA bytes when resident.</summary>
    public ReadOnlySpan<byte> ResidentData;

    public readonly bool IsBaseRecord => BaseRecord == 0;
    public readonly bool IsCompressed => (DataFlags & DataCompressed) != 0;
    public readonly bool IsEncrypted => (DataFlags & DataEncrypted) != 0;
    public readonly bool IsSparse => (DataFlags & DataSparse) != 0;

    public const ushort DataCompressed = 0x0001, DataEncrypted = 0x4000, DataSparse = 0x8000;
}

/// <summary>Parses raw NTFS FILE records (as read from the $MFT) without allocating.</summary>
public static class MftRecordParser
{
    private const int SectorStride = 512; // update sequence stride is always 512 bytes
    private const ulong RecordMask = 0x0000_FFFF_FFFF_FFFF;
    private const long FileTimeToTicks = 504911232000000000; // 1601-01-01 in DateTime ticks

    /// <summary>
    /// Verifies the FILE signature and undoes the update-sequence protection in place.
    /// For records read from disk, returns false for a bad signature or a torn (partially written) record.
    /// </summary>
    /// <param name="acceptAlreadyApplied">
    /// The record came from NTFS's in-memory copy (FSCTL_GET_NTFS_FILE_RECORD), which is already fixed up. Its update
    /// sequence array may be stale — a record modified in memory but not yet flushed keeps the saved values from its
    /// last disk write — so it cannot be used for validation. Such records are accepted as returned (only a record that
    /// still carries the USN in every sector tail is un-protected). Torn-write detection only applies to disk reads.
    /// </param>
    public static bool ApplyFixups(Span<byte> record, bool acceptAlreadyApplied = false)
    {
        if (record.Length < 48 || !record[..4].SequenceEqual("FILE"u8)) return false;
        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
        if (usaCount < 2 || usaOffset + 2 * usaCount > record.Length || (usaCount - 1) * SectorStride > record.Length) return false;
        ushort usn = BinaryPrimitives.ReadUInt16LittleEndian(record[usaOffset..]);
        bool allProtected = true;
        for (int i = 1; i < usaCount && allProtected; i++)
            allProtected = BinaryPrimitives.ReadUInt16LittleEndian(record[(i * SectorStride - 2)..]) == usn;
        if (allProtected)
        {
            for (int i = 1; i < usaCount; i++)
                record.Slice(usaOffset + 2 * i, 2).CopyTo(record.Slice(i * SectorStride - 2, 2));
            return true;
        }
        return acceptAlreadyApplied;
    }

    /// <summary>Parses a record whose fixups were already applied. Returns false when the record is malformed.</summary>
    public static bool TryParse(ReadOnlySpan<byte> record, out MftRecord r)
    {
        r = default;
        if (record.Length < 48 || !record[..4].SequenceEqual("FILE"u8)) return false;
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(record[22..]);
        r.InUse = (flags & 0x1) != 0;
        r.IsDirectory = (flags & 0x2) != 0;
        r.Sequence = BinaryPrimitives.ReadUInt16LittleEndian(record[16..]);
        r.BaseRecord = (long)(BinaryPrimitives.ReadUInt64LittleEndian(record[32..]) & RecordMask);
        int offset = BinaryPrimitives.ReadUInt16LittleEndian(record[20..]);
        int limit = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(record[24..]), (uint)record.Length);

        while (offset + 8 <= limit)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
            if (type == 0xFFFFFFFF) return true;
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[(offset + 4)..]);
            if (length < 16 || length > limit - offset) return false; // subtract, don't add: a corrupt length can overflow
            var attr = record.Slice(offset, length);
            bool nonResident = attr[8] != 0;
            int nameLength = attr[9];

            switch (type)
            {
                case 0x10 when !nonResident: // $STANDARD_INFORMATION
                {
                    if (!TryResidentValue(attr, out var v) || v.Length < 36) break;
                    r.CreatedUtcTicks = Ticks(BinaryPrimitives.ReadInt64LittleEndian(v));
                    r.ModifiedUtcTicks = Ticks(BinaryPrimitives.ReadInt64LittleEndian(v[8..]));
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
                    ulong parentRef = BinaryPrimitives.ReadUInt64LittleEndian(v);
                    r.ParentRecord = (long)(parentRef & RecordMask);
                    r.ParentSequence = (ushort)(parentRef >> 48);
                    if (r.CreatedUtcTicks == 0) r.CreatedUtcTicks = Ticks(BinaryPrimitives.ReadInt64LittleEndian(v[8..]));
                    r.NameModifiedUtcTicks = Ticks(BinaryPrimitives.ReadInt64LittleEndian(v[16..]));
                    r.MftChangedUtcTicks = Ticks(BinaryPrimitives.ReadInt64LittleEndian(v[24..]));
                    r.AccessedUtcTicks = Ticks(BinaryPrimitives.ReadInt64LittleEndian(v[32..]));
                    r.RealSize = Math.Max(0, BinaryPrimitives.ReadInt64LittleEndian(v[48..]));
                    r.Name = MemoryMarshal.Cast<byte, char>(v.Slice(66, chars * 2));
                    r.HasName = true;
                    break;
                }
                case 0x80 when nameLength == 0: // unnamed $DATA
                {
                    r.DataFlags = BinaryPrimitives.ReadUInt16LittleEndian(attr[12..]);
                    if (!nonResident)
                    {
                        if (TryResidentValue(attr, out var v)) { r.Size = v.Length; r.HasSize = true; r.DataIsResident = true; r.ResidentData = v; }
                    }
                    else if (length >= 64 && BinaryPrimitives.ReadInt64LittleEndian(attr[16..]) == 0)
                    {
                        r.Size = BinaryPrimitives.ReadInt64LittleEndian(attr[48..]);
                        r.HasSize = r.Size >= 0;
                        r.CompressionUnit = attr[34];
                        r.AllocatedSize = BinaryPrimitives.ReadInt64LittleEndian(attr[40..]);
                        r.InitializedSize = BinaryPrimitives.ReadInt64LittleEndian(attr[56..]);
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

    /// <summary>FILETIME → DateTime ticks; 0 for unset or out-of-range values.</summary>
    private static long Ticks(long fileTime) =>
        fileTime > 0 && fileTime < DateTime.MaxValue.Ticks - FileTimeToTicks ? fileTime + FileTimeToTicks : 0;

    private static bool TryResidentValue(ReadOnlySpan<byte> attr, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (attr.Length < 24) return false;
        int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(attr[16..]);
        int off = BinaryPrimitives.ReadUInt16LittleEndian(attr[20..]);
        if (off < 24 || len < 0 || len > attr.Length - off) return false; // subtract, don't add: a corrupt length can overflow
        value = attr.Slice(off, len);
        return true;
    }
}
