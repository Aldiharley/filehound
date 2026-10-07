using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

/// <summary>
/// Diagnostics for raw volume reads (must run elevated): reports the volume geometry as seen by NTFS and by the
/// storage stack, then attempts reads with different open flags and sizes, logging the Win32 error of each.
/// </summary>
internal static unsafe partial class RawProbe
{
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint sa, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle h, uint code, void* inBuf, int inSize, void* outBuf, int outSize, out int returned, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadFile(SafeFileHandle h, byte* buffer, int toRead, out int read, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFilePointerEx(SafeFileHandle h, long distance, out long newPos, uint method);

    private const uint GENERIC_READ = 0x80000000, SHARE_RW = 0x3, SHARE_RWD = 0x7, OPEN_EXISTING = 3;
    private const uint FLAG_BACKUP = 0x02000000, FLAG_NO_BUFFERING = 0x20000000;

    public static int Run(char letter, string reportPath)
    {
        var lines = new List<string> { $"raw-probe {letter}: {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
        void Say(string s) { lines.Add(s); File.WriteAllLines(reportPath, lines); }
        try
        {
            using (var h = CreateFile($@"\\.\{letter}:", GENERIC_READ, SHARE_RW, 0, OPEN_EXISTING, FLAG_BACKUP, 0))
            {
                Say($"open (BACKUP_SEMANTICS): invalid={h.IsInvalid} err={(h.IsInvalid ? Marshal.GetLastPInvokeError() : 0)}");
                if (h.IsInvalid) return 1;
                byte* d = stackalloc byte[128];
                bool ok = DeviceIoControl(h, 0x00090064, null, 0, d, 128, out _, 0);
                Say($"NTFS_VOLUME_DATA ok={ok} err={(ok ? 0 : Marshal.GetLastPInvokeError())} bytesPerSector={*(uint*)(d + 40)} cluster={*(uint*)(d + 44)} record={*(uint*)(d + 48)} mftValid={*(long*)(d + 56)} mftStartLcn={*(long*)(d + 64)} mft2Lcn={*(long*)(d + 72)}");

                // STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR via IOCTL_STORAGE_QUERY_PROPERTY(StorageAccessAlignmentProperty=6)
                uint* q = stackalloc uint[3];
                q[0] = 6; q[1] = 0; q[2] = 0;
                byte* a = stackalloc byte[64];
                bool aok = DeviceIoControl(h, 0x002D1400, q, 12, a, 64, out int ar, 0);
                Say($"ACCESS_ALIGNMENT ok={aok} err={(aok ? 0 : Marshal.GetLastPInvokeError())} bytes={ar} logicalSector={*(uint*)(a + 16)} physicalSector={*(uint*)(a + 20)}");
                // IOCTL_DISK_GET_DRIVE_GEOMETRY_EX
                byte* g = stackalloc byte[256];
                bool gok = DeviceIoControl(h, 0x000700A0, null, 0, g, 256, out _, 0);
                Say($"DRIVE_GEOMETRY_EX ok={gok} err={(gok ? 0 : Marshal.GetLastPInvokeError())} bytesPerSector={*(uint*)(g + 20)}");
            }

            long mftOffset;
            using (var h = CreateFile($@"\\.\{letter}:", GENERIC_READ, SHARE_RW, 0, OPEN_EXISTING, FLAG_BACKUP, 0))
            {
                byte* d = stackalloc byte[128];
                DeviceIoControl(h, 0x00090064, null, 0, d, 128, out _, 0);
                mftOffset = *(long*)(d + 64) * *(uint*)(d + 44);
            }
            Say($"mft byte offset = {mftOffset}");

            foreach (var (label, share, flags) in new[] { ("BACKUP", SHARE_RW, FLAG_BACKUP), ("flags=0", SHARE_RW, 0u), ("NO_BUFFERING", SHARE_RWD, FLAG_NO_BUFFERING) })
            {
                using var h = CreateFile($@"\\.\{letter}:", GENERIC_READ, share, 0, OPEN_EXISTING, flags, 0);
                if (h.IsInvalid) { Say($"[{label}] open failed err={Marshal.GetLastPInvokeError()}"); continue; }
                foreach (var (offset, size) in new[] { (0L, 512), (0L, 4096), (mftOffset, 1024), (mftOffset, 4096), (mftOffset, 65536), (mftOffset, 1 << 20), (mftOffset, 4 << 20) })
                {
                    byte* buf = (byte*)NativeMemory.AlignedAlloc((nuint)size, 4096);
                    try
                    {
                        bool s = SetFilePointerEx(h, offset, out long pos, 0);
                        int se = s ? 0 : Marshal.GetLastPInvokeError();
                        bool r = ReadFile(h, buf, size, out int read, 0);
                        int re = r ? 0 : Marshal.GetLastPInvokeError();
                        string sig = read >= 4 ? System.Text.Encoding.ASCII.GetString(new ReadOnlySpan<byte>(buf + (offset == 0 ? 3 : 0), 4)) : "";
                        Say($"[{label}] read off={offset} size={size}: seek={s}/{se} pos={pos} read={r}/{re} bytes={read} sig='{sig}'");
                    }
                    finally { NativeMemory.AlignedFree(buf); }
                }
            }
        }
        catch (Exception ex) { Say("exception: " + ex); }
        return 0;
    }
}
