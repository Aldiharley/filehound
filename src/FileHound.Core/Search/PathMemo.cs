using FileHound.Core.Index;
using FileHound.Core.Query;

namespace FileHound.Core.Search;

/// <summary>
/// Per-search, per-volume memo for path matching. For each path-matching term it caches, per entry,
/// whether the entry's full path contains the term (0 = unknown, 1 = yes, 2 = no). Concurrent writers only
/// ever store the same computed value, so races are benign.
/// </summary>
internal sealed class PathMemo
{
    private readonly Dictionary<Term, byte[]> _memos = new(ReferenceEqualityComparer.Instance);

    public PathMemo(SearchQuery query, int count)
    {
        foreach (var clause in query.Clauses)
            foreach (var alt in clause.Alternatives)
            {
                var term = alt.Filter is PathFilter pf ? pf.Term : alt.Term is { Kind: TermKind.Path } t ? t : null;
                if (term is not null && !_memos.ContainsKey(term)) _memos[term] = new byte[count];
            }
    }

    public bool Matches(VolumeIndex v, int e, Term term)
    {
        var memo = _memos[term];
        if (e < memo.Length && memo[e] != 0) return memo[e] == 1;
        bool result = Compute(v, e, term, memo);
        return result;
    }

    private static bool Compute(VolumeIndex v, int e, Term term, byte[] memo)
    {
        // Walk up until an ancestor with a known value (or the root), then resolve downwards.
        var chain = new List<int>();
        int x = e;
        int guard = 0;
        bool known = false, value = false;
        while (true)
        {
            if (x < memo.Length && memo[x] != 0) { known = true; value = memo[x] == 1; break; }
            chain.Add(x);
            if (x == VolumeIndex.RootEntry || guard++ > 4096) break;
            x = v.Parent(x);
            if (x < 0) break;
        }

        bool parentValue = known && value;
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            int n = chain[i];
            bool r = n == VolumeIndex.RootEntry ? RootContains(v, term) : parentValue || SelfMatches(v, n, term);
            if (n < memo.Length) memo[n] = r ? (byte)1 : (byte)2;
            parentValue = r;
        }
        return parentValue;
    }

    private static bool RootContains(VolumeIndex v, Term term) =>
        v.FoldName(VolumeIndex.RootEntry).Contains(term.Text, StringComparison.Ordinal);

    /// <summary>True when the term occurs in the path in a way that involves this entry's own name.</summary>
    private static bool SelfMatches(VolumeIndex v, int n, Term term)
    {
        var fold = v.FoldName(n);
        if (term.Kind != TermKind.Path) return fold.Contains(term.Text, StringComparison.Ordinal);

        // term = A + '\' + B. The match must end inside this name: name starts with B, parent path ends with A.
        var text = term.Text.AsSpan();
        int slash = text.LastIndexOf('\\');
        var b = text[(slash + 1)..];
        var a = text[..slash];
        if (!fold.StartsWith(b, StringComparison.Ordinal)) return false;
        return ParentPathEndsWith(v, v.Parent(n), a);
    }

    private static bool ParentPathEndsWith(VolumeIndex v, int x, ReadOnlySpan<char> a)
    {
        int guard = 0;
        while (x >= 0 && guard++ < 4096)
        {
            var name = v.FoldName(x);
            if (x == VolumeIndex.RootEntry) return name.EndsWith(a, StringComparison.Ordinal);
            int slash = a.LastIndexOf('\\');
            if (slash < 0) return name.EndsWith(a, StringComparison.Ordinal);
            if (!name.SequenceEqual(a[(slash + 1)..])) return false;
            a = a[..slash];
            x = v.Parent(x);
        }
        return a.IsEmpty;
    }
}
