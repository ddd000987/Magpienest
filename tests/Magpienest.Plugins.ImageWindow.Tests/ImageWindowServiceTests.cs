using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Magpienest.Plugins.ImageWindow;
using Magpienest.Sdk;
using Magpienest.Sdk.Imaging;
using Magpienest.Sdk.Viewing;

namespace Magpienest.Plugins.ImageWindow.Tests;

public sealed class ImageWindowServiceTests
{
    [Fact]
    public async Task BytesAreCopiedAndTheSourceIsOpenedOnceAndDisposed()
    {
        var source = new TrackedStream([1, 2, 3, 4, 5]);
        var opened = 0;
        var request = new ImageWindowRequest
        {
            Name = "x",
            Open = _ =>
            {
                opened++;
                return Task.FromResult<Stream>(source);
            },
        };

        var bytes = await ImageWindowService.ReadAllAsync(request, 1024, TestContext.Current.CancellationToken);
        Assert.Equal([1, 2, 3, 4, 5], bytes);
        Assert.Equal(1, opened);
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task OversizeImagesAreRefused()
    {
        var seekable = new ImageWindowRequest { Name = "x", Open = _ => Task.FromResult<Stream>(new MemoryStream(new byte[100])) };
        await Assert.ThrowsAsync<IOException>(() => ImageWindowService.ReadAllAsync(seekable, 99, TestContext.Current.CancellationToken));

        var stream = new TrackedStream(new byte[100], canSeek: false);
        var unseekable = new ImageWindowRequest { Name = "x", Open = _ => Task.FromResult<Stream>(stream) };
        await Assert.ThrowsAsync<IOException>(() => ImageWindowService.ReadAllAsync(unseekable, 99, TestContext.Current.CancellationToken));
        Assert.True(stream.Disposed);
    }

    [Fact]
    public void WindowsOpenCascadeToggleAndCloseWithThePlugin()
    {
        var ct = TestContext.Current.CancellationToken;
        RunOnSta(async dispatcher =>
        {
            var config = new MemoryConfig();
            // A stopped clock: both windows belong to one batch however slow the machine is.
            var service = new ImageWindowService(new StaDispatcher(dispatcher), new FakeImaging(), config, new NullLog(), new StoppedClock());
            try
            {
                await service.OpenAsync(Request("a.png", 400, 300, "C:\\pictures\\a.png"), ct);
                await service.OpenAsync(Request("b.png", 400, 300, null), ct);
                Assert.Equal(2, service.Windows.Count);

                var first = service.Windows[0];
                var second = service.Windows[1];
                Assert.True(first.IsVisible);
                Assert.Equal("a.png", first.Title);
                Assert.Equal(WindowStyle.None, first.WindowStyle);
                Assert.False(first.Topmost);
                Assert.Equal(System.Windows.Media.Stretch.Uniform, first.ImageStretch); // never stretched
                Assert.NotNull(first.CloseButton);
                Assert.Equal(1, service.Cascade); // the offset itself is covered by WindowGeometryTests (it depends on the cursor here)

                // The aspect ratio of the image is the window's.
                var bounds = first.PhysicalBounds;
                Assert.Equal(4.0 / 3.0, bounds.Width / bounds.Height, 2);

                first.ToggleTopmost();
                Assert.True(first.Topmost);
                Assert.NotNull(first.ToastText);

                // "Show in Explorer" only for files on disk.
                Assert.Equal(1, MenuCount(first.BuildMenu()) - MenuCount(second.BuildMenu()));

                second.Close();
                Assert.Single(service.Windows);

                config.Set(ImageWindowService.TopmostKey, true);
                await service.OpenAsync(Request("c.png", 300, 600, null), ct);
                Assert.True(service.Windows[^1].Topmost);

                service.CloseAll();
                Assert.Empty(service.Windows);
                Assert.False(first.IsVisible);
            }
            finally
            {
                service.CloseAll();
            }
        });
    }

    [Fact]
    public void DecodeFailuresSurfaceAndOpenNoWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        RunOnSta(async dispatcher =>
        {
            var service = new ImageWindowService(new StaDispatcher(dispatcher), new FakeImaging(), new MemoryConfig(), new NullLog());
            var broken = new ImageWindowRequest { Name = "bad", Open = _ => Task.FromResult<Stream>(new MemoryStream()) };
            await Assert.ThrowsAsync<ImageDecodeException>(() => service.OpenAsync(broken, ct));
            Assert.Empty(service.Windows);
        });
    }

    private static int MenuCount(ContextMenu menu) => menu.Items.OfType<MenuItem>().Count();

    /// <summary>The fake decoder reads width and height from the first two 16-bit values.</summary>
    private static ImageWindowRequest Request(string name, int width, int height, string? path)
    {
        var bytes = new byte[] { (byte)(width >> 8), (byte)width, (byte)(height >> 8), (byte)height };
        return new ImageWindowRequest { Name = name, FilePath = path, Open = _ => Task.FromResult<Stream>(new MemoryStream(bytes)) };
    }

    private static void RunOnSta(Func<Dispatcher, Task> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await body(dispatcher);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The UI thread did not finish.");
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException("UI test failed: " + failure);
        }
    }

    private sealed class TrackedStream(byte[] data, bool canSeek = true) : MemoryStream(data)
    {
        public bool Disposed { get; private set; }

        public override bool CanSeek => canSeek && base.CanSeek;

        public override long Length => canSeek ? base.Length : throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class StaDispatcher(Dispatcher dispatcher) : IUiDispatcher
    {
        public bool CheckAccess() => dispatcher.CheckAccess();

        public void Post(Action action) => dispatcher.BeginInvoke(action);

        public Task InvokeAsync(Action action) => dispatcher.InvokeAsync(action).Task;

        public Task<T> InvokeAsync<T>(Func<T> func) => dispatcher.InvokeAsync(func).Task;
    }

    private sealed class MemoryConfig : IPluginConfig
    {
        private readonly Dictionary<string, object?> _values = [];

        public T Get<T>(string key, T defaultValue) => _values.TryGetValue(key, out var v) && v is T t ? t : defaultValue;

        public void Set<T>(string key, T value) => _values[key] = value;
    }

    private sealed class FakeImaging : IImagingService
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".png" };

        public bool IsSupported(string pathOrExtension) => true;

        public Task<ImageInfo?> ReadInfoAsync(Stream seekable, CancellationToken ct = default) =>
            Task.FromResult(Size(seekable) is { } s ? new ImageInfo(s.Width, s.Height) : null);

        public Task<BitmapSource> DecodeAsync(Stream seekable, DecodeOptions options, CancellationToken ct = default)
        {
            var (w, h) = Size(seekable) ?? throw new ImageDecodeException("empty");
            var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, new byte[w * h * 4], w * 4);
            bitmap.Freeze();
            return Task.FromResult<BitmapSource>(bitmap);
        }

        public byte[] EncodeJpeg(BitmapSource source, int quality) => [];

        public void RegisterDecoder(IPluginContext owner, IImageDecoder decoder) { }

        private static (int Width, int Height)? Size(Stream s)
        {
            var b = new byte[4];
            s.Position = 0;
            return s.Read(b, 0, 4) == 4 ? ((b[0] << 8) | b[1], (b[2] << 8) | b[3]) : null;
        }
    }

    private sealed class NullLog : ILog
    {
        public void Debug(string message) { }

        public void Info(string message) { }

        public void Warn(string message, Exception? ex = null) { }

        public void Error(string message, Exception? ex = null) { }
    }

    private sealed class StoppedClock : TimeProvider
    {
        private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
