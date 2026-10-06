using FileHound.Core.Index;
using FileHound.Core.Matching;
using FileHound.Core.Query;

namespace FileHound.Core.Search;

public enum SortMode : byte { Relevance, Name, Size, Modified }

public sealed record SearchRequest(
    string Text,
    SortMode Sort = SortMode.Relevance,
    int MaxResults = 5000,
    bool Fuzzy = true,
    bool IncludeHidden = true,
    FileCategory? Category = null);

/// <summary>One matching entry. <see cref="Key"/> is the packed sort key for the request's sort mode (higher = better).</summary>
public readonly record struct SearchHit(VolumeIndex Volume, int Entry, MatchTier Tier, int Score, long Key);

public sealed record SearchResult(
    IReadOnlyList<SearchHit> Hits,
    int TotalCount,
    TimeSpan Elapsed,
    IReadOnlyList<string> Errors,
    bool UsedTypoPass,
    SearchQuery? Query)
{
    public static readonly SearchResult Empty = new([], 0, TimeSpan.Zero, [], false, null);
}
