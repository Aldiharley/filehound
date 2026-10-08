using System.Diagnostics;
using FileHound.Core.Index;
using FileHound.Core.Search;
using FileHound.Indexing;

// filehound-cli: headless validation tool for the indexing and search engine.
//   filehound-cli scan [C D E] [--fresh] [--data <dir>]
//   filehound-cli search "<query>" [--top 20] [--drives C,D] [--data <dir>]
//   filehound-cli bench "<q1>" "<q2>" ... [--drives C,D] [--data <dir>]

var argList = args.ToList();
string data = TakeOption(argList, "--data") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileHound", "cli");
bool fresh = TakeFlag(argList, "--fresh");
int top = int.TryParse(TakeOption(argList, "--top"), out var t) ? t : 20;
var drivesOpt = TakeOption(argList, "--drives")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => char.ToUpperInvariant(s[0])).ToHashSet();

if (argList.Count == 0)
{
    Console.WriteLine("usage: filehound-cli scan [letters] [--fresh] | search \"query\" [--top N] | bench \"q1\" \"q2\" ... [--drives C,D] [--data dir]");
    return 1;
}

string command = argList[0].ToLowerInvariant();
var rest = argList.Skip(1).ToList();
if (command == "scan" && rest.Count > 0) drivesOpt = rest.Select(s => char.ToUpperInvariant(s[0])).ToHashSet();

if (command == "turbo-validate")
{
    // filehound-cli turbo-validate C --report <file> --data <dir>   (must run elevated)
    char letter = char.ToUpperInvariant((rest.FirstOrDefault() ?? "C")[0]);
    string report = TakeOption(argList, "--report") ?? Path.Combine(Path.GetTempPath(), "filehound-turbo-report.txt");
    string turboData = Path.Combine(Path.GetTempPath(), "fh-turbo-validate");
    return await TurboValidation.RunAsync(letter, turboData, report);
}

if (command == "recovery-probe")
{
    // filehound-cli recovery-probe C --report <file> [--no-snapshot]   (must run elevated)
    string report = TakeOption(argList, "--report") ?? Path.Combine(Path.GetTempPath(), "filehound-recovery-probe.txt");
    bool noSnapshot = TakeFlag(argList, "--no-snapshot");
    return RecoveryProbe.Run(char.ToUpperInvariant((rest.FirstOrDefault() ?? "C")[0]), report, allowSnapshot: !noSnapshot);
}

if (command == "mft-diff")
{
    // filehound-cli mft-diff C --report <file>   (must run elevated)
    string report = TakeOption(argList, "--report") ?? Path.Combine(Path.GetTempPath(), "filehound-mft-diff.txt");
    return MftDiff.Run(char.ToUpperInvariant((rest.FirstOrDefault() ?? "C")[0]), report);
}

if (command == "raw-probe")
{
    // filehound-cli raw-probe C --report <file>   (must run elevated)
    string report = TakeOption(argList, "--report") ?? Path.Combine(Path.GetTempPath(), "filehound-raw-probe.txt");
    return RawProbe.Run(char.ToUpperInvariant((rest.FirstOrDefault() ?? "C")[0]), report);
}

if (command == "turbo-bench")
{
    // filehound-cli turbo-bench C F M --report <file>   (must run elevated)
    // Per drive: the automatic tiered scan first (cold if the MFT isn't cached), the file-record tier single-threaded
    // (first drive only), enumeration (+ metadata fill timing on the first drive), then a warm automatic scan.
    // Compares entry counts and path sets with enumeration, and sizes with the disk.
    string report = TakeOption(argList, "--report") ?? Path.Combine(Path.GetTempPath(), "filehound-turbo-bench.txt");
    // Drive arguments only ("C", "C:"); options were removed above, but `rest` was captured before that.
    var letters = argList.Skip(1).Where(s => s.Length <= 2 && char.IsAsciiLetter(s[0]) && (s.Length == 1 || s[1] == ':'))
        .Select(s => char.ToUpperInvariant(s[0])).DefaultIfEmpty('C').ToList();
    var lines = new List<string> { $"elevated={Elevation.IsElevated}  {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
    void Line(string s) { lines.Add(s); File.WriteAllLines(report, lines); }
    bool first = true;
    foreach (var letter in letters)
    {
        try
        {
            var drive = DriveDiscovery.GetDrives().Single(d => d.Letter == letter);
            Line($"== {letter}: ({drive.Format}, {drive.TotalSize / 1e12:F1} TB)");
            (VolumeIndex V, FileHound.Indexing.Ntfs.MftScanner S, double Secs) Scan(bool raw, bool fileRecord, int threads = 0)
            {
                var s = new FileHound.Indexing.Ntfs.MftScanner { PreferRaw = raw, PreferFileRecord = fileRecord, FileRecordThreads = threads };
                var sw = Stopwatch.StartNew();
                var v = s.Scan(drive, [], null, CancellationToken.None);
                return (v, s, sw.Elapsed.TotalSeconds);
            }
            string Describe(string label, (VolumeIndex V, FileHound.Indexing.Ntfs.MftScanner S, double Secs) r) =>
                $"{label,-20} {r.V.LiveCount,11:N0} entries in {r.Secs,6:F2}s  method={r.S.Method}  (fetch/io {r.S.IoTime.TotalSeconds:F2}s, parse {r.S.ParseTime.TotalSeconds:F2}s, build {r.S.BuildTime.TotalSeconds:F2}s)  incomplete={MetadataFiller.HasIncompleteMetadata(r.V)}"
                + (r.S.FallbackReason is null ? "" : $"\n    fallback: {r.S.FallbackReason}");

            var auto1 = Scan(raw: true, fileRecord: true);
            Line(Describe("auto #1 (cold-ish):", auto1));
            if (first)
            {
                var single = Scan(raw: false, fileRecord: true, threads: 1);
                Line(Describe("file-record 1 thread:", single));
            }
            var en = Scan(raw: false, fileRecord: false);
            string fill = "";
            if (first)
            {
                var swf = Stopwatch.StartNew();
                await new MetadataFiller().FillAsync(en.V, null, CancellationToken.None);
                fill = $" + metadata fill {swf.Elapsed.TotalSeconds:F2}s";
            }
            Line(Describe("enumeration:", en) + fill);
            var raw2 = Scan(raw: true, fileRecord: true);
            Line(Describe("auto #2 (warm):", raw2));
            first = false;

            // Agreement: paths from the enumeration index must exist in the raw index, sizes must match the disk.
            var rnd = new Random(1);
            int pathChecked = 0, pathMissing = 0, sizeChecked = 0, sizeMismatch = 0, sizeChanged = 0;
            var missingExamples = new List<string>();
            for (int i = 0; i < 20000 && pathChecked < 2000; i++)
            {
                int e = rnd.Next(1, en.V.Count);
                if (!en.V.IsLive(e)) continue;
                var p = PathBuilder.GetFullPath(en.V, e);
                pathChecked++;
                if (raw2.V.FindByPath(p) < 0) { pathMissing++; if (missingExamples.Count < 5) missingExamples.Add(p); }
            }
            // Sizes are only meaningful when the scan delivered metadata itself (not for plain enumeration).
            for (int i = 0; i < 40000 && sizeChecked < 2000 && raw2.S.Method != FileHound.Indexing.Ntfs.MftScanner.ScanMethod.Enumeration; i++)
            {
                int e = rnd.Next(1, raw2.V.Count);
                if (!raw2.V.IsLive(e) || raw2.V.IsDirectory(e)) continue;
                var p = PathBuilder.GetFullPath(raw2.V, e);
                FileInfo fi;
                try { fi = new FileInfo(p); if (!fi.Exists) continue; } catch (Exception) { continue; }
                sizeChecked++;
                if (fi.Length != raw2.V.Size(e))
                {
                    // Files being written right now (logs, databases) legitimately differ; re-stat to tell.
                    if (fi.LastWriteTimeUtc > DateTime.UtcNow.AddMinutes(-10)) sizeChanged++; else sizeMismatch++;
                }
            }
            Line($"agreement: entries raw/enum {(double)raw2.V.LiveCount / Math.Max(1, en.V.LiveCount):P2}; paths {pathChecked - pathMissing}/{pathChecked} found; sizes {sizeChecked - sizeMismatch - sizeChanged}/{sizeChecked} equal ({sizeChanged} recently modified, {sizeMismatch} mismatched)");
            foreach (var m in missingExamples) Line($"   missing in raw: {m}");
        }
        catch (Exception ex) { Line($"error on {letter}: {ex}"); }
    }
    File.WriteAllLines(report, lines);
    return 0;
}

if (command == "rawwalk")
{
    // Baseline: parallel enumeration only (no index), to separate filesystem cost from indexing cost.
    int threads = int.TryParse(rest.ElementAtOrDefault(1), out var th) ? th : 8;
    var root = rest.ElementAtOrDefault(0) ?? @"C:\";
    var queue = new System.Collections.Concurrent.BlockingCollection<string>();
    long entries = 0, dirs = 0; int pending = 1;
    queue.Add(root);
    var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, BufferSize = 65536 };
    var swr = Stopwatch.StartNew();
    var tasks = Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
    {
        foreach (var dir in queue.GetConsumingEnumerable())
        {
            try
            {
                foreach (var e in new System.IO.Enumeration.FileSystemEnumerable<(string, bool)>(dir,
                    (ref System.IO.Enumeration.FileSystemEntry x) => (x.ToFullPath(), x.IsDirectory && (x.Attributes & FileAttributes.ReparsePoint) == 0), opts))
                {
                    Interlocked.Increment(ref entries);
                    if (e.Item2) { Interlocked.Increment(ref pending); queue.Add(e.Item1); }
                }
            }
            catch (Exception) { }
            Interlocked.Increment(ref dirs);
            if (Interlocked.Decrement(ref pending) == 0) queue.CompleteAdding();
        }
    })).ToArray();
    await Task.WhenAll(tasks);
    Console.WriteLine($"rawwalk {root} threads={threads}: {entries:N0} entries, {dirs:N0} dirs in {swr.Elapsed.TotalSeconds:F1}s ({entries / swr.Elapsed.TotalSeconds:N0}/s)");
    return 0;
}

if (fresh && Directory.Exists(Path.Combine(data, "index")))
    foreach (var f in Directory.GetFiles(Path.Combine(data, "index"), "*.fhx")) File.Delete(f);

IndexManager.Log = msg => Console.Error.WriteLine($"  [{DateTime.Now:HH:mm:ss}] {msg}");
Console.WriteLine($"Elevated: {Elevation.IsElevated}  (Turbo/MFT indexing {(Elevation.IsElevated ? "available" : "needs admin")})");

var sw = Stopwatch.StartNew();
await using var manager = new IndexManager(new IndexOptions(data, [],
    DriveSource: () => DriveDiscovery.GetDrives().Where(d => drivesOpt is null || drivesOpt.Contains(d.Letter)).ToList()));
await manager.StartAsync();
Console.WriteLine($"Snapshots published in {sw.Elapsed.TotalSeconds:F2}s");
await manager.WaitForIdleAsync();
Console.WriteLine($"Indexing finished in {sw.Elapsed.TotalSeconds:F1}s");
foreach (var d in manager.Drives)
    Console.WriteLine($"  {d.Drive.Letter}: {d.Drive.Format,-6} {d.Mode,-8} {d.Status,-8} {d.Entries,12:N0} entries  skipped {d.Skipped,6:N0}  {d.Error}");
long total = manager.Volumes.Sum(v => (long)v.LiveCount);
System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
GC.WaitForPendingFinalizers();
GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
long indexBytes = manager.Volumes.Sum(v => v.ApproximateBytes);
Console.WriteLine($"Total: {total:N0} entries, managed heap {GC.GetTotalMemory(false) / 1024 / 1024:N0} MB, index arrays {indexBytes / 1024 / 1024:N0} MB ({(total == 0 ? 0 : indexBytes / total)} B/entry)");

var engine = new SearchEngine();
switch (command)
{
    case "scan":
        break;
    case "search":
    {
        var query = string.Join(' ', rest);
        var result = engine.Search(manager.Volumes, new SearchRequest(query, MaxResults: top));
        Console.WriteLine($"\n\"{query}\": {result.TotalCount:N0} results in {result.Elapsed.TotalMilliseconds:F1} ms{(result.UsedTypoPass ? " (typo pass)" : "")}");
        foreach (var e in result.Errors) Console.WriteLine($"  ! {e}");
        foreach (var h in result.Hits)
            Console.WriteLine($"  {h.Tier,-11} {PathBuilder.GetFullPath(h.Volume, h.Entry)}");
        break;
    }
    case "bench":
        foreach (var q in rest)
        {
            engine.Search(manager.Volumes, new SearchRequest(q));
            var times = Enumerable.Range(0, 5).Select(_ => engine.Search(manager.Volumes, new SearchRequest(q))).ToList();
            var median = times.Select(r => r.Elapsed.TotalMilliseconds).Order().ElementAt(2);
            Console.WriteLine($"  {q,-24} median {median,7:F1} ms   {times[0].TotalCount,10:N0} hits");
        }
        break;
    default:
        Console.WriteLine($"Unknown command '{command}'");
        return 1;
}
return 0;

static string? TakeOption(List<string> list, string name)
{
    int i = list.FindIndex(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    if (i < 0 || i + 1 >= list.Count) return null;
    var value = list[i + 1];
    list.RemoveRange(i, 2);
    return value;
}

static bool TakeFlag(List<string> list, string name) => list.RemoveAll(a => a.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;
