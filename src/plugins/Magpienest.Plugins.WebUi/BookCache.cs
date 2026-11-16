using System.Collections.Concurrent;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Viewing;

namespace Magpienest.Plugins.WebUi;

/// <summary>
/// Open page sources of books (archives are slow to open), kept for <see cref="Idle"/> after their last request and
/// disposed on expiry and when the plugin stops. Thread-safe.
/// </summary>
internal sealed class BookCache : IAsyncDisposable
{
    /// <summary>How long an unused book stays open.</summary>
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(1);

    private readonly object _lock = new();
    private readonly Dictionary<long, Entry> _entries = [];
    private readonly TimeProvider _clock;
    private readonly ITimer _sweeper;
    private bool _disposed;

    public BookCache(TimeProvider clock)
    {
        _clock = clock;
        _sweeper = clock.CreateTimer(_ => _ = SweepAsync(), null, SweepEvery, SweepEvery);
    }

    /// <summary>Number of open books (tests).</summary>
    internal int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Opens a book, or reuses the open one. Dispose the lease when the request is done.</summary>
    /// <param name="item">The book.</param>
    /// <param name="open">The kind's <c>OpenPages</c>.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<BookLease> OpenAsync(LibraryItem item, Func<LibraryItem, CancellationToken, Task<IPageSource>> open,
                                           CancellationToken ct)
    {
        Entry entry;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(item.Id, out entry!))
            {
                // Opened without the request's token: other requests share it.
                entry = new Entry(open(item, CancellationToken.None));
                _entries[item.Id] = entry;
            }

            entry.Leases++;
            entry.LastUsed = _clock.GetUtcNow();
        }

        try
        {
            var pages = await entry.Pages.WaitAsync(ct);
            return new BookLease(this, entry, pages);
        }
        catch
        {
            Release(entry);
            lock (_lock)
            {
                if (entry.Pages.IsFaulted && _entries.TryGetValue(item.Id, out var current) && current == entry)
                {
                    _entries.Remove(item.Id);
                }
            }

            throw;
        }
    }

    /// <summary>Closes books unused for longer than <see cref="Idle"/>.</summary>
    internal async Task SweepAsync()
    {
        List<Entry> expired = [];
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            foreach (var (id, entry) in _entries.ToArray())
            {
                if (entry.Leases == 0 && now - entry.LastUsed >= Idle)
                {
                    _entries.Remove(id);
                    expired.Add(entry);
                }
            }
        }

        foreach (var entry in expired)
        {
            await CloseAsync(entry);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Entry[] all;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            all = [.. _entries.Values];
            _entries.Clear();
        }

        await _sweeper.DisposeAsync();
        foreach (var entry in all)
        {
            await CloseAsync(entry);
        }
    }

    private void Release(Entry entry)
    {
        lock (_lock)
        {
            entry.Leases--;
            entry.LastUsed = _clock.GetUtcNow();
        }
    }

    private static async Task CloseAsync(Entry entry)
    {
        try
        {
            await (await entry.Pages).DisposeAsync();
        }
        catch (Exception)
        {
            // It failed to open, or its archive is already gone; nothing left to close.
        }
    }

    /// <summary>One open book.</summary>
    internal sealed class Entry(Task<IPageSource> pages)
    {
        public Task<IPageSource> Pages { get; } = pages;

        /// <summary>Which pages are wider than tall, read once per page.</summary>
        public ConcurrentDictionary<int, bool> Wide { get; } = new();

        public int Leases { get; set; }

        public DateTimeOffset LastUsed { get; set; }
    }

    /// <summary>A book in use by one request.</summary>
    internal sealed class BookLease(BookCache owner, Entry entry, IPageSource pages) : IDisposable
    {
        private int _released;

        public IPageSource Pages { get; } = pages;

        /// <summary>Known page shapes of this book.</summary>
        public ConcurrentDictionary<int, bool> Wide => entry.Wide;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(entry);
            }
        }
    }
}
