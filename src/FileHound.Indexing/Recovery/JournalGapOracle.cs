using System.Runtime.InteropServices;
using FileHound.Indexing.Interop;

namespace FileHound.Indexing.Recovery;

/// <summary>
/// Cheap "is this deleted file's MFT slot still unused?" check that needs no raw disk access.
/// <c>FSCTL_GET_NTFS_FILE_RECORD</c> returns the nearest <em>in-use</em> record at or below the requested number, so a
/// lower record coming back proves the requested slot is still free (the deleted record is intact for undelete).
/// </summary>
public static class JournalGapOracle
{
    private const uint FSCTL_GET_NTFS_FILE_RECORD = 0x00090068;
    private const ulong RecordMask = 0x0000_FFFF_FFFF_FFFF;

    public enum SlotState { Free, Reused, Unknown }

    public static unsafe SlotState Check(char letter, long recordNo)
    {
        using var h = Kernel32.OpenVolume(letter);
        if (h.IsInvalid) return SlotState.Unknown;
        return Check(h, recordNo);
    }

    /// <summary>
    /// The direct answer when raw reads work: the record's own in-use flag. A deleted file's record is not in use until
    /// NTFS hands it to a new file. Records outside the MFT or unreadable are Unknown.
    /// </summary>
    public static SlotState[] CheckMany(VolumeReader reader, IReadOnlyList<long> recordNos)
    {
        var result = new SlotState[recordNos.Count];
        for (int i = 0; i < recordNos.Count; i++)
        {
            long n = recordNos[i];
            if (n < 0 || n >= reader.RecordCount) { result[i] = SlotState.Unknown; continue; }
            try
            {
                var record = reader.ReadRecord(n);
                result[i] = Ntfs.MftRecordParser.TryParse(record, out var r) ? (r.InUse ? SlotState.Reused : SlotState.Free) : SlotState.Unknown;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ObjectDisposedException)
            {
                result[i] = SlotState.Unknown;
            }
        }
        return result;
    }

    /// <summary>Checks many records on one volume handle; every result is Unknown when the volume cannot be opened (not elevated).</summary>
    public static SlotState[] CheckMany(char letter, IReadOnlyList<long> recordNos)
    {
        var result = new SlotState[recordNos.Count];
        using var h = Kernel32.OpenVolume(letter);
        if (h.IsInvalid) { Array.Fill(result, SlotState.Unknown); return result; }
        for (int i = 0; i < recordNos.Count; i++) result[i] = Check(h, recordNos[i]);
        return result;
    }

    /// <summary>Same check on an already-open volume handle (one handle for many records).</summary>
    public static unsafe SlotState Check(Microsoft.Win32.SafeHandles.SafeFileHandle volume, long recordNo)
    {
        if (recordNo < 0) return SlotState.Unknown;
        long input = recordNo;
        byte* output = stackalloc byte[12 + 4096];
        if (!Kernel32.DeviceIoControl(volume, FSCTL_GET_NTFS_FILE_RECORD, &input, sizeof(long), output, 12 + 4096, out int returned, 0) || returned < 12)
            return SlotState.Unknown;
        long got = (long)(*(ulong*)output & RecordMask);
        return got == recordNo ? SlotState.Reused : got < recordNo ? SlotState.Free : SlotState.Unknown;
    }
}
