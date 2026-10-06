using System.Collections.Concurrent;
using System.Diagnostics;
using FileHound.Core.Index;
using FileHound.Core.Matching;
using FileHound.Core.Query;

namespace FileHound.Core.Search;

/// <summary>
/// Parallel brute-force search over one or more <see cref="VolumeIndex"/>es with tiered fuzzy ranking.
/// Pass 1 allows exact → subsequence tiers; when it finds fewer than <see cref="TypoPassThreshold"/> hits a second
/// pass adds typo-tolerant matching (its result set is a superset of pass 1).
/// </summary>
public sealed class SearchEngine(IClock? clock = null)
{
    public const int ChunkSize = 32_768;
    public const int TypoPassThreshold = 200;
    private const long DaysEpochTicks = 630822816000000000; // 2000-01-01

    private readonly IClock _clock = clock ?? SystemClock.Instance;

    public SearchResult Search(IReadOnlyList<VolumeIndex> volumes, SearchRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        var query = QueryParser.Parse(request.Text, _clock);
        if (query.IsEmpty && request.Category is null)
            return SearchResult.Empty with { Errors = query.Errors, Query = query };

        bool typoEligible = request.Fuzzy && query.PositiveTerms.Any(t => t.TypoEligible);
        var (hits, total) = Run(volumes, query, request, typo: false, ct);
        bool usedTypo = false;
        if (typoEligible && total < TypoPassThreshold)
        {
            var (hits2, total2) = Run(volumes, query, request, typo: true, ct);
            if (total2 > total) { hits = hits2; total = total2; usedTypo = true; }
        }
        return new SearchResult(hits, total, sw.Elapsed, query.Errors, usedTypo, query);
    }

    private readonly record struct Chunk(VolumeIndex Volume, int Start, int End, PathMemo? Memo);

    private sealed class LocalState<TRanker>(int capacity) where TRanker : struct, IRanker<SearchHit>
    {
        public readonly BoundedHeap<SearchHit, TRanker> Heap = new(capacity, default);
        public int Count;
    }

    private (IReadOnlyList<SearchHit> Hits, int Total) Run(IReadOnlyList<VolumeIndex> volumes, SearchQuery query, SearchRequest request, bool typo, CancellationToken ct) =>
        request.Sort == SortMode.Name
            ? Run<NameRanker>(volumes, query, request, typo, ct)
            : Run<KeyRanker>(volumes, query, request, typo, ct);

    private (IReadOnlyList<SearchHit> Hits, int Total) Run<TRanker>(IReadOnlyList<VolumeIndex> volumes, SearchQuery query, SearchRequest request, bool typo, CancellationToken ct)
        where TRanker : struct, IRanker<SearchHit>
    {
        var chunks = new List<Chunk>();
        bool needsMemo = query.Clauses.Exists(c => c.HasPath);
        foreach (var v in volumes)
        {
            int count = v.Count;
            var memo = needsMemo ? new PathMemo(query, count) : null;
            for (int s = 1; s < count; s += ChunkSize) chunks.Add(new Chunk(v, s, Math.Min(count, s + ChunkSize), memo));
        }

        int capacity = Math.Max(1, request.MaxResults);
        var locals = new ConcurrentBag<LocalState<TRanker>>();
        var ctx = new EvalContext(query, request, typo);
        var options = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount };

        Parallel.For(0, chunks.Count, options,
            () => new LocalState<TRanker>(capacity),
            (i, _, local) =>
            {
                var chunk = chunks[i];
                var v = chunk.Volume;
                v.Lock.EnterReadLock();
                try
                {
                    int end = Math.Min(chunk.End, v.Count);
                    for (int e = chunk.Start; e < end; e++)
                    {
                        if (Evaluate(v, e, ctx, chunk.Memo, out var tier, out int score, out bool nameMatched))
                        {
                            local.Count++;
                            long key = KeyFor(v, e, request.Sort, tier, score, nameMatched);
                            local.Heap.Offer(new SearchHit(v, e, tier, score, key));
                        }
                    }
                }
                finally { v.Lock.ExitReadLock(); }
                return local;
            },
            locals.Add);

        int total = 0;
        var merged = new BoundedHeap<SearchHit, TRanker>(capacity, default);
        foreach (var l in locals)
        {
            total += l.Count;
            foreach (ref readonly var hit in l.Heap.Items) merged.Offer(hit);
        }
        return (merged.ToSortedList(), total);
    }

    private sealed class EvalContext(SearchQuery query, SearchRequest request, bool typo)
    {
        public readonly SearchQuery Query = query;
        public readonly bool Fuzzy = request.Fuzzy;
        public readonly bool Typo = typo;
        public readonly bool IncludeHidden = request.IncludeHidden;
        public readonly FileCategory? Category = request.Category;
    }

    private static bool Evaluate(VolumeIndex v, int e, EvalContext ctx, PathMemo? memo, out MatchTier tier, out int score, out bool nameMatched)
    {
        tier = MatchTier.Exact;
        score = 0;
        nameMatched = false;
        var flags = v.Flags(e);
        if ((flags & EntryFlags.Deleted) != 0) return false;
        if (!ctx.IncludeHidden && (flags & (EntryFlags.Hidden | EntryFlags.System)) != 0) return false;
        if (ctx.Category is { } cat && v.Category(e) != cat) return false;

        var clauses = ctx.Query.Clauses;
        ReadOnlySpan<char> name = default, fold = default;
        ulong mask = 0;
        bool loaded = false;

        for (int c = 0; c < clauses.Count; c++)
        {
            var clause = clauses[c];
            bool any = false;
            var bestTier = MatchTier.None;
            int bestScore = int.MinValue;
            var alts = clause.Alternatives;
            for (int a = 0; a < alts.Count; a++)
            {
                var alt = alts[a];
                if (alt.Filter is { } f)
                {
                    if (f is PathFilter pf)
                    {
                        if (memo!.Matches(v, e, pf.Term)) any = true;
                    }
                    else if (FilterMatches(f, v, e, flags)) any = true;
                    if (any && clause.Negated) break;
                    continue;
                }

                var term = alt.Term!;
                if (term.Constraint == KindConstraint.Folder && (flags & EntryFlags.Directory) == 0) continue;
                if (term.Constraint == KindConstraint.File && (flags & EntryFlags.Directory) != 0) continue;
                if (term.Kind == TermKind.Path)
                {
                    if (memo!.Matches(v, e, term)) any = true;
                    continue;
                }
                if (!loaded) { name = v.Name(e); fold = v.FoldName(e); mask = v.Mask(e); loaded = true; }
                bool allowFuzzy = ctx.Fuzzy && !clause.Negated;
                var t = TermMatcher.Match(name, fold, mask, term, allowFuzzy, ctx.Typo && allowFuzzy, out int s);
                if (t == MatchTier.None) continue;
                any = true;
                if (t < bestTier || (t == bestTier && s > bestScore)) { bestTier = t; bestScore = s; }
            }

            if (clause.Negated)
            {
                if (any) return false;
                continue;
            }
            if (!any) return false;
            if (bestTier != MatchTier.None)
            {
                nameMatched = true;
                if (bestTier > tier) tier = bestTier;
                score += bestScore;
            }
        }
        return true;
    }

    private static bool FilterMatches(Filter f, VolumeIndex v, int e, EntryFlags flags)
    {
        bool isDir = (flags & EntryFlags.Directory) != 0;
        switch (f)
        {
            case ExtFilter ext:
                if (isDir) return false;
                var x = Categorizer.Extension(v.FoldName(e));
                return !x.IsEmpty && ext.Lookup.Contains(x);
            case CategoryFilter cat:
                return Array.IndexOf(cat.Categories, v.Category(e)) >= 0;
            case KindFilter kind:
                return isDir == kind.Folders;
            case SizeFilter size:
                if (isDir || (flags & EntryFlags.MetadataKnown) == 0) return false;
                long s = v.Size(e);
                return s >= size.Min && s <= size.Max;
            case DateFilter date:
                if ((flags & EntryFlags.MetadataKnown) == 0) return false;
                long m = v.ModifiedTicks(e);
                return m >= date.MinUtcTicks && m < date.MaxUtcTicks;
            case DriveFilter drive:
                return v.DriveLetter == drive.Letter;
            default:
                return false;
        }
    }

    private static long KeyFor(VolumeIndex v, int e, SortMode sort, MatchTier tier, int score, bool nameMatched)
    {
        switch (sort)
        {
            case SortMode.Size:
                return (v.Flags(e) & EntryFlags.MetadataKnown) != 0 ? v.Size(e) : -1;
            case SortMode.Modified:
                return v.ModifiedTicks(e);
            case SortMode.Name:
                return 0;
        }
        long t = 255 - (byte)tier;
        long sc = Math.Clamp(score + 2048, 0, 4095);
        long nb = nameMatched ? 1 : 0;
        long len = 255 - Math.Min(v.Name(e).Length, 255);
        long depth = 255 - v.Depth(e);
        long days = Math.Clamp((v.ModifiedTicks(e) - DaysEpochTicks) / TimeSpan.TicksPerDay, 0, (1 << 23) - 1);
        return (t << 52) | (sc << 40) | (nb << 39) | (len << 31) | (depth << 23) | days;
    }

    /// <summary>Higher packed key ranks better (relevance, size and modified sorts).</summary>
    private struct KeyRanker : IRanker<SearchHit>
    {
        public readonly int Better(in SearchHit a, in SearchHit b)
        {
            int c = a.Key.CompareTo(b.Key);
            return c != 0 ? c : TieBreak(a, b);
        }
    }

    /// <summary>Alphabetically earlier (case-insensitive) ranks better.</summary>
    private struct NameRanker : IRanker<SearchHit>
    {
        public readonly int Better(in SearchHit a, in SearchHit b)
        {
            int c = b.Volume.FoldName(b.Entry).SequenceCompareTo(a.Volume.FoldName(a.Entry));
            return c != 0 ? c : TieBreak(a, b);
        }
    }

    /// <summary>Deterministic ordering: earlier drive letter, then lower entry id ranks higher.</summary>
    private static int TieBreak(in SearchHit a, in SearchHit b)
    {
        int c = b.Volume.DriveLetter.CompareTo(a.Volume.DriveLetter);
        if (c != 0) return c;
        c = b.Volume.Root.Length.CompareTo(a.Volume.Root.Length);
        return c != 0 ? c : b.Entry.CompareTo(a.Entry);
    }
}
