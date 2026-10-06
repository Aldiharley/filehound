using FileHound.Core.Search;
using Xunit.Abstractions;

namespace FileHound.Core.Tests;

public class PerformanceTests(ITestOutputHelper output)
{
    [Fact, Trait("Category", "Perf")]
    public void Two_million_entries_median_under_budget()
    {
        string[] words = ["report", "invoice", "photo", "holiday", "project", "backup", "final", "draft", "budget", "music",
                          "video", "setup", "notes", "scan", "family", "work", "data", "test", "config", "readme"];
        string[] exts = ["pdf", "docx", "jpg", "png", "mp4", "mp3", "txt", "xlsx", "zip", "exe", "cs", "json"];
        var rnd = new Random(7);
        var v = new VolumeIndex(@"C:\", IndexMode.Standard, 2_100_000);
        var dirs = new List<int> { 0 };
        long now = DateTime.UtcNow.Ticks;
        for (int i = 0; i < 2_000_000; i++)
        {
            bool dir = i % 10 == 0;
            string name = $"{words[rnd.Next(words.Length)]}_{words[rnd.Next(words.Length)]}{rnd.Next(10000)}" + (dir ? "" : "." + exts[rnd.Next(exts.Length)]);
            int e = v.Add(dirs[rnd.Next(dirs.Count)], name, dir ? EntryFlags.Directory : EntryFlags.MetadataKnown, rnd.Next(1 << 30), now - rnd.Next(1 << 30) * 10_000L);
            if (dir) dirs.Add(e);
        }

        var engine = new SearchEngine();
        string[] queries = ["report", "inv", "holiday photo", "fnl", "budjet", "ext:pdf invoice", "size:>500mb", "*.json",
                            "proj back", "dm:year notes", "qzx", "music_video"];
        engine.Search([v], new SearchRequest("warmup"));
        var times = new List<(string Query, double Ms, int Count)>();
        foreach (var q in queries)
        {
            var sw = Stopwatch.StartNew();
            var r = engine.Search([v], new SearchRequest(q));
            times.Add((q, sw.Elapsed.TotalMilliseconds, r.TotalCount));
        }
        foreach (var t in times) output.WriteLine($"{t.Query,-18} {t.Ms,8:F1} ms  {t.Count,9:N0} hits");
        var median = times.Select(t => t.Ms).Order().ElementAt(times.Count / 2);
        output.WriteLine($"median {median:F1} ms");
        Assert.True(median <= 150, $"median {median:F1} ms");
    }
}
