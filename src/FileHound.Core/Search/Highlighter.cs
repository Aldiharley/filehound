using FileHound.Core.Matching;
using FileHound.Core.Query;

namespace FileHound.Core.Search;

/// <summary>Computes which characters of a result name to highlight for a query (merged, sorted ranges).</summary>
public static class Highlighter
{
    public static IReadOnlyList<(int Start, int Length)> Compute(string name, SearchQuery query)
    {
        if (query.PositiveTerms.Count == 0 || string.IsNullOrEmpty(name)) return [];
        var fold = name.ToLowerInvariant().AsSpan();
        var marks = new bool[name.Length];
        Span<int> positions = stackalloc int[64];
        foreach (var term in query.PositiveTerms)
        {
            if (term.Kind != TermKind.Plain) continue;
            var pattern = term.Text.AsSpan();
            int idx = fold.IndexOf(pattern, StringComparison.Ordinal);
            if (idx >= 0)
            {
                int best = idx;
                for (int at = idx; at >= 0;)
                {
                    if (TermMatcher.IsWordBoundary(name, at)) { best = at; break; }
                    int next = fold[(at + 1)..].IndexOf(pattern, StringComparison.Ordinal);
                    at = next < 0 ? -1 : at + 1 + next;
                }
                marks.AsSpan(best, pattern.Length).Fill(true);
                continue;
            }
            if (pattern.Length <= positions.Length && FuzzyScorer.TryMatchSubsequence(name, fold, pattern, out _, out int start, out int end))
            {
                var pos = positions[..pattern.Length];
                FuzzyScorer.ScoreWindow(name, fold, pattern, start, end, pos);
                foreach (int p in pos) marks[p] = true;
            }
        }

        var ranges = new List<(int, int)>();
        for (int i = 0; i < marks.Length;)
        {
            if (!marks[i]) { i++; continue; }
            int s = i;
            while (i < marks.Length && marks[i]) i++;
            ranges.Add((s, i - s));
        }
        return ranges;
    }
}
