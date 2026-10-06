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

if (command == "turbo-bench")
{
    // filehound-cli turbo-bench C --report <file>   (must run elevated): times MFT scans with a breakdown
    char letter = char.ToUpperInvariant((rest.FirstOrDefault() ?? "C")[0]);
    string report = TakeOption(argList, "--report") ?? Path.Combine(Path.GetTempPath(), "filehound-turbo-bench.txt");
    var lines = new List<string> { $"elevated={Elevation.IsElevated}" };
    try
    {
        var drive = DriveDiscovery.GetDrives().Single(d => d.Letter == letter);
        for (int run = 1; run <= 3; run++)
        {
            var scanner = new FileHound.Indexing.Ntfs.MftScanner();
            var swb = Stopwatch.StartNew();
            var v = scanner.Scan(drive, [], null, CancellationToken.None);
            lines.Add($"run {run}: {v.LiveCount:N0} entries in {swb.Elapsed.TotalSeconds:F2}s  (ioctl {scanner.IoTime.TotalSeconds:F2}s, parse+add {scanner.ParseTime.TotalSeconds:F2}s, build {scanner.BuildTime.TotalSeconds:F2}s)");
            var swf = Stopwatch.StartNew();
            if (run == 1) { await new MetadataFiller().FillAsync(v, null, CancellationToken.None); lines.Add($"run {run}: metadata fill {swf.Elapsed.TotalSeconds:F2}s"); }
        }
    }
    catch (Exception ex) { lines.Add("error: " + ex); }
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
