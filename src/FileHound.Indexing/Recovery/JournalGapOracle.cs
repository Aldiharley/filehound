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
