namespace FileHound.Core.Matching;

/// <summary>How well a term matched a name. Lower is better; ranking compares tier first.</summary>
public enum MatchTier : byte
{
    Exact = 0,
    Prefix = 1,
    WordStart = 2,
    Substring = 3,
    Subsequence = 4,
    Typo = 5,
    None = 255,
}
