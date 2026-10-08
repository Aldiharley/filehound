using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

/// <summary>
/// Elevated diagnostics for the recovery feature. On a volume where raw ReadFile on \\.\X: is refused, tests whether
/// the bytes can be reached another way: (1) which NTFS control codes work, (2) the physical disk at the partition
/// offset, (3) a Volume Shadow Copy device (created temporarily when none exists, then deleted).
/// </summary>
internal static unsafe partial class RecoveryProbe
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

    private const uint GENERIC_READ = 0x80000000, SHARE_RW = 0x3, OPEN_EXISTING = 3, FLAG_BACKUP = 0x02000000, FLAG_NO_BUFFERING = 0x20000000;
    private const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064, FSCTL_GET_NTFS_FILE_RECORD = 0x00090068, FSCTL_GET_VOLUME_BITMAP = 0x0009006F;
    private const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;

    public static int Run(char letter, string reportPath, bool allowSnapshot)
    {
        var lines = new List<string> { $"recovery-probe {letter}: {DateTime.Now:yyyy-MM-dd HH:mm:ss} elevated={FileHound.Indexing.Elevation.IsElevated}" };
        void Say(string s) { lines.Add(s); File.WriteAllLines(reportPath, lines); }
        try
        {
            // ---- 1. control codes on the volume handle
            using var vol = CreateFile($@"\\.\{letter}:", GENERIC_READ, SHARE_RW, 0, OPEN_EXISTING, FLAG_BACKUP, 0);
            Say($"open \\\\.\\{letter}: invalid={vol.IsInvalid} err={(vol.IsInvalid ? Marshal.GetLastPInvokeError() : 0)}");
            if (vol.IsInvalid) return 1;

            byte* vd = stackalloc byte[128];
            bool vdOk = DeviceIoControl(vol, FSCTL_GET_NTFS_VOLUME_DATA, null, 0, vd, 128, out _, 0);
            uint bytesPerCluster = vdOk ? *(uint*)(vd + 44) : 4096;
            uint recordSize = vdOk ? *(uint*)(vd + 48) : 1024;
            long mftStartLcn = vdOk ? *(long*)(vd + 64) : 0;
            long totalClusters = vdOk ? *(long*)(vd + 32) : 0;
            Say($"FSCTL_GET_NTFS_VOLUME_DATA ok={vdOk} err={(vdOk ? 0 : Marshal.GetLastPInvokeError())} cluster={bytesPerCluster} record={recordSize} mftLcn={mftStartLcn} totalClusters={totalClusters}");

            long input = 16; byte* rec = stackalloc byte[12 + 4096];
            bool frOk = DeviceIoControl(vol, FSCTL_GET_NTFS_FILE_RECORD, &input, 8, rec, 12 + (int)recordSize, out int frRet, 0);
            Say($"FSCTL_GET_NTFS_FILE_RECORD(16) ok={frOk} err={(frOk ? 0 : Marshal.GetLastPInvokeError())} bytes={frRet}");

            long startLcn = 0; byte* bm = stackalloc byte[16 + 4096];
            bool bmOk = DeviceIoControl(vol, FSCTL_GET_VOLUME_BITMAP, &startLcn, 8, bm, 16 + 4096, out int bmRet, 0);
            int bmErr = bmOk ? 0 : Marshal.GetLastPInvokeError();
            Say($"FSCTL_GET_VOLUME_BITMAP ok={bmOk} err={bmErr} ({(bmErr == 234 ? "ERROR_MORE_DATA = works, partial" : "")}) bytes={bmRet} startLcn={*(long*)bm} bitmapSize={*(long*)(bm + 8)}");

            byte* jd = stackalloc byte[80];
            bool jOk = DeviceIoControl(vol, FSCTL_QUERY_USN_JOURNAL, null, 0, jd, 80, out _, 0);
            Say($"FSCTL_QUERY_USN_JOURNAL ok={jOk} err={(jOk ? 0 : Marshal.GetLastPInvokeError())}");

            long mftOffset = mftStartLcn * bytesPerCluster;
            Say($"volume ReadFile @mft: {TryRead(vol, mftOffset, 4096, out string sig)} sig='{sig}'");

            // ---- 2. physical disk at the partition offset
            byte* ext = stackalloc byte[8 + 24 * 4];
            bool exOk = DeviceIoControl(vol, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, null, 0, ext, 8 + 24 * 4, out _, 0);
            if (exOk)
            {
                uint count = *(uint*)ext;
                uint disk = *(uint*)(ext + 8);
                long partOffset = *(long*)(ext + 16);
                Say($"disk extents: count={count} disk={disk} partitionOffset={partOffset}");
                using var pd = CreateFile($@"\\.\PhysicalDrive{disk}", GENERIC_READ, SHARE_RW, 0, OPEN_EXISTING, FLAG_NO_BUFFERING, 0);
                if (pd.IsInvalid) Say($"open PhysicalDrive{disk}: failed err={Marshal.GetLastPInvokeError()}");
                else
                {
                    Say($"physical ReadFile @partition start: {TryRead(pd, partOffset, 4096, out sig, oemAt3: true)} oemId='{sig}'  ({(sig.StartsWith("NTFS") ? "plain NTFS" : sig.StartsWith("-FVE") ? "BitLocker ciphertext" : "?")})");
                    Say($"physical ReadFile @mft: {TryRead(pd, partOffset + mftOffset, 4096, out sig)} sig='{sig}'");
                }
            }
            else Say($"IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS failed err={Marshal.GetLastPInvokeError()}");

            // ---- 3. shadow copy device
            string? device = FindShadowDevice(letter, Say);
            string? createdId = null;
            if (device is null && allowSnapshot)
            {
                Say("no existing shadow copy; creating a temporary one (writes a diff area to the volume)...");
                (createdId, device) = CreateShadow(letter, Say);
            }
            if (device is not null)
            {
                using var sc = CreateFile(device, GENERIC_READ, SHARE_RW, 0, OPEN_EXISTING, FLAG_BACKUP, 0);
                if (sc.IsInvalid) Say($"open {device}: failed err={Marshal.GetLastPInvokeError()}");
                else
                {
                    bool svd = DeviceIoControl(sc, FSCTL_GET_NTFS_VOLUME_DATA, null, 0, vd, 128, out _, 0);
                    Say($"shadow FSCTL_GET_NTFS_VOLUME_DATA ok={svd} err={(svd ? 0 : Marshal.GetLastPInvokeError())}");
                    Say($"shadow ReadFile @0 (boot): {TryRead(sc, 0, 4096, out sig, oemAt3: true)} oemId='{sig}'");
                    Say($"shadow ReadFile @mft: {TryRead(sc, mftOffset, 4096, out sig)} sig='{sig}'");
                    Say($"shadow ReadFile @mft 4MB: {TryRead(sc, mftOffset, 4 << 20, out _)}");
                    // file-level access through the snapshot
                    try
                    {
                        var p = device + @"\Windows\System32\drivers\etc\hosts";
                        using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        Say($"shadow file read {p}: ok ({fs.Length} bytes)");
                    }
                    catch (Exception ex) { Say($"shadow file read: failed {ex.GetType().Name}: {ex.Message}"); }
                }
            }
            else Say("no shadow copy device available");
            if (createdId is not null)
            {
                var ps = Run("vssadmin", $"delete shadows /shadow={createdId} /quiet");
                Say($"deleted temporary shadow copy {createdId}: exit={ps.ExitCode}");
            }
        }
        catch (Exception ex) { Say("exception: " + ex); }
        return 0;
    }

    private static string TryRead(SafeFileHandle h, long offset, int size, out string sig, bool oemAt3 = false)
    {
        sig = "";
        byte* buf = (byte*)NativeMemory.AlignedAlloc((nuint)size, 4096);
        try
        {
            if (!SetFilePointerEx(h, offset, out _, 0)) return $"seek failed err={Marshal.GetLastPInvokeError()}";
            if (!ReadFile(h, buf, size, out int read, 0)) return $"FAILED err={Marshal.GetLastPInvokeError()}";
            int at = oemAt3 ? 3 : 0;
            sig = read >= at + 8 ? System.Text.Encoding.ASCII.GetString(new ReadOnlySpan<byte>(buf + at, 8)).TrimEnd() : "";
            bool allZero = true; for (int i = 0; i < Math.Min(read, 4096); i++) if (buf[i] != 0) { allZero = false; break; }
            return $"OK {read} bytes{(allZero ? " (all zeros)" : "")}";
        }
        finally { NativeMemory.AlignedFree(buf); }
    }

    private static string? FindShadowDevice(char letter, Action<string> say)
    {
        var ps = Run("vssadmin", "list shadows");
        string? origVol = null, device = null, latest = null;
        foreach (var raw in ps.Output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Original Volume:", StringComparison.OrdinalIgnoreCase)) origVol = line;
            if (line.StartsWith("Shadow Copy Volume:", StringComparison.OrdinalIgnoreCase))
            {
                device = line["Shadow Copy Volume:".Length..].Trim();
                if (origVol is not null && origVol.Contains($"({letter}:)", StringComparison.OrdinalIgnoreCase)) latest = device;
            }
        }
        say($"existing shadow copies for {letter}: {(latest ?? "none")}");
        return latest;
    }

    private static (string? Id, string? Device) CreateShadow(char letter, Action<string> say)
    {
        var ps = Run("vssadmin", $"create shadow /for={letter}:");
        say($"vssadmin create shadow exit={ps.ExitCode}: {string.Join(" | ", ps.Output.Split('\n').Select(l => l.Trim()).Where(l => l.Contains("Shadow Copy") || l.Contains("Error")))}");
        string? id = null, device = null;
        foreach (var raw in ps.Output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Shadow Copy ID:", StringComparison.OrdinalIgnoreCase)) id = line["Shadow Copy ID:".Length..].Trim();
            if (line.StartsWith("Shadow Copy Volume Name:", StringComparison.OrdinalIgnoreCase)) device = line["Shadow Copy Volume Name:".Length..].Trim();
        }
        return (id, device);
    }

    private static (int ExitCode, string Output) Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }
}
