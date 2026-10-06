using FileHound.Core.Matching;
using FileHound.Core.Query;

namespace FileHound.Core.Tests;

public class MatcherTests
{
    private static MatchTier M(string name, string q, bool fuzzy = true, bool typo = false)
    {
        var t = QueryParser.Parse(q).PositiveTerms[0];
        var fold = name.ToLowerInvariant();
        return TermMatcher.Match(name, fold, CharMask.Of(fold), t, fuzzy, typo, out _);
    }

    [Theory]
    [InlineData("report", "report", MatchTier.Exact)]
    [InlineData("Report.PDF", "report", MatchTier.Exact)]
    [InlineData("reports2025.xlsx", "report", MatchTier.Prefix)]
    [InlineData("final_report.docx", "report", MatchTier.WordStart)]
    [InlineData("FinalReport.docx", "report", MatchTier.WordStart)]
    [InlineData("v2report.docx", "report", MatchTier.WordStart)]
    [InlineData("myreport.txt", "report", MatchTier.Substring)]
    [InlineData("quarterly_report_final.xlsx", "qrtrly", MatchTier.Subsequence)]
    [InlineData("Invoice_Scan_0412.pdf", "invce", MatchTier.Subsequence)]
    [InlineData("unrelated.txt", "report", MatchTier.None)]
    [InlineData("hello.txt", "*.txt", MatchTier.Exact)]
    public void Tiers(string name, string q, MatchTier expected) => Assert.Equal(expected, M(name, q));

    [Fact]
    public void Subsequence_off_when_not_fuzzy() => Assert.Equal(MatchTier.None, M("quarterly_report.xlsx", "qrtrly", fuzzy: false));

    [Fact]
    public void Typo_found_only_in_typo_pass()
    {
        Assert.Equal(MatchTier.None, M("quarterly_report.xlsx", "quartelry", typo: false));
        Assert.Equal(MatchTier.Typo, M("quarterly_report.xlsx", "quartelry", typo: true));
        Assert.Equal(MatchTier.Typo, M("budget_2025.xlsx", "budjet", typo: true));
    }

    [Fact]
    public void Typo_respects_max_distance() => Assert.Equal(MatchTier.None, M("budget.xlsx", "bxxgxt", typo: true));

    [Fact]
    public void Noise_guard_rejects_scattered_subsequence() =>
        Assert.Equal(MatchTier.None, M("a_very_long_name_with_random_letters_scattered_everywhere.txt", "anws"));

    [Theory]
    [InlineData("hello.txt", "*.txt", true)]
    [InlineData("hello.txt", "h?llo.*", true)]
    [InlineData("hello.txt", "*.doc", false)]
    [InlineData("abc", "a*b*c", true)]
    [InlineData("abc", "a*b*d", false)]
    [InlineData("abc", "abc*", true)]
    [InlineData("abc", "ab", false)]
    [InlineData("", "*", true)]
    [InlineData("aaab", "*a*b", true)]
    public void Wildcards(string text, string pattern, bool expected) => Assert.Equal(expected, WildcardMatcher.IsMatch(text, pattern));

    [Fact]
    public void Fzf_prefers_boundary_matches()
    {
        Assert.True(FuzzyScorer.TryMatchSubsequence("foo_bar.txt", "foo_bar.txt", "fb", out var s1, out _, out _));
        Assert.True(FuzzyScorer.TryMatchSubsequence("fxxbxx.txt", "fxxbxx.txt", "fb", out var s2, out _, out _));
        Assert.True(s1 > s2, $"{s1} vs {s2}");
    }

    [Fact]
    public void Fzf_prefers_consecutive_matches()
    {
        Assert.True(FuzzyScorer.TryMatchSubsequence("xabcx", "xabcx", "abc", out var tight, out _, out _));
        Assert.True(FuzzyScorer.TryMatchSubsequence("xaxbxcx", "xaxbxcx", "abc", out var loose, out _, out _));
        Assert.True(tight > loose);
    }

    [Fact]
    public void Subsequence_window_is_tightened_backwards()
    {
        Assert.True(FuzzyScorer.TryMatchSubsequence("a_x_ab", "a_x_ab", "ab", out _, out var start, out var end));
        Assert.Equal(4, start);
        Assert.Equal(6, end);
    }

    [Fact]
    public void Myers_matches_reference_dp()
    {
        var rnd = new Random(42);
        for (int i = 0; i < 3000; i++)
        {
            string text = RandomWord(rnd, rnd.Next(0, 30)), pat = RandomWord(rnd, rnd.Next(4, 12));
            var t = new Term(pat);
            Assert.Equal(ReferenceSemiGlobal(text, pat), TypoMatcher.MinSubstringDistance(text, t));
        }
    }

    [Fact]
    public void Myers_handles_64_char_pattern()
    {
        var pat = new string('a', 64);
        Assert.Equal(0, TypoMatcher.MinSubstringDistance("b" + pat + "b", new Term(pat)));
        Assert.Equal(1, TypoMatcher.MinSubstringDistance(new string('a', 63), new Term(pat)));
    }

    [Fact]
    public void Myers_handles_non_ascii()
    {
        var t = new Term("café");
        Assert.Equal(0, TypoMatcher.MinSubstringDistance("my café menu", t));
        Assert.Equal(1, TypoMatcher.MinSubstringDistance("my cafe menu", t));
    }

    [Theory]
    [InlineData(3, 0)]
    [InlineData(4, 1)]
    [InlineData(6, 1)]
    [InlineData(7, 2)]
    public void Max_distance_by_length(int len, int expected) => Assert.Equal(expected, TypoMatcher.MaxDistanceFor(len));

    private static string RandomWord(Random r, int n)
    {
        var c = new char[n];
        for (int i = 0; i < n; i++) c[i] = (char)('a' + r.Next(4));
        return new string(c);
    }

    private static int ReferenceSemiGlobal(string text, string pat)
    {
        var prev = new int[text.Length + 1];
        var cur = new int[text.Length + 1];
        for (int i = 1; i <= pat.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= text.Length; j++)
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (pat[i - 1] == text[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev.Min();
    }
}
