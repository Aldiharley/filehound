using System.Collections.Frozen;
using FileHound.Core.Index;

namespace FileHound.Core.Query;

public interface IClock { DateTimeOffset Now { get; } }

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset Now => DateTimeOffset.Now;
}

public enum TermKind : byte
{
    /// <summary>Matched against the name with tiered (exact → typo) matching.</summary>
    Plain,
    /// <summary>Contains * or ?; must match the whole name.</summary>
    Wildcard,
    /// <summary>Contains a backslash; matched against the full path.</summary>
    Path,
}

public enum KindConstraint : byte { None, File, Folder }

/// <summary>A single search word or phrase, pre-lowercased with matcher state precomputed.</summary>
public sealed class Term
{
    public Term(string text, KindConstraint constraint = KindConstraint.None)
    {
        Text = text.ToLowerInvariant();
        Constraint = constraint;
        Kind = Text.AsSpan().IndexOfAny('*', '?') >= 0 ? TermKind.Wildcard
             : Text.Contains('\\') ? TermKind.Path
             : TermKind.Plain;
        Mask = CharMask.Of(Text);
        LastSegment = Kind == TermKind.Path ? Text[(Text.LastIndexOf('\\') + 1)..] : Text;
        TypoEligible = Kind == TermKind.Plain && Text.Length is >= 4 and <= 64;
        MaxTypoDistance = !TypoEligible ? 0 : Text.Length <= 6 ? 1 : 2;
        if (TypoEligible) BuildPeq();
    }

    public string Text { get; }
    public TermKind Kind { get; }
    public KindConstraint Constraint { get; }
    public ulong Mask { get; }
    /// <summary>For path terms, the text after the final backslash (used as a cheap name prefilter).</summary>
    public string LastSegment { get; }
    public bool TypoEligible { get; }
    public int MaxTypoDistance { get; }

    // Myers pattern-equality bitmasks.
    internal ulong[]? PeqAscii { get; private set; }
    internal KeyValuePair<char, ulong>[]? PeqOther { get; private set; }

    internal ulong Peq(char c)
    {
        if (c < 128) return PeqAscii![c];
        foreach (var kv in PeqOther!) if (kv.Key == c) return kv.Value;
        return 0;
    }

    private void BuildPeq()
    {
        var ascii = new ulong[128];
        var other = new Dictionary<char, ulong>();
        for (int i = 0; i < Text.Length; i++)
        {
            char c = Text[i];
            if (c < 128) ascii[c] |= 1UL << i;
            else other[c] = other.GetValueOrDefault(c) | (1UL << i);
        }
        PeqAscii = ascii;
        PeqOther = other.ToArray();
    }

    public override string ToString() => Text;
}

public abstract record Filter;
public sealed record ExtFilter(FrozenSet<string> Extensions) : Filter;
public sealed record CategoryFilter(FileCategory[] Categories) : Filter;
/// <summary><c>Folders</c> true = folders only; false = files only.</summary>
public sealed record KindFilter(bool Folders) : Filter;
/// <summary>Inclusive size range in bytes.</summary>
public sealed record SizeFilter(long Min, long Max) : Filter;
/// <summary>Half-open UTC ticks range [Min, Max).</summary>
public sealed record DateFilter(long MinUtcTicks, long MaxUtcTicks) : Filter;
public sealed record DriveFilter(char Letter) : Filter;
public sealed record PathFilter(Term Term) : Filter;

/// <summary>Either a term or a filter.</summary>
public sealed class Alternative
{
    public Alternative(Term term) => Term = term;
    public Alternative(Filter filter) => Filter = filter;
    public Term? Term { get; }
    public Filter? Filter { get; }
    public bool IsPath => Filter is PathFilter || Term?.Kind == TermKind.Path;
}

/// <summary>One AND-ed clause made of OR-ed alternatives.</summary>
public sealed class Clause
{
    public Clause(List<Alternative> alternatives, bool negated)
    {
        Alternatives = alternatives;
        Negated = negated;
        IsFilterOnly = alternatives.TrueForAll(a => a.Filter is not null && a.Filter is not PathFilter);
        HasPath = alternatives.Exists(a => a.IsPath);
    }

    public List<Alternative> Alternatives { get; }
    public bool Negated { get; }
    public bool IsFilterOnly { get; }
    public bool HasPath { get; }
}

public sealed class SearchQuery
{
    public SearchQuery(List<Clause> clauses, List<string> errors)
    {
        // Evaluation order: cheap filter-only clauses, then name terms, then path clauses.
        Clauses = clauses.OrderBy(c => c.IsFilterOnly ? 0 : c.HasPath ? 2 : 1).ToList();
        Errors = errors;
        PositiveTerms = clauses.Where(c => !c.Negated)
            .SelectMany(c => c.Alternatives)
            .Where(a => a.Term is { Kind: not TermKind.Path })
            .Select(a => a.Term!)
            .ToList();
    }

    public List<Clause> Clauses { get; }
    public List<string> Errors { get; }
    public IReadOnlyList<Term> PositiveTerms { get; }
    public bool IsEmpty => Clauses.Count == 0;
    /// <summary>True when there is at least one positive clause with a name term (so ranking by match quality applies).</summary>
    public bool HasNameTerms => PositiveTerms.Count > 0;
}
