namespace FileHound.Core.Search;

/// <summary>
/// Keeps the best <c>capacity</c> items seen. <paramref name="better"/> returns &gt; 0 when the first argument ranks
/// higher. Internally a min-heap whose root is the worst retained item.
/// </summary>
internal sealed class BoundedHeap<T>(int capacity, Comparison<T> better)
{
    private readonly T[] _items = new T[Math.Max(1, capacity)];
    private int _count;

    public int Count => _count;
    public ReadOnlySpan<T> Items => _items.AsSpan(0, _count);

    public void Offer(in T item)
    {
        if (_count < _items.Length)
        {
            _items[_count] = item;
            SiftUp(_count++);
        }
        else if (better(item, _items[0]) > 0)
        {
            _items[0] = item;
            SiftDown(0);
        }
    }

    private void SiftUp(int i)
    {
        while (i > 0)
        {
            int p = (i - 1) >> 1;
            if (better(_items[p], _items[i]) <= 0) break; // parent already worse-or-equal
            (_items[p], _items[i]) = (_items[i], _items[p]);
            i = p;
        }
    }

    private void SiftDown(int i)
    {
        while (true)
        {
            int l = 2 * i + 1, r = l + 1, worst = i;
            if (l < _count && better(_items[worst], _items[l]) > 0) worst = l;
            if (r < _count && better(_items[worst], _items[r]) > 0) worst = r;
            if (worst == i) return;
            (_items[worst], _items[i]) = (_items[i], _items[worst]);
            i = worst;
        }
    }
}
