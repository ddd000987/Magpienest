using System.Windows.Media.Imaging;

namespace Magpienest.Plugins.Thumbnails;

/// <summary>Byte-capped least-recently-used cache of frozen bitmaps, keyed by thumbnail key and validated by stamp.</summary>
internal sealed class MemoryLru
{
    private sealed record Entry(string Key, string Stamp, BitmapSource Bitmap, long Bytes);

    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _map = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _order = new(); // first = most recently used
    private readonly long _capBytes;
    private long _bytes;

    public MemoryLru(long capBytes) => _capBytes = capBytes;

    /// <summary>Number of cached bitmaps.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _map.Count;
            }
        }
    }

    /// <summary>Estimated size: pixels × 4 bytes.</summary>
    public static long Estimate(BitmapSource bitmap) => (long)bitmap.PixelWidth * bitmap.PixelHeight * 4;

    public bool TryGet(string key, string stamp, out BitmapSource bitmap)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                if (string.Equals(node.Value.Stamp, stamp, StringComparison.Ordinal))
                {
                    _order.Remove(node);
                    _order.AddFirst(node);
                    bitmap = node.Value.Bitmap;
                    return true;
                }

                RemoveNode(node);
            }
        }

        bitmap = null!;
        return false;
    }

    public void Put(string key, string stamp, BitmapSource bitmap)
    {
        var bytes = Estimate(bitmap);
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                RemoveNode(existing);
            }

            if (bytes > _capBytes)
            {
                return; // would never fit; do not flush the whole cache for it
            }

            var node = new LinkedListNode<Entry>(new Entry(key, stamp, bitmap, bytes));
            _order.AddFirst(node);
            _map[key] = node;
            _bytes += bytes;
            while (_bytes > _capBytes && _order.Last is { } last)
            {
                RemoveNode(last);
            }
        }
    }

    public void Remove(string key)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                RemoveNode(node);
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _order.Clear();
            _bytes = 0;
        }
    }

    private void RemoveNode(LinkedListNode<Entry> node)
    {
        _order.Remove(node);
        _map.Remove(node.Value.Key);
        _bytes -= node.Value.Bytes;
    }
}
