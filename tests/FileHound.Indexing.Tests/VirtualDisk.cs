using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FileHound.Indexing.Tests;

/// <summary>
/// A throwaway NTFS volume on a VHDX made with diskpart (needs elevation). Used by the acceptance tests so real
/// deletes, overwrites and raw reads happen on a disk nobody cares about. Detached and deleted on dispose.
/// </summary>
internal sealed class VirtualDisk : IDisposable
{
    private readonly string _path;

    private VirtualDisk(string path, char letter)
    {
        _path = path;
        Letter = letter;
    }

    public char Letter { get; }
    public string Root => $@"{Letter}:\";

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint sa, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushFileBuffers(SafeFileHandle h);

    /// <summary>Forces NTFS to write its cached metadata ($MFT, $Bitmap) so raw reads see the current state. Test-only write access.</summary>
    public void FlushMetadata()
    {
        using var h = CreateFileW($@"\.\{Letter}:", 0x80000000 | 0x40000000, 0x3, 0, 3, 0x02000000, 0);
        if (!h.IsInvalid) FlushFileBuffers(h);
        Thread.Sleep(500);
    }

    /// <summary>Creates, attaches, partitions and formats a 256 MB VHDX; null (with the reason in <paramref name="reason"/>) when diskpart cannot.</summary>
    public static VirtualDisk? Create(string vhdPath, out string reason)
    {
        reason = "";
        char? letter = Enumerable.Range(0, 20).Select(i => (char)('Z' - i)).FirstOrDefault(l => !Directory.Exists($@"{l}:\"), '\0');
        if (letter is null or '\0') { reason = "no free drive letter"; return null; }
        var script = $"""
            create vdisk file="{vhdPath}" maximum=256 type=expandable
            select vdisk file="{vhdPath}"
            attach vdisk
            create partition primary
            format fs=ntfs quick label=FHTEST
            assign letter={letter}
            """;
        var (code, output) = RunDiskpart(script);
        if (code != 0)
        {
            reason = $"diskpart exit {code}: {output.Trim()}";
            TryDetach(vhdPath);
            TryDelete(vhdPath);
            return null;
        }
        var disk = new VirtualDisk(vhdPath, letter.Value);
        for (int i = 0; i < 50 && !Directory.Exists(disk.Root); i++) Thread.Sleep(200);
        if (!Directory.Exists(disk.Root)) { reason = "volume did not appear"; disk.Dispose(); return null; }
        // No change journal on purpose: a fresh volume is exactly the case MftScanner must handle by creating one.
        return disk;
    }

    public void Dispose()
    {
        TryDetach(_path);
        TryDelete(_path);
    }

    private static void TryDetach(string path)
    {
        try { RunDiskpart($"""
            select vdisk file="{path}"
            detach vdisk
            """); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { }
    }

    private static void TryDelete(string path)
    {
        for (int i = 0; i < 10; i++)
        {
            try { if (File.Exists(path)) File.Delete(path); return; }
            catch (IOException) { Thread.Sleep(300); }
            catch (UnauthorizedAccessException) { Thread.Sleep(300); }
        }
    }

    private static (int ExitCode, string Output) RunDiskpart(string script)
    {
        string scriptPath = Path.Combine(Path.GetTempPath(), $"fh-diskpart-{Guid.NewGuid():N}.txt");
        File.WriteAllText(scriptPath, script);
        try
        {
            var psi = new ProcessStartInfo("diskpart.exe", $"/s \"{scriptPath}\"") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(120_000);
            return (p.ExitCode, output);
        }
        finally { File.Delete(scriptPath); }
    }
}
