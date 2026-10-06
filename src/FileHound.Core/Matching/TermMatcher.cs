using System.Numerics;
using System.Runtime.CompilerServices;
using FileHound.Core.Query;

namespace FileHound.Core.Matching;

/// <summary>Classifies how a single term matches a file name into a <see cref="MatchTier"/> plus an in-tier score.</summary>
public static class TermMatcher
{
    /// <summary>
    /// Matches <paramref name="t"/> against a name. <paramref name="fold"/> is the lowercase name and
    /// <paramref name="nameMask"/> its <see cref="Index.CharMask"/>. Path terms are not handled here.
    /// </summary>
    public static MatchTier Match(ReadOnlySpan<char> name, ReadOnlySpan<char> fold, ulong nameMask, Term t, bool fuzzy, bool typo, out int score)
    {
        score = 0;
        var pattern = t.Text.AsSpan();
        switch (t.Kind)
        {
            case TermKind.Wildcard:
                return WildcardMatcher.IsMatch(fold, pattern) ? MatchTier.Exact : MatchTier.None;
            case TermKind.Path:
                return MatchTier.None;
        }

        int m = pattern.Length;
        if ((t.Mask & ~nameMask) == 0 && m <= fold.Length)
        {
            int idx = fold.IndexOf(pattern, StringComparison.Ordinal);
            if (idx >= 0)
            {
                if (idx == 0)
                {
                    score = FuzzyScorer.ScoreWindow(name, fold, pattern, 0, m);
                    if (m == fold.Length || fold.LastIndexOf('.') == m) return MatchTier.Exact;
                    return MatchTier.Prefix;
                }
                // Prefer the first occurrence that starts at a word boundary.
                for (int at = idx; at >= 0;)
                {
                    if (IsWordBoundary(name, at))
                    {
                        score = FuzzyScorer.ScoreWindow(name, fold, pattern, at, at + m);
                        return MatchTier.WordStart;
                    }
                    int next = fold[(at + 1)..].IndexOf(pattern, StringComparison.Ordinal);
                    at = next < 0 ? -1 : at + 1 + next;
                }
                score = FuzzyScorer.ScoreWindow(name, fold, pattern, idx, idx + m);
                return MatchTier.Substring;
            }

            if (fuzzy && m >= 2 && FuzzyScorer.TryMatchSubsequence(name, fold, pattern, out int s, out int start, out int end))
            {
                // Noise guard: the matched window may be at most about twice the term length.
                if (end - start <= 2 * m + 3)
                {
                    score = s;
                    return MatchTier.Subsequence;
                }
            }
        }

        if (typo && t.TypoEligible)
        {
            int k = t.MaxTypoDistance;
            if (BitOperations.PopCount(t.Mask & ~nameMask) <= k && fold.Length >= m - k)
            {
                int d = TypoMatcher.MinSubstringDistance(fold, t);
                if (d <= k)
                {
                    score = 100 - 30 * d;
                    return MatchTier.Typo;
                }
            }
        }
        return MatchTier.None;
    }

    /// <summary>True when position <paramref name="index"/> starts a "word" in the original-case name.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsWordBoundary(ReadOnlySpan<char> name, int index)
    {
        if (index <= 0) return true;
        char prev = name[index - 1], cur = name[index];
        switch (prev)
        {
            case ' ' or '_' or '-' or '.' or '(' or '[' or '{' or ',' or ';' or '+' or '@' or '#' or '~' or '\\' or '/':
                return true;
        }
        if (char.IsLower(prev) && char.IsUpper(cur)) return true;
        if (char.IsLetter(prev) && char.IsDigit(cur)) return true;
        if (char.IsDigit(prev) && char.IsLetter(cur)) return true;
        return false;
    }
}
