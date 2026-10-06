using System.ComponentModel;
using System.Runtime.InteropServices;
using FileHound.Indexing.Interop;

namespace FileHound.Indexing.Ntfs;

/// <summary>
/// Turbo indexer: enumerates every file record of an NTFS volume via <c>FSCTL_ENUM_USN_DATA</c>
/// (the technique used by Everything). Requires an elevated process. Produces names, parents and attributes;
/// sizes and times are filled afterwards by <see cref="MetadataFiller"/>.
/// </summary>
public sealed class MftScanner
{
    private const long RootRecord = 5;
    private const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;
    // NTFS metafiles live at the root; real folders like "$WINDOWS.~BT" or "$SysReset" are indexed normally.
    private static readonly string[] s_skipRootNames =
    [
        "$MFT", "$MFTMirr", "$LogFile", "$Volume", "$AttrDef", "$Bitmap", "$Boot", "$BadClus", "$Secure", "$UpCase", "$Extend",
        "$Recycle.Bin", "System Volume Information",
    ];

    public static unsafe bool TryQueryJournal(char letter, out ulong journalId, out long firstUsn, out long nextUsn)
    {
        journalId = 0; firstUsn = 0; nextUsn = 0;
        using var h = Kernel32.OpenVolume(letter);
        if (h.IsInvalid) return false;
        UsnJournalDataV1 jd;
        if (!Kernel32.DeviceIoControl(h, Kernel32.FSCTL_QUERY_USN_JOURNAL, null, 0, &jd, sizeof(UsnJournalDataV1), out _, 0)) return false;
        journalId = jd.UsnJournalID; firstUsn = jd.FirstUsn; nextUsn = jd.NextUsn;
        return true;
    }

    public unsafe VolumeIndex Scan(DriveDescriptor drive, IReadOnlyCollection<string> excluded, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        using var h = Kernel32.OpenVolume(drive.Letter);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot open volume {drive.Letter}:");

        UsnJournalDataV1 jd;
        if (!Kernel32.DeviceIoControl(h, Kernel32.FSCTL_QUERY_USN_JOURNAL, null, 0, &jd, sizeof(UsnJournalDataV1), out _, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"No USN journal on {drive.Letter}:");

        long totalRecords = EstimateRecordCount(h);
        var builder = new VolumeIndexBuilder(drive.Root, IndexMode.Turbo, (int)Math.Clamp(totalRecords, 1 << 16, 1 << 26));
        // HighUsn = max so records changed during the scan are still enumerated; the USN updater then replays
        // from the NextUsn captured above (its upserts are idempotent).
        var med = new MftEnumDataV1 { StartFileReferenceNumber = 0, LowUsn = 0, HighUsn = long.MaxValue, MinMajorVersion = 2, MaxMajorVersion = 3 };
        const int BufferSize = 1 << 20;
        byte* buffer = (byte*)NativeMemory.Alloc(BufferSize);
        try
        {
            long lastReport = Environment.TickCount64;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!Kernel32.DeviceIoControl(h, Kernel32.FSCTL_ENUM_USN_DATA, &med, sizeof(MftEnumDataV1), buffer, BufferSize, out int returned, 0))
                {
                    int err = Marshal.GetLastPInvokeError();
                    if (err == Kernel32.ERROR_HANDLE_EOF) break;
                    throw new Win32Exception(err, $"MFT enumeration failed on {drive.Letter}:");
                }
                if (returned <= 8) break;
                med.StartFileReferenceNumber = *(ulong*)buffer;
                var span = new ReadOnlySpan<byte>(buffer + 8, returned - 8);
                while (UsnRecordParser.TryRead(span, out var rec))
                {
                    span = span[rec.Length..];
                    // Root-level NTFS metafiles, $Recycle.Bin and System Volume Information are skipped; their
                    // children become orphans and are dropped (and unmapped) by the builder.
                    if (rec.ParentRecordNo == RootRecord && IsSkippedRootName(rec.Name)) continue;
                    builder.AddRecord(rec.RecordNo, rec.ParentRecordNo, rec.Name, EntryFlagsExtensions.FromAttributes(rec.Attributes));
                }
                if (progress is not null && Environment.TickCount64 - lastReport > 200)
                {
                    lastReport = Environment.TickCount64;
                    long pos = (long)(med.StartFileReferenceNumber & 0x0000_FFFF_FFFF_FFFF);
                    progress.Report(new ScanProgress(builder.Count, 0, 0, totalRecords > 0 ? Math.Min(0.99, (double)pos / totalRecords) : 0));
                }
            }
        }
        finally { NativeMemory.Free(buffer); }

        var v = builder.Build(RootRecord);
        v.UsnJournalId = jd.UsnJournalID;
        v.NextUsn = jd.NextUsn;
        v.VolumeSerial = drive.Serial;
        foreach (var path in excluded)
        {
            if (!path.StartsWith(drive.Root, StringComparison.OrdinalIgnoreCase)) continue;
            int e = v.FindByPath(path);
            if (e > 0) v.Delete(e);
        }
        progress?.Report(new ScanProgress(v.LiveCount, 0, 0, 1.0));
        return v;
    }

    /// <summary>True for root-level names FileHound never indexes (NTFS metafiles, recycle bin, restore points).</summary>
    internal static bool IsSkippedRootName(ReadOnlySpan<char> name)
    {
        foreach (var s in s_skipRootNames) if (name.Equals(s, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static unsafe long EstimateRecordCount(Microsoft.Win32.SafeHandles.SafeFileHandle h)
    {
        byte* data = stackalloc byte[128];
        if (!Kernel32.DeviceIoControl(h, FSCTL_GET_NTFS_VOLUME_DATA, null, 0, data, 128, out _, 0)) return 0;
        uint bytesPerRecord = *(uint*)(data + 48);
        long mftValid = *(long*)(data + 56);
        return bytesPerRecord == 0 ? 0 : mftValid / bytesPerRecord;
    }
}
