namespace FileHound.Core.Search;

/// <summary>Ranks two items: &gt; 0 when <paramref name="a"/> is better than <paramref name="b"/>.</summary>
internal interface IRanker<T>
{
    int Better(in T a, in T b);
}

/// <summary>
/// Keeps the best <c>capacity</c> items seen. Internally a min-heap whose root is the worst retained item.
/// Generic over a struct ranker so comparisons are devirtualised and inlined by the JIT.
/// </summary>
internal sealed class BoundedHeap<T, TRanker>(int capacity, TRanker ranker) where TRanker : struct, IRanker<T>
{
    private readonly T[] _items = new T[Math.Max(1, capacity)];
    private TRanker _ranker = ranker;
    private int _count;

    public int Count => _count;
    public bool IsFull => _count == _items.Length;
    public ref readonly T Worst => ref _items[0];
    public ReadOnlySpan<T> Items => _items.AsSpan(0, _count);

    public void Offer(in T item)
    {
        if (_count < _items.Length)
        {
            _items[_count] = item;
            SiftUp(_count++);
        }
        else if (_ranker.Better(item, _items[0]) > 0)
        {
            _items[0] = item;
            SiftDown(0);
        }
    }

    /// <summary>Returns the retained items, best first.</summary>
    public List<T> ToSortedList()
    {
        var list = new List<T>(_items.AsSpan(0, _count).ToArray());
        var r = _ranker;
        list.Sort((a, b) => r.Better(b, a));
        return list;
    }

    private void SiftUp(int i)
    {
        while (i > 0)
        {
            int p = (i - 1) >> 1;
            if (_ranker.Better(_items[p], _items[i]) <= 0) break;
            (_items[p], _items[i]) = (_items[i], _items[p]);
            i = p;
        }
    }

    private void SiftDown(int i)
    {
        while (true)
        {
            int l = 2 * i + 1, r = l + 1, worst = i;
            if (l < _count && _ranker.Better(_items[worst], _items[l]) > 0) worst = l;
            if (r < _count && _ranker.Better(_items[worst], _items[r]) > 0) worst = r;
            if (worst == i) return;
            (_items[worst], _items[i]) = (_items[i], _items[worst]);
            i = worst;
        }
    }
}
