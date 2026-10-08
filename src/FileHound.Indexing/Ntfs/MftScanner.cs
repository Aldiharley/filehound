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

    /// <summary>True when the last <see cref="Scan"/> had to create the volume's change journal first.</summary>
    public bool CreatedJournal { get; private set; }

    public const ulong JournalMaximumSize = 64UL << 20, JournalAllocationDelta = 8UL << 20;

    /// <summary>FSCTL_CREATE_USN_JOURNAL needs a writable volume handle; this is the one place indexing writes to a drive.</summary>
    internal static unsafe bool TryCreateJournal(char letter)
    {
        using var w = Kernel32.CreateFile($@"\\.\{char.ToUpperInvariant(letter)}:", Kernel32.GENERIC_READ | Kernel32.GENERIC_WRITE,
            Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE, 0, Kernel32.OPEN_EXISTING, Kernel32.FILE_FLAG_BACKUP_SEMANTICS, 0);
        if (w.IsInvalid) return false;
        var data = new CreateUsnJournalData { MaximumSize = JournalMaximumSize, AllocationDelta = JournalAllocationDelta };
        return Kernel32.DeviceIoControl(w, Kernel32.FSCTL_CREATE_USN_JOURNAL, &data, sizeof(CreateUsnJournalData), null, 0, out _, 0);
    }

    /// <summary>Timing breakdown of the last <see cref="Scan"/> (diagnostics).</summary>
    public TimeSpan IoTime { get; private set; }
    public TimeSpan ParseTime { get; private set; }
    public TimeSpan BuildTime { get; private set; }

    public enum ScanMethod { None, RawVolume, FileRecord, Enumeration }

    /// <summary>Try the raw $MFT volume reader first (names, sizes and dates in one pass). Default true.</summary>
    public bool PreferRaw { get; init; } = true;
    /// <summary>Then try per-record FSCTL_GET_NTFS_FILE_RECORD reads (works when raw volume reads are blocked). Default true.</summary>
    public bool PreferFileRecord { get; init; } = true;
    /// <summary>Worker threads for the FSCTL_GET_NTFS_FILE_RECORD tier (0 = default).</summary>
    public int FileRecordThreads { get; init; }
    /// <summary>Which tier produced the last scan.</summary>
    public ScanMethod Method { get; private set; }
    /// <summary>True when the last scan used <see cref="RawMftReader"/>.</summary>
    public bool UsedRawReader => Method == ScanMethod.RawVolume;
    /// <summary>Why faster tiers were skipped for the last scan (null when the first tier worked or none was tried).</summary>
    public string? FallbackReason { get; private set; }

    public unsafe VolumeIndex Scan(DriveDescriptor drive, IReadOnlyCollection<string> excluded, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        long ioTicks = 0, parseTicks = 0;
        Method = ScanMethod.None;
        FallbackReason = null;
        using var h = Kernel32.OpenVolume(drive.Letter);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot open volume {drive.Letter}:");

        // Captured before reading: the USN updater replays everything that changes while we scan.
        UsnJournalDataV1 jd;
        if (!Kernel32.DeviceIoControl(h, Kernel32.FSCTL_QUERY_USN_JOURNAL, null, 0, &jd, sizeof(UsnJournalDataV1), out _, 0))
        {
            int err = Marshal.GetLastPInvokeError();
            // A freshly formatted volume has no change journal. Create one (as Everything does): without it there are no
            // live updates and no deletion log for the drive. Sized like the Windows default for data volumes.
            if (err != Kernel32.ERROR_JOURNAL_NOT_ACTIVE || !TryCreateJournal(drive.Letter)
                || !Kernel32.DeviceIoControl(h, Kernel32.FSCTL_QUERY_USN_JOURNAL, null, 0, &jd, sizeof(UsnJournalDataV1), out _, 0))
                throw new Win32Exception(err, $"No USN journal on {drive.Letter}:");
            CreatedJournal = true;
        }

        if (PreferRaw)
        {
            try
            {
                var raw = new RawMftReader();
                var rv = raw.Read(h, drive, progress, ct);
                Method = ScanMethod.RawVolume;
                IoTime = raw.IoTime;
                ParseTime = raw.ParseTime;
                BuildTime = raw.BuildTime;
                return Finish(rv, jd, drive, excluded, progress);
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException or Win32Exception)
            {
                FallbackReason = "raw volume read: " + ex.Message; // e.g. Win32 error 50 when security software blocks raw reads
            }
        }

        if (PreferFileRecord)
        {
            try
            {
                var fr = FileRecordThreads > 0 ? new FileRecordMftReader { Threads = FileRecordThreads } : new FileRecordMftReader();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var fv = fr.Read(drive, progress, ct);
                Method = ScanMethod.FileRecord;
                ParseTime = fr.ParseTime;
                BuildTime = fr.BuildTime;
                IoTime = sw.Elapsed - fr.ParseTime - fr.BuildTime; // parsing overlaps fetching; this is the remainder
                return Finish(fv, jd, drive, excluded, progress);
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException or Win32Exception)
            {
                FallbackReason = (FallbackReason is null ? "" : FallbackReason + "; ") + "file-record reads: " + ex.Message;
            }
        }
        Method = ScanMethod.Enumeration;

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
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                bool ok = Kernel32.DeviceIoControl(h, Kernel32.FSCTL_ENUM_USN_DATA, &med, sizeof(MftEnumDataV1), buffer, BufferSize, out int returned, 0);
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                ioTicks += t1 - t0;
                if (!ok)
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
                parseTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t1;
                if (progress is not null && Environment.TickCount64 - lastReport > 200)
                {
                    lastReport = Environment.TickCount64;
                    long pos = (long)(med.StartFileReferenceNumber & 0x0000_FFFF_FFFF_FFFF);
                    progress.Report(new ScanProgress(builder.Count, 0, 0, totalRecords > 0 ? Math.Min(0.99, (double)pos / totalRecords) : 0));
                }
            }
        }
        finally { NativeMemory.Free(buffer); }

        IoTime = System.Diagnostics.Stopwatch.GetElapsedTime(0, ioTicks);
        ParseTime = System.Diagnostics.Stopwatch.GetElapsedTime(0, parseTicks);
        long b0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var v = builder.Build(RootRecord);
        BuildTime = System.Diagnostics.Stopwatch.GetElapsedTime(b0);
        return Finish(v, jd, drive, excluded, progress);
    }

    private static VolumeIndex Finish(VolumeIndex v, UsnJournalDataV1 jd, DriveDescriptor drive, IReadOnlyCollection<string> excluded, IProgress<ScanProgress>? progress)
    {
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

    /// <summary>Number of MFT records within the MFT's valid data length (in-use and free alike).</summary>
    internal static unsafe long EstimateRecordCount(Microsoft.Win32.SafeHandles.SafeFileHandle h)
    {
        byte* data = stackalloc byte[128];
        if (!Kernel32.DeviceIoControl(h, FSCTL_GET_NTFS_VOLUME_DATA, null, 0, data, 128, out _, 0)) return 0;
        uint bytesPerRecord = *(uint*)(data + 48);
        long mftValid = *(long*)(data + 56);
        return bytesPerRecord == 0 ? 0 : mftValid / bytesPerRecord;
    }
}
