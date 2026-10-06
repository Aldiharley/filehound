namespace FileHound.Core.Matching;

/// <summary>
/// fzf-style fuzzy scoring (FuzzyMatchV1: greedy forward scan, backward tightening, bonus-based scoring).
/// Scoring constants and the bonus model are adapted from fzf (https://github.com/junegunn/fzf, MIT, © Junegunn Choi).
/// </summary>
public static class FuzzyScorer
{
    private const int ScoreMatch = 16;
    private const int ScoreGapStart = -3;
    private const int ScoreGapExtension = -1;
    private const int BonusBoundary = ScoreMatch / 2;                         // 8
    private const int BonusNonWord = ScoreMatch / 2;                          // 8
    private const int BonusCamel123 = BonusBoundary + ScoreGapExtension;      // 7
    private const int BonusConsecutive = -(ScoreGapStart + ScoreGapExtension); // 4
    private const int BonusFirstCharMultiplier = 2;
    private const int BonusBoundaryWhite = BonusBoundary + 2;                 // 10
    private const int BonusBoundaryDelimiter = BonusBoundary + 1;             // 9

    private enum CharClass : byte { White, NonWord, Delimiter, Lower, Upper, Letter, Number }

    private static CharClass ClassOf(char c)
    {
        if (char.IsAsciiLetterLower(c)) return CharClass.Lower;
        if (char.IsAsciiLetterUpper(c)) return CharClass.Upper;
        if (char.IsAsciiDigit(c)) return CharClass.Number;
        switch (c)
        {
            case ' ' or '\t': return CharClass.White;
            case '\\' or '/' or ',' or ':' or ';' or '|' or '_' or '-' or '.': return CharClass.Delimiter;
        }
        if (char.IsLower(c)) return CharClass.Lower;
        if (char.IsUpper(c)) return CharClass.Upper;
        if (char.IsLetter(c)) return CharClass.Letter;
        if (char.IsDigit(c)) return CharClass.Number;
        return CharClass.NonWord;
    }

    private static int BonusFor(CharClass prev, CharClass cls)
    {
        if (cls > CharClass.NonWord)
        {
            switch (prev)
            {
                case CharClass.White: return BonusBoundaryWhite;
                case CharClass.Delimiter: return BonusBoundaryDelimiter;
                case CharClass.NonWord: return BonusBoundary;
            }
        }
        if ((prev == CharClass.Lower && cls == CharClass.Upper) || (prev != CharClass.Number && cls == CharClass.Number))
            return BonusCamel123;
        return cls switch
        {
            CharClass.NonWord or CharClass.Delimiter => BonusNonWord,
            CharClass.White => BonusBoundaryWhite,
            _ => 0,
        };
    }

    /// <summary>
    /// Scores the pattern against <paramref name="fold"/>[start..end). <paramref name="name"/> (original case) is used
    /// for character classes. When <paramref name="positions"/> is large enough, matched indexes are written to it.
    /// </summary>
    public static int ScoreWindow(ReadOnlySpan<char> name, ReadOnlySpan<char> fold, ReadOnlySpan<char> pattern, int start, int end, Span<int> positions = default)
    {
        int pidx = 0, score = 0, consecutive = 0, firstBonus = 0;
        bool inGap = false;
        var prevClass = start > 0 ? ClassOf(name[start - 1]) : CharClass.White;
        for (int idx = start; idx < end && pidx < pattern.Length; idx++)
        {
            var cls = ClassOf(name[idx]);
            if (fold[idx] == pattern[pidx])
            {
                if (pidx < positions.Length) positions[pidx] = idx;
                score += ScoreMatch;
                int bonus = BonusFor(prevClass, cls);
                if (consecutive == 0) firstBonus = bonus;
                else
                {
                    if (bonus >= BonusBoundary && bonus > firstBonus) firstBonus = bonus;
                    bonus = Math.Max(Math.Max(bonus, firstBonus), BonusConsecutive);
                }
                score += pidx == 0 ? bonus * BonusFirstCharMultiplier : bonus;
                inGap = false;
                consecutive++;
                pidx++;
            }
            else
            {
                score += inGap ? ScoreGapExtension : ScoreGapStart;
                inGap = true;
                consecutive = 0;
                firstBonus = 0;
            }
            prevClass = cls;
        }
        return score;
    }

    /// <summary>Finds the pattern as a subsequence of <paramref name="fold"/> and scores the tightest window.</summary>
    public static bool TryMatchSubsequence(ReadOnlySpan<char> name, ReadOnlySpan<char> fold, ReadOnlySpan<char> pattern, out int score, out int start, out int end)
    {
        score = 0; start = -1; end = -1;
        if (pattern.IsEmpty || pattern.Length > fold.Length) return false;
        int pidx = 0;
        for (int idx = 0; idx < fold.Length; idx++)
        {
            if (fold[idx] == pattern[pidx])
            {
                if (pidx == 0) start = idx;
                if (++pidx == pattern.Length) { end = idx + 1; break; }
            }
        }
        if (end < 0) return false;
        pidx = pattern.Length - 1;
        for (int idx = end - 1; idx >= start; idx--)
        {
            if (fold[idx] == pattern[pidx] && --pidx < 0) { start = idx; break; }
        }
        score = ScoreWindow(name, fold, pattern, start, end);
        return true;
    }
}
