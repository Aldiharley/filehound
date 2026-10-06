namespace FileHound.Core.Index;

/// <summary>
/// 64-bit "which characters occur" bitmap used as a cheap prefilter for fuzzy and typo matching.
/// a-z => bits 0-25, 0-9 => bits 26-35, any other char => bit 36 + (c % 27).
/// A missing bit proves a character is absent; collisions only make the filter less strict.
/// </summary>
public static class CharMask
{
    public static ulong Of(ReadOnlySpan<char> fold)
    {
        ulong m = 0;
        foreach (char c in fold) m |= Bit(c);
        return m;
    }

    public static ulong Bit(char c)
    {
        if ((uint)(c - 'a') <= 'z' - 'a') return 1UL << (c - 'a');
        if ((uint)(c - '0') <= 9) return 1UL << (26 + c - '0');
        return 1UL << (36 + c % 27);
    }
}
