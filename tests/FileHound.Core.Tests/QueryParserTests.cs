using FileHound.Core.Query;

namespace FileHound.Core.Tests;

public class QueryParserTests
{
    private sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset Now => now; }
    private static readonly IClock Clock = new FixedClock(new DateTimeOffset(2026, 10, 7, 15, 0, 0, TimeSpan.Zero));

    private static Term T(SearchQuery q, int clause, int alt = 0) => q.Clauses[clause].Alternatives[alt].Term!;
    private static Filter F(SearchQuery q, int clause, int alt = 0) => q.Clauses[clause].Alternatives[alt].Filter!;

    [Fact] public void Empty_query() => Assert.True(QueryParser.Parse("   ").IsEmpty);

    [Fact]
    public void Words_are_and_clauses()
    {
        var q = QueryParser.Parse("Budget 2025");
        Assert.Equal(2, q.Clauses.Count);
        Assert.Equal("budget", T(q, 0).Text);
        Assert.False(q.IsEmpty);
    }

    [Fact] public void Quotes_keep_spaces() => Assert.Equal("my file", T(QueryParser.Parse("\"My File\""), 0).Text);

    [Fact] public void Quoted_pipe_is_literal() => Assert.Equal("a|b", T(QueryParser.Parse("\"a|b\""), 0).Text);

    [Fact]
    public void Pipe_makes_alternatives()
    {
        var q = QueryParser.Parse("a b|c");
        Assert.Equal(2, q.Clauses.Count);
        Assert.Equal(2, q.Clauses[1].Alternatives.Count);
        Assert.Equal("c", T(q, 1, 1).Text);
    }

    [Fact]
    public void Spaced_pipe_makes_alternatives()
    {
        var q = QueryParser.Parse("b | c");
        Assert.Single(q.Clauses);
        Assert.Equal(2, q.Clauses[0].Alternatives.Count);
    }

    [Fact]
    public void Bang_negates()
    {
        var q = QueryParser.Parse("report !tmp");
        Assert.True(q.Clauses[1].Negated);
        Assert.Single(q.PositiveTerms);
    }

    [Fact] public void Wildcard_term() => Assert.Equal(TermKind.Wildcard, T(QueryParser.Parse("*.log"), 0).Kind);

    [Fact]
    public void Backslash_term_is_path()
    {
        var t = T(QueryParser.Parse(@"src\Core"), 0);
        Assert.Equal(TermKind.Path, t.Kind);
        Assert.Equal("core", t.LastSegment);
    }

    [Fact]
    public void Ext_filter_list()
    {
        var f = (ExtFilter)F(QueryParser.Parse("ext:PDF;.docx"), 0);
        Assert.Contains("pdf", (IReadOnlySet<string>)f.Extensions);
        Assert.Contains("docx", (IReadOnlySet<string>)f.Extensions);
        Assert.True(QueryParser.Parse("ext:pdf").Clauses[0].IsFilterOnly);
    }

    [Fact]
    public void Folder_with_term_constrains_term()
    {
        var q = QueryParser.Parse("folder:node_modules");
        Assert.Single(q.Clauses);
        Assert.Equal("node_modules", T(q, 0).Text);
        Assert.Equal(KindConstraint.Folder, T(q, 0).Constraint);
        Assert.Equal(KindConstraint.File, T(QueryParser.Parse("file:abc"), 0).Constraint);
    }

    [Fact]
    public void Folder_alone_is_kind_filter()
    {
        var f = (KindFilter)F(QueryParser.Parse("folder:"), 0);
        Assert.True(f.Folders);
        Assert.False(((KindFilter)F(QueryParser.Parse("file:"), 0)).Folders);
    }

    [Fact] public void Type_filter() => Assert.Equal(FileCategory.Image, ((CategoryFilter)F(QueryParser.Parse("type:pic"), 0)).Categories[0]);

    [Fact] public void Unknown_type_is_error() => Assert.Single(QueryParser.Parse("type:banana").Errors);

    [Theory]
    [InlineData(">10mb", 10L * 1024 * 1024 + 1, long.MaxValue)]
    [InlineData(">=1kb", 1024, long.MaxValue)]
    [InlineData("<2kb", 0, 2047)]
    [InlineData("<=2kb", 0, 2048)]
    [InlineData("1mb..1gb", 1048576, 1073741824)]
    [InlineData("1.5kb", 1536, 1536)]
    [InlineData("empty", 0, 0)]
    [InlineData("tiny", 0, 10239)]
    [InlineData("gigantic", 134217728, long.MaxValue)]
    [InlineData("500", 500, 500)]
    public void Size_grammar(string s, long min, long max)
    {
        Assert.True(SizeParser.TryParse(s, out var a, out var b));
        Assert.Equal(min, a);
        Assert.Equal(max, b);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData(">")]
    [InlineData("10xb")]
    public void Size_rejects_garbage(string s) => Assert.False(SizeParser.TryParse(s, out _, out _));

    [Fact]
    public void Bad_size_reports_error_and_drops_filter()
    {
        var q = QueryParser.Parse("size:abc report");
        Assert.Single(q.Errors);
        Assert.Single(q.Clauses);
    }

    [Fact]
    public void Dm_today_range()
    {
        Assert.True(DateRangeParser.TryParse("today", Clock, out var min, out var max));
        Assert.Equal(TimeSpan.TicksPerDay, max - min);
        var localMidnight = new DateTime(Clock.Now.ToLocalTime().Date.Ticks, DateTimeKind.Local).ToUniversalTime().Ticks;
        Assert.Equal(localMidnight, min);
    }

    [Fact]
    public void Dm_year()
    {
        Assert.True(DateRangeParser.TryParse("2025", Clock, out var min, out var max));
        Assert.Equal(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Local).ToUniversalTime().Ticks, min);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local).ToUniversalTime().Ticks, max);
    }

    [Fact]
    public void Dm_month_and_day()
    {
        Assert.True(DateRangeParser.TryParse("2026-02", Clock, out var min, out var max));
        Assert.Equal(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Local).ToUniversalTime().Ticks, max);
        Assert.True(DateRangeParser.TryParse("2026-02-03", Clock, out min, out max));
        Assert.Equal(TimeSpan.TicksPerDay, max - min);
    }

    [Fact]
    public void Dm_greater_than_date()
    {
        Assert.True(DateRangeParser.TryParse(">2026-01-01", Clock, out var min, out var max));
        Assert.Equal(long.MaxValue, max);
        Assert.Equal(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Local).ToUniversalTime().Ticks, min);
    }

    [Fact]
    public void Dm_week_is_rolling_seven_days()
    {
        Assert.True(DateRangeParser.TryParse("week", Clock, out var min, out _));
        Assert.Equal(Clock.Now.UtcTicks - 7 * TimeSpan.TicksPerDay, min);
    }

    [Fact] public void Dm_garbage_fails() => Assert.False(DateRangeParser.TryParse("someday", Clock, out _, out _));

    [Theory]
    [InlineData("9999")]
    [InlineData("9999-12")]
    [InlineData("9999-12-31")]
    [InlineData(">9999-12-31")]
    public void Dm_end_of_calendar_does_not_throw(string s)
    {
        var ex = Record.Exception(() => QueryParser.Parse("dm:" + s, Clock));
        Assert.Null(ex);
    }

    [Fact] public void Drive_filter() => Assert.Equal('E', ((DriveFilter)F(QueryParser.Parse("drive:e:"), 0)).Letter);

    [Fact]
    public void Path_filter()
    {
        var f = (PathFilter)F(QueryParser.Parse("path:Projects"), 0);
        Assert.Equal("projects", f.Term.Text);
        Assert.True(QueryParser.Parse("path:Projects").Clauses[0].HasPath);
    }

    [Fact] public void Unknown_prefix_is_plain_term() => Assert.Equal("http:x", T(QueryParser.Parse("http:x"), 0).Text);

    [Fact]
    public void Typo_eligibility()
    {
        var q = QueryParser.Parse("abc quartelry *.txt");
        Assert.False(q.PositiveTerms[0].TypoEligible);
        Assert.True(q.PositiveTerms[1].TypoEligible);
        Assert.Equal(2, q.PositiveTerms[1].MaxTypoDistance);
        Assert.False(q.PositiveTerms[2].TypoEligible);
    }
}
