using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using FileHound.Core.Index;
using FileHound.Core.Search;
using FileHound.Indexing;

/// <summary>
/// End-to-end validation of Turbo (MFT + USN journal) indexing on a real NTFS drive. Must run elevated.
/// Writes a human-readable report; returns the number of failed checks.
/// </summary>
internal static class TurboValidation
{
    private static readonly List<string> s_lines = [];
    private static int s_failures;

    public static async Task<int> RunAsync(char letter, string dataDir, string reportPath)
    {
        try { return await RunCoreAsync(letter, dataDir); }
        catch (Exception ex)
        {
            Fail($"Unhandled exception: {ex}");
            return s_failures;
        }
        finally
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            File.WriteAllLines(reportPath, s_lines);
        }
    }

    private static async Task<int> RunCoreAsync(char letter, string dataDir)
    {
        Say($"FileHound Turbo validation on {letter}: at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Check("Process is elevated", Elevation.IsElevated);
        if (!Elevation.IsElevated) return s_failures;
        if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
        IndexManager.Log = m => Say("    log: " + m);

        var drive = DriveDiscovery.GetDrives().Single(d => d.Letter == letter);
        IndexOptions Options() => new(dataDir, [], PreferTurbo: true, DriveSource: () => [drive], SnapshotInterval: TimeSpan.FromHours(1));
        var work = Path.Combine($"{letter}:\\", "FileHoundTurboTest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var engine = new SearchEngine();
        try
        {
            // ---------------------------------------------------------------- 1. fresh MFT scan
            var sw = Stopwatch.StartNew();
            var m = new IndexManager(Options());
            await m.StartAsync();
            await m.WaitForIdleAsync();
            var st = m.Drives.Single();
            Say($"1. Fresh scan: mode={st.Mode} status={st.Status} entries={st.Entries:N0} metadataComplete={st.MetadataComplete} in {sw.Elapsed.TotalSeconds:F1}s");
            Check("Turbo mode used", st.Mode == IndexMode.Turbo);
            Check("Drive ready", st.Status == DriveStatus.Ready);
            Check("Over 100k entries", st.Entries > 100_000);
            Check("Metadata filled", st.MetadataComplete);
            int Count(string q) => engine.Search(m.Volumes, new SearchRequest(q, Fuzzy: false)).TotalCount;
            Check("explorer.exe is found with its size", engine.Search(m.Volumes, new SearchRequest("explorer.exe path:windows", Fuzzy: false)).Hits
                .Any(h => h.Volume.Size(h.Entry) > 0));
            Check("size:>100mb finds files", Count("size:>100mb") > 0);
            Check("dm:year finds files", Count("dm:year") > 0);

            // ---------------------------------------------------------------- 2. live create / rename / recycle
            var file = Path.Combine(work, "turbo-new-file.txt");
            File.WriteAllText(file, "hello");
            Check("Live create appears", await WaitUntil(() => Count("turbo-new-file.txt") == 1));
            File.Move(file, Path.Combine(work, "turbo-renamed-file.txt"));
            Check("Live rename appears", await WaitUntil(() => Count("turbo-renamed-file.txt") == 1 && Count("turbo-new-file.txt") == 0));
            Recycle(Path.Combine(work, "turbo-renamed-file.txt"));
            Check("Recycle Bin delete disappears", await WaitUntil(() => Count("turbo-renamed-file.txt") == 0));

            // ---------------------------------------------------------------- 3. folder recycle + restore
            var folder = Path.Combine(work, "turbo-folder");
            Directory.CreateDirectory(Path.Combine(folder, "inner"));
            File.WriteAllText(Path.Combine(folder, "inner", "turbo-child-a.txt"), "a");
            File.WriteAllText(Path.Combine(folder, "turbo-child-b.txt"), "b");
            Check("Folder with children indexed", await WaitUntil(() => Count("turbo-child-a.txt") == 1 && Count("turbo-child-b.txt") == 1));
            var before = RecycledItems(letter);
            Recycle(folder);
            Check("Recycled folder's children disappear", await WaitUntil(() => Count("turbo-child-a.txt") == 0 && Count("turbo-child-b.txt") == 0));
            var recycled = RecycledItems(letter).Except(before).FirstOrDefault(p => Path.GetFileName(p).StartsWith("$R", StringComparison.OrdinalIgnoreCase) && Directory.Exists(p));
            Check("Found the folder in the Recycle Bin", recycled is not null);
            if (recycled is not null)
            {
                Directory.Move(recycled, folder); // what Explorer's "Restore" does
                Check("Restored folder's children come back", await WaitUntil(() => Count("turbo-child-a.txt") == 1 && Count("turbo-child-b.txt") == 1));
                File.AppendAllText(Path.Combine(folder, "inner", "turbo-child-a.txt"), new string('x', 5000));
                Check("Change inside restored folder updates size",
                    await WaitUntil(() => engine.Search(m.Volumes, new SearchRequest("turbo-child-a.txt", Fuzzy: false)).Hits.FirstOrDefault() is { } h && h.Volume.Size(h.Entry) > 5000));
                File.Delete(Path.Combine(folder, "turbo-child-b.txt"));
                Check("Delete inside restored folder applies", await WaitUntil(() => Count("turbo-child-b.txt") == 0));
            }

            // ---------------------------------------------------------------- 4. search speed on the Turbo index
            foreach (var q in new[] { "report", "invce scan", "ext:dll microsoft", "size:>1gb", "*.log", "budjet", "path:windows\\system32 kernel" })
            {
                engine.Search(m.Volumes, new SearchRequest(q));
                var times = Enumerable.Range(0, 5).Select(_ => engine.Search(m.Volumes, new SearchRequest(q))).ToList();
                Say($"   search {q,-28} median {times.Select(t => t.Elapsed.TotalMilliseconds).Order().ElementAt(2),7:F1} ms  {times[0].TotalCount,9:N0} hits");
            }

            // ---------------------------------------------------------------- 5. restart: resume from snapshot + USN replay
            await m.DisposeAsync();
            var offline = Path.Combine(work, "turbo-made-while-stopped.txt");
            File.WriteAllText(offline, "made while FileHound was not running");
            sw.Restart();
            m = new IndexManager(Options());
            await m.StartAsync();
            Say($"5. Restart: snapshot published in {sw.Elapsed.TotalSeconds:F2}s");
            await m.WaitForIdleAsync();
            st = m.Drives.Single();
            Say($"   after resume: mode={st.Mode} status={st.Status} entries={st.Entries:N0} metadataComplete={st.MetadataComplete} total {sw.Elapsed.TotalSeconds:F1}s");
            Check("Resumed in Turbo mode", st.Mode == IndexMode.Turbo && st.Status == DriveStatus.Ready);
            Check("Resume much faster than a fresh scan", sw.Elapsed.TotalSeconds < 60);
            Check("Change made while stopped is replayed from the journal", await WaitUntil(() => Count("turbo-made-while-stopped.txt") == 1));
            Check("Restored folder survived restart", Count("turbo-child-a.txt") == 1);
            await m.DisposeAsync();
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        Say(s_failures == 0 ? "RESULT: ALL CHECKS PASSED" : $"RESULT: {s_failures} CHECK(S) FAILED");
        return s_failures;
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 8000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }

    private static void Say(string line)
    {
        lock (s_lines) s_lines.Add(line);
        Console.WriteLine(line);
    }

    private static void Check(string name, bool ok)
    {
        if (!ok) s_failures++;
        Say($"   [{(ok ? "PASS" : "FAIL")}] {name}");
    }

    private static void Fail(string message)
    {
        s_failures++;
        Say("   [FAIL] " + message);
    }

    private static IReadOnlyList<string> RecycledItems(char letter)
    {
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var dir = $@"{letter}:\$Recycle.Bin\{sid}";
        return Directory.Exists(dir) ? Directory.GetFileSystemEntries(dir) : [];
    }

    // ---------------------------------------------------------------- send to Recycle Bin (SHFileOperation)

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public nint hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public nint hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW op);

    private static void Recycle(string path)
    {
        const uint FO_DELETE = 3;
        const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;
        var op = new SHFILEOPSTRUCTW { wFunc = FO_DELETE, pFrom = path + "\0\0", fFlags = FOF_SILENT | FOF_NOCONFIRMATION | FOF_ALLOWUNDO | FOF_NOERRORUI };
        int rc = SHFileOperationW(ref op);
        if (rc != 0) throw new IOException($"Recycle failed for {path} (0x{rc:X})");
    }
}
