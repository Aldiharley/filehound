using FileHound.Core.Matching;
using FileHound.Core.Query;
using FileHound.Core.Search;

namespace FileHound.Core.Tests;

public class SearchEngineTests
{
    private static VolumeIndex Fixture()
    {
        var v = new VolumeIndex(@"C:\", IndexMode.Standard);
        var docs = v.Add(0, "Documents", EntryFlags.Directory | EntryFlags.MetadataKnown, 0, Ticks(2026, 1, 1));
        var proj = v.Add(0, "Projects", EntryFlags.Directory | EntryFlags.MetadataKnown, 0, Ticks(2026, 1, 1));
        v.Add(docs, "report.pdf", EntryFlags.MetadataKnown, 5_000, Ticks(2026, 10, 6));
        v.Add(docs, "final_report.docx", EntryFlags.MetadataKnown, 50_000, Ticks(2025, 1, 1));
        v.Add(docs, "myreport.txt", EntryFlags.MetadataKnown, 10, Ticks(2024, 1, 1));
        var src = v.Add(proj, "src", EntryFlags.Directory | EntryFlags.MetadataKnown, 0, 0);
        v.Add(src, "quarterly_report_final.xlsx", EntryFlags.MetadataKnown, 2_000_000_000, Ticks(2026, 9, 1));
        v.Add(proj, "node_modules", EntryFlags.Directory | EntryFlags.MetadataKnown, 0, 0);
        v.Add(docs, "secret.txt", EntryFlags.Hidden | EntryFlags.MetadataKnown, 1, 0);
        return v;
    }

    private static long Ticks(int y, int m, int d) => new DateTime(y, m, d, 12, 0, 0, DateTimeKind.Utc).Ticks;
    private static string[] Names(SearchResult r) => r.Hits.Select(h => h.Volume.Name(h.Entry).ToString()).ToArray();

    private static SearchResult Run(string q, SortMode s = SortMode.Relevance, bool fuzzy = true, bool hidden = true, FileCategory? cat = null) =>
        new SearchEngine().Search([Fixture()], new SearchRequest(q, s, 5000, fuzzy, hidden, cat));

    [Fact]
    public void Ranks_by_tier() =>
        Assert.Equal(["report.pdf", "final_report.docx", "quarterly_report_final.xlsx", "myreport.txt"], Names(Run("report")));

    [Fact] public void Total_count_reported() => Assert.Equal(4, Run("report").TotalCount);

    [Fact] public void And_terms() => Assert.Equal(["quarterly_report_final.xlsx"], Names(Run("report final xlsx")));

    [Fact] public void Not_term() => Assert.DoesNotContain("myreport.txt", Names(Run("report !txt")));

    [Fact] public void Or_alternatives() => Assert.Equal(2, Run("ext:pdf|ext:docx").TotalCount);

    [Fact] public void Folder_constraint() => Assert.Equal(["node_modules"], Names(Run("folder:node")));

    [Fact] public void File_constraint_excludes_folders() => Assert.Empty(Names(Run("file:node")));

    [Fact] public void Size_filter() => Assert.Equal(["quarterly_report_final.xlsx"], Names(Run("size:>1gb")));

    [Fact] public void Date_filter() => Assert.Equal(["report.pdf"], Names(Run("dm:2026-10")));

    [Fact]
    public void Hidden_excluded_when_requested()
    {
        Assert.Single(Names(Run("secret")));
        Assert.Empty(Names(Run("secret", hidden: false)));
    }

    [Fact]
    public void Category_chip() =>
        Assert.Equal(["final_report.docx", "myreport.txt", "quarterly_report_final.xlsx", "report.pdf"], Names(Run("report", cat: FileCategory.Document)).Order());

    [Fact]
    public void Category_chip_alone_lists_category() => Assert.Equal(4, Run("", cat: FileCategory.Folder).TotalCount);

    [Fact]
    public void Typo_pass_runs_when_few_hits()
    {
        var r = Run("quartelry");
        Assert.True(r.UsedTypoPass);
        Assert.Equal("quarterly_report_final.xlsx", Names(r)[0]);
        Assert.Equal(MatchTier.Typo, r.Hits[0].Tier);
    }

    [Fact] public void No_typo_pass_when_fuzzy_off() => Assert.Equal(0, Run("quartelry", fuzzy: false).TotalCount);

    [Fact] public void Path_filter_matches_self_and_descendants() => Assert.Equal(4, Run("path:projects").TotalCount); // Projects, src, xlsx, node_modules

    [Fact] public void Path_filter_combined_with_term() => Assert.Equal(["quarterly_report_final.xlsx"], Names(Run("path:projects report")));

    [Fact] public void Backslash_term_matches_full_path() => Assert.Equal(["quarterly_report_final.xlsx"], Names(Run(@"projects\src\quart")));

    [Fact] public void Backslash_term_matches_descendants() => Assert.Equal(2, Run(@"projects\sr").TotalCount); // src + its child

    [Fact] public void Drive_filter() { Assert.Equal(4, Run("report drive:c").TotalCount); Assert.Equal(0, Run("report drive:e").TotalCount); }

    [Fact] public void Name_sort() => Assert.Equal("final_report.docx", Names(Run("report", SortMode.Name))[0]);

    [Fact] public void Size_sort() => Assert.Equal("quarterly_report_final.xlsx", Names(Run("report", SortMode.Size))[0]);

    [Fact] public void Modified_sort() => Assert.Equal("report.pdf", Names(Run("report", SortMode.Modified))[0]);

    [Fact]
    public void Max_results_caps_hits_not_count()
    {
        var r = new SearchEngine().Search([Fixture()], new SearchRequest("report", MaxResults: 2));
        Assert.Equal(2, r.Hits.Count);
        Assert.Equal(4, r.TotalCount);
        Assert.Equal("report.pdf", r.Hits[0].Volume.Name(r.Hits[0].Entry).ToString());
    }

    [Fact] public void Empty_query_returns_nothing() => Assert.Equal(0, Run("").TotalCount);

    [Fact]
    public void Deleted_entries_never_returned()
    {
        var v = Fixture();
        v.Delete(v.FindByPath(@"C:\Documents"));
        Assert.Equal(1, new SearchEngine().Search([v], new SearchRequest("report")).TotalCount);
    }

    [Fact]
    public void Searches_multiple_volumes()
    {
        var e = new VolumeIndex(@"E:\", IndexMode.Standard);
        e.Add(0, "report.pdf", EntryFlags.MetadataKnown, 1, 1);
        var r = new SearchEngine().Search([Fixture(), e], new SearchRequest("report.pdf", Fuzzy: false));
        Assert.Equal(2, r.TotalCount);
    }

    [Fact]
    public void Large_index_spans_many_chunks()
    {
        var v = new VolumeIndex(@"C:\", IndexMode.Standard);
        for (int i = 0; i < 100_000; i++) v.Add(0, i % 1000 == 0 ? $"needle{i}.txt" : $"hay{i}.txt", EntryFlags.MetadataKnown, i, i);
        var r = new SearchEngine().Search([v], new SearchRequest("needle", MaxResults: 10));
        Assert.Equal(100, r.TotalCount);
        Assert.Equal(10, r.Hits.Count);
    }

    [Fact]
    public void Cancellation_throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => new SearchEngine().Search([Fixture()], new SearchRequest("report"), cts.Token));
    }

    [Fact]
    public void Errors_are_surfaced()
    {
        var r = Run("report size:zzz");
        Assert.Single(r.Errors);
        Assert.Equal(4, r.TotalCount);
    }

    [Fact]
    public void Highlights_substring_and_subsequence()
    {
        Assert.Equal([(6, 6)], Highlighter.Compute("final_report.docx", QueryParser.Parse("report")));
        Assert.Equal(6, Highlighter.Compute("quarterly_report_final.xlsx", QueryParser.Parse("qrtrly")).Sum(h => h.Length));
        Assert.Equal([(0, 3), (6, 3)], Highlighter.Compute("abcxyzdef", QueryParser.Parse("abc def")));
    }
}
