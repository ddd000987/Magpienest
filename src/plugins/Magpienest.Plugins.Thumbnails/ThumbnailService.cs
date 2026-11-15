using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Magpienest.Sdk;
using Magpienest.Sdk.Imaging;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Thumbnails;

namespace Magpienest.Plugins.Thumbnails;

/// <summary>
/// Memory LRU → disk cache → provider. Constructible without the kernel so it can be tested directly.
/// </summary>
/// <remarks>
/// Pipeline: every request ends in one internal "get or create JPEG" operation (<see cref="GetOrCreateAsync"/>);
/// <see cref="GetJpegAsync"/> returns its bytes and <see cref="GetAsync"/> turns them (or the freshly created bitmap)
/// into an <see cref="ImageSource"/>, so the two public methods can never disagree.
///
/// Cancellation choice: concurrent requests for one key share one "flight" (one generation, one Task). A caller that
/// cancels stops waiting at once (it gets <see cref="OperationCanceledException"/>) but the shared work keeps running
/// for the other waiters; only when the LAST waiter has cancelled is the flight cancelled (its provider token is
/// cancelled and a not-yet-started job is removed from the queue) and forgotten, so a later request starts fresh.
/// The alternative (cancelling the shared task with the first caller) would let one scrolled-away tile fail the
/// tile that is still on screen, which is exactly the case the web interface and the grid both hit.
/// </remarks>
internal sealed class ThumbnailService : IThumbnailService, IDisposable
{
    public const long DefaultMemoryCapBytes = 256L * 1024 * 1024;

    private sealed class Flight
    {
        public Flight(string key, string stamp, CancellationTokenSource cts)
        {
            Key = key;
            Stamp = stamp;
            Cts = cts;
        }

        public string Key { get; }
        public string Stamp { get; }
        public CancellationTokenSource Cts { get; }
        public int Waiters { get; set; }
        public bool Abandoned { get; set; }
        public TaskCompletionSource<Produced?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>The outcome of a flight: the JPEG and (lazily or already) the bitmap made from it.</summary>
    private sealed class Produced
    {
        private readonly object _lock = new();
        private Task<BitmapSource?>? _bitmap;

        public Produced(string key, string stamp, byte[] jpeg, long epoch, BitmapSource? bitmap)
        {
            Key = key;
            Stamp = stamp;
            Jpeg = jpeg;
            Epoch = epoch;
            if (bitmap is not null)
            {
                _bitmap = Task.FromResult<BitmapSource?>(bitmap);
            }
        }

        public string Key { get; }
        public string Stamp { get; }
        public byte[] Jpeg { get; }
        public long Epoch { get; }

        public Task<BitmapSource?> GetBitmapAsync(Func<Produced, Task<BitmapSource?>> decode)
        {
            lock (_lock)
            {
                return _bitmap ??= decode(this);
            }
        }
    }

    private sealed class Job
    {
        public Job(Flight flight, LibraryItem item, IThumbnailProvider provider, long epoch)
        {
            Flight = flight;
            Item = item;
            Provider = provider;
            Epoch = epoch;
        }

        public Flight Flight { get; }
        public LibraryItem Item { get; }
        public IThumbnailProvider Provider { get; }
        public long Epoch { get; }
        public TaskCompletionSource<Produced?> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static readonly PixelFormat[] AlphaFormats =
    [
        PixelFormats.Bgra32, PixelFormats.Pbgra32, PixelFormats.Prgba64, PixelFormats.Rgba64,
        PixelFormats.Prgba128Float, PixelFormats.Rgba128Float,
    ];

    private readonly IImagingService _imaging;
    private readonly ILog _log;
    private readonly ThumbnailDb _db;
    private readonly MemoryLru _memory;
    private readonly ConcurrentDictionary<string, IThumbnailProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _failures = new(StringComparer.Ordinal); // key -> stamp that failed
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _readGate = new(Math.Max(2, Environment.ProcessorCount));

    private readonly object _flightLock = new();
    private readonly Dictionary<string, Flight> _flights = new(StringComparer.Ordinal);

    // Guards "check the epoch, then publish to a cache" against Invalidate/Clear bumping the epoch.
    private readonly object _stateLock = new();
    private long _epoch;

    private readonly object _queueLock = new();
    private readonly List<Job> _stack = []; // LIFO: workers take from the end
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly Task[] _workers;

    private readonly object _shellLock = new();
    private ShellImageWorker? _shell;
    private int _disposed;

    public ThumbnailService(
        string dbPath,
        IImagingService imaging,
        ILog log,
        long memoryCapBytes = DefaultMemoryCapBytes,
        int? workerCount = null,
        TimeSpan? flushInterval = null,
        int flushBatch = 50)
    {
        _imaging = imaging;
        _log = log;
        _memory = new MemoryLru(memoryCapBytes);
        _db = new ThumbnailDb(dbPath, log, flushInterval ?? TimeSpan.FromSeconds(2), flushBatch);
        var workers = workerCount ?? Math.Max(2, Environment.ProcessorCount / 2);
        _workers = new Task[workers];
        for (var i = 0; i < workers; i++)
        {
            _workers[i] = Task.Run(WorkerAsync);
        }
    }

    internal int MemoryCount => _memory.Count;

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void RegisterProvider(IPluginContext owner, string kind, IThumbnailProvider provider)
    {
        if (!_providers.TryAdd(kind, provider))
        {
            throw new InvalidOperationException($"A thumbnail provider for kind '{kind}' is already registered.");
        }

        owner.Track(() =>
        {
            _providers.TryRemove(new KeyValuePair<string, IThumbnailProvider>(kind, provider));
            // A provider that comes back later may succeed where this one failed.
            ClearFailures(kind.ToLowerInvariant() + "|");
        });
    }

    public async Task<ImageSource?> GetAsync(LibraryItem item, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!TryPrepare(item, out var provider, out var key, out var stamp))
        {
            return null;
        }

        if (_memory.TryGet(key, stamp, out var cached))
        {
            return cached;
        }

        var produced = await GetOrCreateAsync(item, provider, key, stamp, ct).ConfigureAwait(false);
        if (produced is null)
        {
            return null;
        }

        return await produced.GetBitmapAsync(DecodeAsync).WaitAsync(ct).ConfigureAwait(false);
    }

    public async Task<byte[]?> GetJpegAsync(LibraryItem item, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!TryPrepare(item, out var provider, out var key, out var stamp))
        {
            return null;
        }

        var produced = await GetOrCreateAsync(item, provider, key, stamp, ct).ConfigureAwait(false);
        return produced?.Jpeg;
    }

    public async Task InvalidateAsync(LibraryItem item)
    {
        var key = MakeKey(item);
        lock (_stateLock)
        {
            _epoch++;
            _memory.Remove(key);
            _failures.TryRemove(key, out _);
        }

        lock (_flightLock)
        {
            // An in-flight generation may still deliver its (old) result to its waiters, but it is no longer
            // shared with new requests and, because the epoch moved, it is not cached.
            _flights.Remove(key);
        }

        if (!IsDisposed)
        {
            await Task.Run(() => _db.Delete(key)).ConfigureAwait(false);
        }
    }

    public async Task ClearCacheAsync()
    {
        lock (_stateLock)
        {
            _epoch++;
            _memory.Clear();
            _failures.Clear();
        }

        lock (_flightLock)
        {
            _flights.Clear();
        }

        if (!IsDisposed)
        {
            await Task.Run(_db.Clear).ConfigureAwait(false);
        }
    }

    public Task<BitmapSource?> GetShellImageAsync(string path, int pixelSize, bool iconOnly, CancellationToken ct = default)
    {
        if (IsDisposed || pixelSize <= 0 || string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult<BitmapSource?>(null);
        }

        ShellImageWorker worker;
        lock (_shellLock)
        {
            if (IsDisposed)
            {
                return Task.FromResult<BitmapSource?>(null);
            }

            worker = _shell ??= new ShellImageWorker(_log);
        }

        return worker.GetImageAsync(path, pixelSize, iconOnly, ct);
    }

    /// <summary>Flushes pending writes, stops the workers and the shell thread, closes the database.</summary>
    /// <remarks>
    /// Called from the plugin's undo list on the UI thread. The flush is one small transaction (at most a batch of
    /// about 50 thumbnails), which is the price of not losing them.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel(); // flights, queued jobs and workers observe this
        lock (_shellLock)
        {
            _shell?.Dispose();
        }

        // Workers only enqueue writes (dropped once the db is disposed), so they need not be joined.
        _db.Dispose();
    }

    private static string MakeKey(LibraryItem item) => $"{item.Kind}|{item.Path}".ToLowerInvariant();

    private bool TryPrepare(LibraryItem item, out IThumbnailProvider provider, out string key, out string stamp)
    {
        key = MakeKey(item);
        stamp = string.Empty;
        provider = null!;
        if (IsDisposed)
        {
            return false;
        }

        if (!_providers.TryGetValue(item.Kind, out var found))
        {
            // Not remembered as a failure: the provider's plugin may simply not be active yet.
            _log.Debug($"No thumbnail provider for kind '{item.Kind}'.");
            return false;
        }

        provider = found;
        try
        {
            stamp = provider.GetStamp(item);
        }
        catch (Exception ex)
        {
            _log.Debug($"Thumbnail stamp for '{item.Path}' failed: {ex.Message}");
            return false;
        }

        if (_failures.TryGetValue(key, out var failedStamp) && failedStamp == stamp)
        {
            return false; // failed earlier this session with the same stamp
        }

        return true;
    }

    private async Task<Produced?> GetOrCreateAsync(LibraryItem item, IThumbnailProvider provider, string key, string stamp, CancellationToken ct)
    {
        Flight flight;
        var created = false;
        lock (_flightLock)
        {
            if (!_flights.TryGetValue(key, out flight!) || flight.Abandoned || flight.Stamp != stamp)
            {
                flight = new Flight(key, stamp, CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token));
                _flights[key] = flight;
                created = true;
            }

            flight.Waiters++;
        }

        if (created)
        {
            var started = flight;
            _ = Task.Run(() => RunFlightAsync(started, item, provider));
        }

        try
        {
            return await flight.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ReleaseWaiter(flight);
            throw;
        }
    }

    private void ReleaseWaiter(Flight flight)
    {
        var cancel = false;
        lock (_flightLock)
        {
            flight.Waiters--;
            if (flight.Waiters <= 0 && !flight.Completion.Task.IsCompleted)
            {
                flight.Abandoned = true;
                cancel = true;
                if (_flights.TryGetValue(flight.Key, out var current) && current == flight)
                {
                    _flights.Remove(flight.Key);
                }
            }
        }

        if (cancel)
        {
            CancelQuietly(flight.Cts);
        }
    }

    private static void CancelQuietly(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // the flight already finished
        }
    }

    private async Task RunFlightAsync(Flight flight, LibraryItem item, IThumbnailProvider provider)
    {
        var ct = flight.Cts.Token;
        try
        {
            ct.ThrowIfCancellationRequested();
            long epoch;
            lock (_stateLock)
            {
                epoch = _epoch;
            }

            var jpeg = await ReadDiskAsync(flight.Key, flight.Stamp, ct).ConfigureAwait(false);
            Produced? produced;
            if (jpeg is not null)
            {
                produced = new Produced(flight.Key, flight.Stamp, jpeg, epoch, bitmap: null);
            }
            else
            {
                var job = new Job(flight, item, provider, epoch);
                using var registration = ct.Register(() => CancelJob(job));
                lock (_queueLock)
                {
                    _stack.Add(job);
                }

                _queueSignal.Release();
                produced = await job.Done.Task.ConfigureAwait(false);
            }

            flight.Completion.TrySetResult(produced);
        }
        catch (OperationCanceledException)
        {
            flight.Completion.TrySetCanceled();
        }
        catch (Exception ex)
        {
            _log.Debug($"Thumbnail request for '{item.Path}' failed: {ex.Message}");
            flight.Completion.TrySetResult(null);
        }
        finally
        {
            lock (_flightLock)
            {
                if (_flights.TryGetValue(flight.Key, out var current) && current == flight)
                {
                    _flights.Remove(flight.Key);
                }
            }

            try
            {
                flight.Cts.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private async Task<byte[]?> ReadDiskAsync(string key, string stamp, CancellationToken ct)
    {
        await _readGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => _db.TryRead(key, stamp), ct).ConfigureAwait(false);
        }
        finally
        {
            _readGate.Release();
        }
    }

    private void CancelJob(Job job)
    {
        lock (_queueLock)
        {
            _stack.Remove(job); // a request cancelled before it starts is dropped
        }

        job.Done.TrySetCanceled();
    }

    private async Task WorkerAsync()
    {
        var token = _shutdown.Token;
        try
        {
            while (true)
            {
                await _queueSignal.WaitAsync(token).ConfigureAwait(false);
                Job job;
                lock (_queueLock)
                {
                    if (_stack.Count == 0)
                    {
                        continue; // the job was cancelled and removed after its signal was released
                    }

                    job = _stack[^1]; // LIFO: the most recently requested tile is the one on screen
                    _stack.RemoveAt(_stack.Count - 1);
                }

                await RunJobAsync(job).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task RunJobAsync(Job job)
    {
        var ct = job.Flight.Cts.Token;
        if (ct.IsCancellationRequested || job.Done.Task.IsCompleted)
        {
            job.Done.TrySetCanceled();
            return;
        }

        try
        {
            var produced = await GenerateAsync(job, ct).ConfigureAwait(false);
            job.Done.TrySetResult(produced);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            job.Done.TrySetCanceled();
        }
        catch (Exception ex)
        {
            RememberFailure(job, ex.Message);
            job.Done.TrySetResult(null);
        }
    }

    private async Task<Produced?> GenerateAsync(Job job, CancellationToken ct)
    {
        var bitmap = await job.Provider.CreateAsync(job.Item, ThumbnailDefaults.ThumbnailSize, ct).ConfigureAwait(false);
        if (bitmap is null)
        {
            RememberFailure(job, "the provider returned no bitmap");
            return null;
        }

        if (!bitmap.IsFrozen)
        {
            if (!bitmap.CanFreeze)
            {
                RememberFailure(job, "the provider returned a bitmap that cannot be frozen");
                return null;
            }

            bitmap.Freeze();
        }

        bitmap = ScaleDown(bitmap);
        var jpeg = _imaging.EncodeJpeg(bitmap, ThumbnailDefaults.JpegQuality);

        // What the caller gets must look the same as what a later disk hit shows. JPEG has no alpha, so a bitmap
        // with transparency is replaced by its JPEG round trip; opaque ones are used as they are.
        var shown = bitmap;
        if (Array.IndexOf(AlphaFormats, bitmap.Format) >= 0)
        {
            try
            {
                using var stream = new MemoryStream(jpeg, writable: false);
                shown = await _imaging.DecodeAsync(stream, new DecodeOptions(), ct).ConfigureAwait(false);
                if (!shown.IsFrozen)
                {
                    shown.Freeze();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Debug($"Could not decode the encoded thumbnail of '{job.Item.Path}': {ex.Message}");
                shown = bitmap;
            }
        }

        var produced = new Produced(job.Flight.Key, job.Flight.Stamp, jpeg, job.Epoch, shown);
        lock (_stateLock)
        {
            if (_epoch == job.Epoch)
            {
                _memory.Put(produced.Key, produced.Stamp, shown);
                _db.Enqueue(produced.Key, produced.Stamp, jpeg);
            }
        }

        return produced;
    }

    private static BitmapSource ScaleDown(BitmapSource bitmap)
    {
        var size = ThumbnailDefaults.ThumbnailSize;
        var longEdge = Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
        if (longEdge <= size)
        {
            return bitmap;
        }

        var scale = (double)size / longEdge;
        var width = Math.Clamp((int)Math.Round(bitmap.PixelWidth * scale), 1, size);
        var height = Math.Clamp((int)Math.Round(bitmap.PixelHeight * scale), 1, size);
        // A hair below the exact factor so the transform never rounds the long edge up to size + 1.
        var transform = new ScaleTransform((width - 0.01) / bitmap.PixelWidth, (height - 0.01) / bitmap.PixelHeight);
        var scaled = new TransformedBitmap(bitmap, transform);
        scaled.Freeze();
        return scaled;
    }

    private async Task<BitmapSource?> DecodeAsync(Produced produced)
    {
        // Task.Run: the first call may come from the UI thread; keep decoding off it.
        return await Task.Run(async () =>
        {
            try
            {
                using var stream = new MemoryStream(produced.Jpeg, writable: false);
                var bitmap = await _imaging.DecodeAsync(stream, new DecodeOptions(), _shutdown.Token).ConfigureAwait(false);
                if (!bitmap.IsFrozen)
                {
                    bitmap.Freeze();
                }

                lock (_stateLock)
                {
                    if (_epoch == produced.Epoch)
                    {
                        _memory.Put(produced.Key, produced.Stamp, bitmap);
                    }
                }

                return (BitmapSource?)bitmap;
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                // A damaged blob: forget it so the next request regenerates the thumbnail.
                _log.Debug($"Cached thumbnail '{produced.Key}' could not be decoded and is dropped: {ex.Message}");
                _db.Delete(produced.Key);
                return null;
            }
        }).ConfigureAwait(false);
    }

    private void RememberFailure(Job job, string reason)
    {
        _log.Debug($"No thumbnail for '{job.Item.Path}': {reason}.");
        lock (_stateLock)
        {
            if (_epoch == job.Epoch)
            {
                _failures[job.Flight.Key] = job.Flight.Stamp;
            }
        }
    }

    private void ClearFailures(string keyPrefix)
    {
        foreach (var key in _failures.Keys)
        {
            if (key.StartsWith(keyPrefix, StringComparison.Ordinal))
            {
                _failures.TryRemove(key, out _);
            }
        }
    }
}
