namespace Magpienest.Plugins.ImageViewer;

/// <summary>
/// Decoded pages keyed by page index, least-recently-used first out when over the byte budget.
/// Pinned pages (the current frame) are never evicted. Single-threaded (UI thread).
/// </summary>
/// <typeparam name="T">Cached value.</typeparam>
internal sealed class PageCache<T>(long budgetBytes) where T : class
{
    private readonly Dictionary<int, LinkedListNode<Entry>> _map = [];
    private readonly LinkedList<Entry> _lru = new(); // first = most recently used
    private readonly HashSet<int> _pinned = [];

    public long BudgetBytes { get; set; } = budgetBytes;

    public long TotalBytes { get; private set; }

    public int Count => _map.Count;

    public bool TryGet(int index, out T value)
    {
        if (_map.TryGetValue(index, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            value = node.Value.Value;
            return true;
        }

        value = null!;
        return false;
    }

    public bool Contains(int index) => _map.ContainsKey(index);

    public void Add(int index, T value, long bytes)
    {
        Remove(index);
        var node = _lru.AddFirst(new Entry(index, value, Math.Max(0, bytes)));
        _map[index] = node;
        TotalBytes += node.Value.Bytes;
        Trim();
    }

    /// <summary>Replaces the pinned set (the pages of the current frame) and trims.</summary>
    public void Pin(IEnumerable<int> indexes)
    {
        _pinned.Clear();
        _pinned.UnionWith(indexes);
        Trim();
    }

    public void Clear()
    {
        _map.Clear();
        _lru.Clear();
        _pinned.Clear();
        TotalBytes = 0;
    }

    private void Remove(int index)
    {
        if (_map.Remove(index, out var node))
        {
            _lru.Remove(node);
            TotalBytes -= node.Value.Bytes;
        }
    }

    private void Trim()
    {
        var node = _lru.Last;
        while (TotalBytes > BudgetBytes && node is not null)
        {
            var previous = node.Previous;
            if (!_pinned.Contains(node.Value.Index))
            {
                Remove(node.Value.Index);
            }

            node = previous;
        }
    }

    private sealed record Entry(int Index, T Value, long Bytes);
}
