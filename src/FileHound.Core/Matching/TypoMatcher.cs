using FileHound.Core.Query;

namespace FileHound.Core.Matching;

/// <summary>
/// Typo-tolerant matching: the minimum edit distance between the term and any substring of the text,
/// computed with Myers' 1999 bit-parallel algorithm in O(n) for terms up to 64 chars.
/// </summary>
public static class TypoMatcher
{
    public static int MaxDistanceFor(int termLength) => termLength < 4 ? 0 : termLength <= 6 ? 1 : 2;

    public static int MinSubstringDistance(ReadOnlySpan<char> text, Term term)
    {
        int m = term.Text.Length;
        if (m == 0) return 0;
        if (m > 64 || term.PeqAscii is null) throw new ArgumentException("Term is not typo-eligible.", nameof(term));
        ulong pv = ~0UL, mv = 0;
        ulong high = 1UL << (m - 1);
        int score = m, best = m;
        foreach (char c in text)
        {
            ulong eq = term.Peq(c);
            ulong xv = eq | mv;
            ulong xh = (((eq & pv) + pv) ^ pv) | eq;
            ulong ph = mv | ~(xh | pv);
            ulong mh = pv & xh;
            if ((ph & high) != 0) score++;
            else if ((mh & high) != 0) score--;
            // Semi-global: the first row is all zeros, so no carry-in on the shift.
            ph <<= 1;
            mh <<= 1;
            pv = mh | ~(xv | ph);
            mv = ph & xv;
            if (score < best) { best = score; if (best == 0) break; }
        }
        return best;
    }
}
