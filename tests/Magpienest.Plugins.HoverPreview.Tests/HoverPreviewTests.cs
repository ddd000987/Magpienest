using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Magpienest.Plugins.HoverPreview;
using Magpienest.Sdk;
using Magpienest.Sdk.Imaging;
using Magpienest.Sdk.Viewing;

namespace Magpienest.Plugins.HoverPreview.Tests;

public sealed class HoverLayoutTests
{
    private static readonly Size Area = new(1000, 800);

    [Fact]
    public void PrefersTheRightOfTheCursorAndCentresVertically()
    {
        var rect = HoverLayout.Fit(new Point(100, 400), Area, new Size(300, 200))!.Value;
        Assert.Equal(100 + HoverLayout.Gap, rect.X);
        Assert.Equal(300, rect.Width);
        Assert.Equal(400 - 100, rect.Y);
    }

    [Fact]
    public void FlipsToTheLeftWhenTheLeftHasMoreRoom()
    {
        var rect = HoverLayout.Fit(new Point(900, 400), Area, new Size(600, 400))!.Value;
        Assert.True(rect.Right <= 900 - HoverLayout.Gap + 0.001);
        Assert.Equal(600, rect.Width);
    }

    [Fact]
    public void ShrinksToFitButNeverEnlarges()
    {
        var tall = HoverLayout.Fit(new Point(100, 400), Area, new Size(2000, 4000))!.Value;
        Assert.True(tall.Height <= Area.Height - 2 * HoverLayout.Margin);
        Assert.Equal(0.5, tall.Width / tall.Height, 2);

        var small = HoverLayout.Fit(new Point(100, 400), Area, new Size(80, 60))!.Value;
        Assert.Equal(80, small.Width);
        Assert.Equal(60, small.Height);
    }

    [Theory]
    [InlineData(100, 400)]
    [InlineData(900, 400)]
    [InlineData(500, 50)]
    [InlineData(500, 750)]
    [InlineData(500, 400)]
    public void NeverCoversTheCursorAndStaysInsideTheArea(double x, double y)
    {
        var cursor = new Point(x, y);
        var rect = HoverLayout.Fit(cursor, Area, new Size(3000, 2000), chrome: 2)!.Value;
        Assert.False(rect.Contains(cursor));
        Assert.True(rect.Left >= HoverLayout.Margin - 0.001 && rect.Top >= HoverLayout.Margin - 0.001);
        Assert.True(rect.Right <= Area.Width - HoverLayout.Margin + 0.001 && rect.Bottom <= Area.Height - HoverLayout.Margin + 0.001);
    }

    [Fact]
    public void NothingWhenTheAreaIsTooSmall()
    {
        Assert.Null(HoverLayout.Fit(new Point(30, 30), new Size(60, 60), new Size(500, 500)));
        Assert.Null(HoverLayout.Fit(new Point(30, 30), Area, new Size(0, 10)));
    }
}

public sealed class HoverPreviewServiceTests
{
    [Fact]
    public void ANewHoverReplacesTheOldOneAndRecyclingAnElementEndsIt()
    {
        RunOnSta(() =>
        {
            var service = new HoverPreviewService(new FakeImaging(), new MemoryConfig(), new NullLog());
            var first = new Border();
            var second = new Border { DataContext = "a" };
            service.Hover(first, Request());
            Assert.Same(first, service.Target);

            service.Hover(second, Request());
            Assert.Same(second, service.Target);

            second.DataContext = "b";
            Assert.Null(service.Target);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void ADelayFarOutsideTheRangeIsClamped()
    {
        var config = new MemoryConfig();
        var service = new HoverPreviewService(new FakeImaging(), config, new NullLog());
        Assert.Equal(TimeSpan.FromMilliseconds(HoverPreviewService.DefaultDelay), service.Delay);
        config.Set(HoverPreviewService.DelayKey, 5);
        Assert.Equal(TimeSpan.FromMilliseconds(100), service.Delay);
        config.Set(HoverPreviewService.DelayKey, 60_000);
        Assert.Equal(TimeSpan.FromSeconds(3), service.Delay);
    }

    [Fact]
    public void LeavingBeforeTheDelayNeverOpensThePages()
    {
        RunOnSta(async () =>
        {
            var config = new MemoryConfig();
            config.Set(HoverPreviewService.DelayKey, 100);
            var service = new HoverPreviewService(new FakeImaging(), config, new NullLog());
            var opened = 0;
            var target = new Border();
            service.Hover(target, new HoverPreviewRequest
            {
                Title = "x",
                OpenPages = _ =>
                {
                    opened++;
                    return Task.FromResult<IPageSource>(new FilePageSource([]));
                },
            });

            // The mouse is not over the element: when the delay ends the hover just stops.
            await Task.Delay(400, TestContext.Current.CancellationToken);
            Assert.Equal(0, opened);
            Assert.Null(service.Target);
        });
    }

    private static HoverPreviewRequest Request() =>
        new() { Title = "x", OpenPages = _ => Task.FromResult<IPageSource>(new FilePageSource([])) };

    private static void RunOnSta(Func<Task> body)
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
                    await body();
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
        thread.Join();
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    private sealed class MemoryConfig : IPluginConfig
    {
        private readonly Dictionary<string, object?> _values = [];

        public T Get<T>(string key, T defaultValue) => _values.TryGetValue(key, out var v) && v is T t ? t : defaultValue;

        public void Set<T>(string key, T value) => _values[key] = value;
    }

    private sealed class NullLog : ILog
    {
        public void Debug(string message) { }

        public void Info(string message) { }

        public void Warn(string message, Exception? ex = null) { }

        public void Error(string message, Exception? ex = null) { }
    }

    private sealed class FakeImaging : IImagingService
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>();

        public bool IsSupported(string pathOrExtension) => true;

        public Task<ImageInfo?> ReadInfoAsync(Stream seekable, CancellationToken ct = default) => Task.FromResult<ImageInfo?>(null);

        public Task<BitmapSource> DecodeAsync(Stream seekable, DecodeOptions options, CancellationToken ct = default) =>
            throw new ImageDecodeException("not used");

        public byte[] EncodeJpeg(BitmapSource source, int quality) => [];

        public void RegisterDecoder(IPluginContext owner, IImageDecoder decoder) { }
    }
}
