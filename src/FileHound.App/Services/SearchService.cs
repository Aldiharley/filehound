using FileHound.App.ViewModels;

namespace FileHound.App.Services;

public sealed record SearchOutcome(SearchResult Result, IReadOnlyList<ResultItem> Items);

/// <summary>Debounced, cancellable search over the live indexes; results are materialised off the UI thread.</summary>
public sealed class SearchService(IndexManager manager)
{
    private readonly SearchEngine _engine = new();
    private CancellationTokenSource? _cts;

    /// <summary>Runs a search after <paramref name="debounceMs"/>; returns null when superseded by a newer call.</summary>
    public async Task<SearchOutcome?> SearchAsync(SearchRequest request, int debounceMs = 40)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        try
        {
            if (debounceMs > 0) await Task.Delay(debounceMs, cts.Token);
            var volumes = manager.Volumes;
            return await Task.Run(() =>
            {
                var result = _engine.Search(volumes, request, cts.Token);
                var items = Materialize(result, cts.Token);
                return new SearchOutcome(result, items);
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static IReadOnlyList<ResultItem> Materialize(SearchResult result, CancellationToken ct)
    {
        var items = new ResultItem[result.Hits.Count];
        var now = DateTime.UtcNow;
        for (int i = 0; i < items.Length; i++)
        {
            if ((i & 255) == 0) ct.ThrowIfCancellationRequested();
            var h = result.Hits[i];
            items[i] = ResultItem.From(h.Volume, h.Entry, result.Query, now);
        }
        return items;
    }
}
