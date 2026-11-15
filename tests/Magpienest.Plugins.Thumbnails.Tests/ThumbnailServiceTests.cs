using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Magpienest.Sdk.Thumbnails;

namespace Magpienest.Plugins.Thumbnails.Tests;

public sealed class ThumbnailServiceTests : ServiceTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CacheHitAvoidsProvider()
    {
        var service = CreateService();
        var provider = new CountingProvider();
        Register(service, provider);

        var first = await service.GetAsync(Item("a.png"), Ct);
        var second = await service.GetAsync(Item("a.png"), Ct);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(1, provider.Calls);
        Assert.True(((Freezable)first).IsFrozen);
    }

    [Fact]
    public async Task KeyIsCaseInsensitive()
    {
        var service = CreateService();
        var provider = new CountingProvider();
        Register(service, provider);

        await service.GetAsync(Item("Photo.PNG"), Ct);
        await service.GetAsync(Item("photo.png"), Ct);

        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task DiskCacheSurvivesANewServiceInstance()
    {
        var first = CreateService();
        var provider1 = new CountingProvider();
        Register(first, provider1);
        await first.GetAsync(Item("a.png"), Ct);
        first.Dispose(); // flushes the queued write

        var second = CreateService();
        var provider2 = new CountingProvider();
        Register(second, provider2);
        var image = await second.GetAsync(Item("a.png"), Ct);

        Assert.NotNull(image);
        Assert.True(((Freezable)image).IsFrozen);
        Assert.Equal(1, provider1.Calls);
        Assert.Equal(0, provider2.Calls);
        Assert.Equal(1, RowCount());
    }

    [Fact]
    public async Task StampChangeRegenerates()
    {
        var first = CreateService();
        var provider = new CountingProvider { Stamp = "v1" };
        Register(first, provider);
        await first.GetAsync(Item("a.png"), Ct);
        await first.GetAsync(Item("a.png"), Ct);
        Assert.Equal(1, provider.Calls);

        provider.Stamp = "v2";
        await first.GetAsync(Item("a.png"), Ct);
        Assert.Equal(2, provider.Calls);
        first.Dispose();

        // The disk row was replaced, not duplicated, and now carries the new stamp.
        var second = CreateService();
        var provider2 = new CountingProvider { Stamp = "v2" };
        Register(second, provider2);
        await second.GetAsync(Item("a.png"), Ct);
        Assert.Equal(0, provider2.Calls);
        Assert.Equal(1, RowCount());
    }

    [Fact]
    public async Task LruEvictsTheLeastRecentlyUsed()
    {
        // 100 x 80 x 4 = 32 000 bytes per thumbnail; room for two.
        var service = CreateService(memoryCapBytes: 2 * 32_000 + 100);
        var provider = new CountingProvider();
        Register(service, provider);

        var a = await service.GetAsync(Item("a.png"), Ct);
        var b = await service.GetAsync(Item("b.png"), Ct);
        Assert.Same(a, await service.GetAsync(Item("a.png"), Ct)); // a is now more recent than b
        var c = await service.GetAsync(Item("c.png"), Ct);          // evicts b

        Assert.Equal(2, service.MemoryCount);
        Assert.Same(a, await service.GetAsync(Item("a.png"), Ct));
        Assert.Same(c, await service.GetAsync(Item("c.png"), Ct));
        var bAgain = await service.GetAsync(Item("b.png"), Ct);
        Assert.NotSame(b, bAgain); // reloaded from the disk cache ...
        Assert.Equal(3, provider.Calls); // ... not from the provider
    }

    [Fact]
    public async Task ConcurrentIdenticalRequestsCallTheProviderOnce()
    {
        var service = CreateService();
        var provider = new CountingProvider();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Behaviour = async (_, _) =>
        {
            await gate.Task;
            return CountingProvider.MakeBitmap(100, 80, PixelFormats.Bgr32);
        };
        Register(service, provider);

        var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(() => service.GetAsync(Item("a.png"), Ct), Ct)).ToArray();
        await WaitUntilAsync(() => provider.Calls == 1);
        await Task.Delay(100, Ct);
        gate.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, provider.Calls);
        Assert.All(results, r => Assert.Same(results[0], r));
    }

    [Fact]
    public async Task RequestCancelledBeforeItStartsIsDropped()
    {
        var service = CreateService(workerCount: 1);
        var provider = new CountingProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Behaviour = async (item, _) =>
        {
            if (item.Title == "block.png")
            {
                started.TrySetResult();
                await gate.Task;
            }

            return CountingProvider.MakeBitmap(100, 80, PixelFormats.Bgr32);
        };
        Register(service, provider);

        var blocker = service.GetAsync(Item("block.png"), Ct);
        await started.Task;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var dropped = service.GetAsync(Item("dropped.png"), cts.Token);
        await Task.Delay(200, Ct); // let it reach the queue
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dropped);

        gate.SetResult();
        await blocker;
        Assert.NotNull(await service.GetAsync(Item("after.png"), Ct)); // the worker is still healthy
        await Task.Delay(100, Ct);

        Assert.Equal(0, provider.CallsFor(Item("dropped.png").Path));
    }

    [Fact]
    public async Task QueueIsLastInFirstOut()
    {
        var service = CreateService(workerCount: 1);
        var provider = new CountingProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Behaviour = async (item, _) =>
        {
            if (item.Title == "block.png")
            {
                started.TrySetResult();
                await gate.Task;
            }

            return CountingProvider.MakeBitmap(100, 80, PixelFormats.Bgr32);
        };
        Register(service, provider);

        var blocker = service.GetAsync(Item("block.png"), Ct);
        await started.Task;
        var tasks = new List<Task<ImageSource?>>();
        foreach (var name in new[] { "b.png", "c.png", "d.png" })
        {
            tasks.Add(service.GetAsync(Item(name), Ct));
            await Task.Delay(150, Ct); // distinct arrival order
        }

        gate.SetResult();
        await blocker;
        await Task.WhenAll(tasks);

        Assert.Equal(
            new[] { "block.png", "d.png", "c.png", "b.png" },
            provider.Order.Select(p => Path.GetFileName(p)).ToArray());
    }

    [Fact]
    public async Task OneCallerCancellingDoesNotCancelTheSharedWork()
    {
        var service = CreateService();
        var provider = new CountingProvider();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var providerToken = CancellationToken.None;
        provider.Behaviour = async (_, ct) =>
        {
            providerToken = ct;
            await gate.Task;
            return CountingProvider.MakeBitmap(100, 80, PixelFormats.Bgr32);
        };
        Register(service, provider);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var cancelled = service.GetAsync(Item("a.png"), cts.Token);
        var staying = service.GetAsync(Item("a.png"), Ct);
        await WaitUntilAsync(() => provider.Calls == 1);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(providerToken.IsCancellationRequested);

        gate.SetResult();
        Assert.NotNull(await staying);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task WhenEveryWaiterCancelsTheProviderIsCancelled()
    {
        var service = CreateService();
        var provider = new CountingProvider();
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Behaviour = async (_, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                observed.TrySetResult(true);
                throw;
            }

            return null;
        };
        Register(service, provider);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var request = service.GetAsync(Item("a.png"), cts.Token);
        await WaitUntilAsync(() => provider.Calls == 1);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.True(await observed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct));

        // A cancelled request is not a failure: asking again calls the provider again.
        provider.Behaviour = null;
        Assert.NotNull(await service.GetAsync(Item("a.png"), Ct));
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task AlreadyCancelledTokenThrowsWithoutCallingTheProvider()
    {
        var service = CreateService();
        var provider = new CountingProvider();
        Register(service, provider);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetAsync(Item("a.png"), new CancellationToken(canceled: true)));
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task FailureIsNotRetriedInTheSessionAndNotWrittenToDisk()
    {
        var first = CreateService();
        var provider = new CountingProvider { Behaviour = (_, _) => Task.FromResult<BitmapSource?>(null) };
        Register(first, provider);

        Assert.Null(await first.GetAsync(Item("bad.png"), Ct));
        Assert.Null(await first.GetAsync(Item("bad.png"), Ct));
        Assert.Null(await first.GetJpegAsync(Item("bad.png"), Ct));
        Assert.Equal(1, provider.Calls);
        Assert.Contains(Log.DebugMessages, m => m.Contains("bad.png"));
        first.Dispose();
        Assert.Equal(0, RowCount());

        // A new session tries again.
        var second = CreateService();
        var provider2 = new CountingProvider();
        Register(second, provider2);
        Assert.NotNull(await second.GetAsync(Item("bad.png"), Ct));
        Assert.Equal(1, provider2.Calls);
    }

    [Fact]
    public async Task ThrowingProviderIsAFailureToo()
    {
        var service = CreateService();
        var provider = new CountingProvider { Behaviour = (_, _) => throw new InvalidOperationException("boom") };
        Register(service, provider);

        Assert.Null(await service.GetAsync(Item("bad.png"), Ct));
        Assert.Null(await service.GetAsync(Item("bad.png"), Ct));
        Assert.Equal(1, provider.Calls);
        Assert.Contains(Log.DebugMessages, m => m.Contains("boom"));
        Assert.Empty(Log.Problems);
    }

    [Fact]
    public async Task ChangedStampRetriesAFailedItem()
    {
        var service = CreateService();
        var provider = new CountingProvider { Stamp = "v1" };
        provider.Behaviour = (_, _) => Task.FromResult<BitmapSource?>(null);
        Register(service, provider);
        Assert.Null(await service.GetAsync(Item("a.png"), Ct));

        provider.Behaviour = null;
        provider.Stamp = "v2";
        Assert.NotNull(await service.GetAsync(Item("a.png"), Ct));
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task UnknownKindReturnsNullAndIsNotRemembered()
    {
        var service = CreateService();
        Assert.Null(await service.GetAsync(Item("a.png", kind: "other"), Ct));
        Assert.Null(await service.GetJpegAsync(Item("a.png", kind: "other"), Ct));

        var provider = new CountingProvider();
        Register(service, provider, kind: "other");
        Assert.NotNull(await service.GetAsync(Item("a.png", kind: "other"), Ct));
    }

    [Fact]
    public async Task GetJpegReturnsAValidJpegAndSharesTheCacheWithGetAsync()
    {
        var service = CreateService();
        var provider = new CountingProvider();
        Register(service, provider);

        var image = await service.GetAsync(Item("a.png"), Ct);
        var jpeg = await service.GetJpegAsync(Item("a.png"), Ct);
        Assert.NotNull(image);
        Assert.NotNull(jpeg);
        Assert.Equal(0xFF, jpeg[0]);
        Assert.Equal(0xD8, jpeg[1]);
        Assert.Equal(1, provider.Calls);

        using var stream = new MemoryStream(jpeg);
        var decoded = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal(100, decoded.PixelWidth);
        Assert.Equal(80, decoded.PixelHeight);

        // And the other way round, in a fresh service.
        service.Dispose();
        var other = CreateService();
        var provider2 = new CountingProvider();
        Register(other, provider2);
        var stored = await other.GetJpegAsync(Item("a.png"), Ct);
        Assert.Equal(jpeg, stored); // the blob as stored
        Assert.NotNull(await other.GetAsync(Item("a.png"), Ct));
        Assert.Equal(0, provider2.Calls);

        var third = CreateService();
        var provider3 = new CountingProvider();
        Register(third, provider3);
        Assert.NotNull(await third.GetJpegAsync(Item("fresh.png"), Ct));
        Assert.NotNull(await third.GetAsync(Item("fresh.png"), Ct));
        Assert.Equal(1, provider3.Calls);
    }

    [Theory]
    [InlineData(1000, 300)]
    [InlineData(300, 1000)]
    [InlineData(385, 385)]
    [InlineData(5000, 17)]
    [InlineData(2000, 2000)]
    [InlineData(200, 100)]
    public async Task ResultsNeverExceedTheThumbnailSize(int width, int height)
    {
        var service = CreateService();
        var provider = new CountingProvider { Width = width, Height = height };
        Register(service, provider);

        var image = (BitmapSource?)await service.GetAsync(Item("big.png"), Ct);

        Assert.NotNull(image);
        Assert.True(image.PixelWidth <= ThumbnailDefaults.ThumbnailSize, $"width {image.PixelWidth}");
        Assert.True(image.PixelHeight <= ThumbnailDefaults.ThumbnailSize, $"height {image.PixelHeight}");
        Assert.True(image.IsFrozen);
        if (width <= ThumbnailDefaults.ThumbnailSize && height <= ThumbnailDefaults.ThumbnailSize)
        {
            Assert.Equal(width, image.PixelWidth); // never upscaled
        }
    }

    [Fact]
    public async Task TransparentBitmapsAreFlattenedConsistently()
    {
        var service = CreateService();
        var provider = new CountingProvider { Format = PixelFormats.Pbgra32 };
        Register(service, provider);

        var image = await service.GetAsync(Item("alpha.png"), Ct);

        Assert.NotNull(image);
        Assert.True(((Freezable)image).IsFrozen);
    }

    [Fact]
    public async Task InvalidateRemovesMemoryDiskAndFailure()
    {
        var first = CreateService();
        var provider = new CountingProvider();
        Register(first, provider);
        await first.GetAsync(Item("a.png"), Ct);
        await first.InvalidateAsync(Item("a.png"));
        await first.GetAsync(Item("a.png"), Ct);
        Assert.Equal(2, provider.Calls);

        // failure entry
        provider.Behaviour = (_, _) => Task.FromResult<BitmapSource?>(null);
        Assert.Null(await first.GetAsync(Item("b.png"), Ct));
        provider.Behaviour = null;
        Assert.Null(await first.GetAsync(Item("b.png"), Ct)); // still remembered
        await first.InvalidateAsync(Item("b.png"));
        Assert.NotNull(await first.GetAsync(Item("b.png"), Ct));
        first.Dispose();

        // disk: after invalidating, a new session finds nothing stored
        var second = CreateService();
        var provider2 = new CountingProvider();
        Register(second, provider2);
        await second.GetAsync(Item("a.png"), Ct);
        await second.InvalidateAsync(Item("a.png"));
        second.Dispose();
        var third = CreateService();
        var provider3 = new CountingProvider();
        Register(third, provider3);
        await third.GetAsync(Item("a.png"), Ct);
        Assert.Equal(1, provider3.Calls);
    }

    [Fact]
    public async Task ClearCacheEmptiesMemoryAndDisk()
    {
        var first = CreateService();
        var provider = new CountingProvider();
        Register(first, provider);
        await first.GetAsync(Item("a.png"), Ct);
        await first.GetAsync(Item("b.png"), Ct);
        first.Dispose();

        var second = CreateService();
        var provider2 = new CountingProvider();
        Register(second, provider2);
        await second.GetAsync(Item("a.png"), Ct); // from disk, now also in memory
        Assert.Equal(0, provider2.Calls);

        await second.ClearCacheAsync();
        Assert.Equal(0, second.MemoryCount);
        Assert.Equal(0, RowCount());
        await second.GetAsync(Item("a.png"), Ct);
        Assert.Equal(1, provider2.Calls);
        await second.GetAsync(Item("b.png"), Ct);
        Assert.Equal(2, provider2.Calls);
    }

    [Fact]
    public async Task WritesAreBatchedByCountAndByTimer()
    {
        var service = CreateService(flushInterval: TimeSpan.FromMilliseconds(300), flushBatch: 3);
        var provider = new CountingProvider();
        Register(service, provider);

        await service.GetAsync(Item("a.png"), Ct);
        await service.GetAsync(Item("b.png"), Ct);
        await Task.Delay(50, Ct);
        Assert.Equal(0, RowCountSafe()); // nothing is written before the batch is full or the timer fires

        await service.GetAsync(Item("c.png"), Ct); // third one fills the batch
        await WaitUntilAsync(() => RowCountSafe() == 3);

        await service.GetAsync(Item("d.png"), Ct); // below the batch size: the timer flushes it
        await WaitUntilAsync(() => RowCountSafe() == 4);
    }

    [Fact]
    public async Task ProviderRegistrationIsUndoneWithTheOwner()
    {
        var service = CreateService();
        var provider = new CountingProvider();
        var owner = Register(service, provider);
        Assert.NotNull(await service.GetAsync(Item("a.png"), Ct));

        Assert.Throws<InvalidOperationException>(() => Register(service, new CountingProvider()));

        owner.Deactivate();
        Assert.Null(await service.GetAsync(Item("a.png"), Ct));
        Assert.Null(await service.GetAsync(Item("b.png"), Ct));
    }

    [Fact]
    public async Task DisposedServiceReturnsNull()
    {
        var service = CreateService();
        Register(service, new CountingProvider());
        service.Dispose();
        service.Dispose();

        Assert.Null(await service.GetAsync(Item("a.png"), Ct));
        Assert.Null(await service.GetJpegAsync(Item("a.png"), Ct));
        Assert.Null(await service.GetShellImageAsync(typeof(ThumbnailServiceTests).Assembly.Location, 32, iconOnly: true, Ct));
        await service.InvalidateAsync(Item("a.png"));
        await service.ClearCacheAsync();
    }

    [Fact]
    public async Task ShellIconIsReturnedForAnExistingFile()
    {
        var service = CreateService();
        var path = typeof(ThumbnailServiceTests).Assembly.Location;

        var icon = await service.GetShellImageAsync(path, 48, iconOnly: true, Ct);

        Assert.NotNull(icon);
        Assert.True(icon.IsFrozen);
        Assert.True(icon.PixelWidth > 0 && icon.PixelHeight > 0);
        var pixels = new byte[icon.PixelWidth * icon.PixelHeight * 4];
        icon.CopyPixels(pixels, icon.PixelWidth * 4, 0); // the data stays valid after the HBITMAP was deleted
    }

    [Fact]
    public async Task ShellImageForAMissingFileIsNullAndCancellationIsHonoured()
    {
        var service = CreateService();

        Assert.Null(await service.GetShellImageAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "nope.png"), 48, iconOnly: true, Ct));
        Assert.Null(await service.GetShellImageAsync("", 48, iconOnly: true, Ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetShellImageAsync(typeof(ThumbnailServiceTests).Assembly.Location, 48, iconOnly: true, new CancellationToken(canceled: true)));
    }

    private int RowCountSafe()
    {
        try
        {
            return RowCount();
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return 0; // file or table not there yet
        }
    }
}
