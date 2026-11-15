using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Magpienest.Plugins.ImageViewer;
using Magpienest.Sdk;
using Magpienest.Sdk.Imaging;
using Magpienest.Sdk.Shell;
using Magpienest.Sdk.Viewing;

namespace Magpienest.Plugins.ImageViewer.Tests;

/// <summary>Runs the real view and view model on an STA dispatcher with a fake decoder.</summary>
public sealed class ViewerSessionTests
{
    [Fact]
    public void OpensPagesThroughZoomsAndCloses()
    {
        RunOnSta(async () =>
        {
            var shell = new FakeShell();
            var imaging = new FakeImaging();
            var pages = new FakePages(5) { FailingIndex = 3 };
            var closed = 0;
            var progress = new List<ViewerProgress>();
            var request = new ViewerRequest
            {
                Pages = pages,
                StartIndex = 1,
                OnClosed = () => closed++,
                OnProgress = progress.Add,
            };
            var vm = new ViewerViewModel(request, imaging, shell, new ViewerSettings(512, false), new NullLog());
            var view = new ViewerView(vm);
            var window = new Window { Content = view, Width = 800, Height = 600, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            window.Show();
            try
            {
                vm.Start();
                await WaitFor(() => vm.HasImage);
                Assert.Equal(1, vm.Index);
                Assert.Equal(400, vm.Current!.PixelWidth);

                // Preload: the next pages and the previous one are decoded without being shown (page 3 is broken).
                await WaitFor(() => imaging.Decoded.Contains(0) && imaging.Decoded.Contains(2));
                Assert.DoesNotContain(4, imaging.Decoded); // only two frames ahead

                vm.Next();
                Assert.Equal(2, vm.Index);
                Assert.True(vm.HasImage); // came from the cache, no spinner

                vm.Next();
                await WaitFor(() => vm.HasError);
                Assert.Equal(3, vm.Index);
                Assert.False(vm.HasImage);

                vm.Next();
                await WaitFor(() => vm.HasImage);
                Assert.Equal(4, vm.Index);

                vm.Next(); // at the end: stays, without a notification
                Assert.Equal(4, vm.Index);
                Assert.Empty(shell.Toasts);

                vm.First();
                await WaitFor(() => vm.HasImage && vm.Index == 0);
                vm.Previous();
                Assert.Equal(0, vm.Index);

                Assert.True(vm.ZoomPercent > 0);
                await vm.CloseAsync();
                Assert.Equal(1, closed);
                Assert.True(pages.Disposed);
                Assert.NotEmpty(progress);
                Assert.Null(vm.Current);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void BookModePairsPagesAndFollowsReadingDirection()
    {
        RunOnSta(async () =>
        {
            var shell = new FakeShell();
            var imaging = new FakeImaging { SizeOf = i => i == 3 ? (80, 60) : (60, 80) }; // page 3 is a landscape spread
            var pages = new FakePages(7);
            var progress = new List<ViewerProgress>();
            var changed = new List<BookSettings>();
            var request = new ViewerRequest
            {
                Pages = pages,
                Mode = ViewerMode.Book,
                Title = "Book",
                Book = new BookSettings(ReadingDirection.RightToLeft, SpreadMode.Double, CoverAlone: true),
                OnProgress = progress.Add,
                OnBookSettingsChanged = changed.Add,
            };
            var vm = new ViewerViewModel(request, imaging, shell, new ViewerSettings(512, false), new NullLog());
            var view = new ViewerView(vm);
            var window = new Window { Content = view, Width = 800, Height = 600, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            window.Show();
            try
            {
                async Task Expect(int first, int last)
                {
                    await WaitFor(() => vm.Frame == new PageFrame(first, last) && !vm.IsLoading);
                }

                vm.Start();
                await Expect(0, 0); // cover alone
                vm.Next();
                await Expect(1, 2);
                Assert.True(vm.IsSpread);

                // Right-to-left: the lower page is on the right.
                Assert.True(Canvas.GetLeft(view.PageImage) > Canvas.GetLeft(view.PageImage2));
                Assert.Equal(view.PageImage.Height, view.PageImage2.Height);

                vm.Next();
                await Expect(3, 3); // the wide page stands alone once its size is known
                vm.Next();
                await Expect(4, 5);
                vm.Next();
                await Expect(6, 6);
                vm.Next();
                Assert.Equal(new PageFrame(6, 6), vm.Frame);
                Assert.Empty(shell.Toasts); // last page: no notification

                vm.Previous();
                await Expect(4, 5);
                vm.Previous();
                await Expect(3, 3);
                vm.Previous();
                await Expect(1, 2);
                vm.Previous();
                await Expect(0, 0);

                // D cycles two pages → strip → one page → two pages, keeping the current page; M flips the layout.
                vm.GoTo(1);
                await Expect(1, 2);
                vm.ToggleSpread();
                Assert.True(vm.IsStrip);
                Assert.Equal(1, vm.StripCurrent);
                vm.ToggleSpread();
                await Expect(1, 1);
                vm.ToggleSpread();
                vm.ToggleDirection();
                await Expect(1, 2);
                Assert.True(Canvas.GetLeft(view.PageImage) < Canvas.GetLeft(view.PageImage2));
                Assert.Equal(
                    [new BookSettings(ReadingDirection.RightToLeft, SpreadMode.Double, true, PageFlow.Strip),
                     new BookSettings(ReadingDirection.RightToLeft, SpreadMode.Single, true),
                     new BookSettings(ReadingDirection.RightToLeft, SpreadMode.Double, true),
                     new BookSettings(ReadingDirection.LeftToRight, SpreadMode.Double, true)],
                    changed);

                vm.Last(); // End: the frame that ends at the last page
                await Expect(5, 6);
                await vm.CloseAsync();
                Assert.True(progress[^1].ReachedEnd);
                Assert.Equal(5, progress[^1].PageIndex);
                Assert.Equal(7, progress[^1].PageCount);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void BookStripRunsRightToLeftWithoutGapsAndKeepsThePage()
    {
        RunOnSta(async () =>
        {
            var imaging = new FakeImaging { SizeOf = i => i == 3 ? (80, 60) : (60, 80) };
            var progress = new List<ViewerProgress>();
            var changed = new List<BookSettings>();
            var request = new ViewerRequest
            {
                Pages = new FakePages(40),
                Mode = ViewerMode.Book,
                StartIndex = 2,
                Book = new BookSettings(ReadingDirection.RightToLeft, SpreadMode.Double, true, PageFlow.Strip),
                OnProgress = progress.Add,
                OnBookSettingsChanged = changed.Add,
            };
            var vm = new ViewerViewModel(request, imaging, new FakeShell(), new ViewerSettings(512, false), new NullLog());
            var view = new ViewerView(vm);
            var window = new Window { Content = view, Width = 800, Height = 600, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            window.Show();
            try
            {
                var strip = view.StripView;
                vm.Start();
                await WaitFor(() => strip.IsActive && strip.Slots.Count > 0 && strip.Slots.Values.All(s => s.Source is not null));
                Assert.True(vm.IsStrip);
                Assert.Equal(2, vm.StripCurrent);
                Assert.False(view.Spread.IsVisible);

                // Right-to-left: page 2's reading-start (right) edge is the viewport's right edge.
                var stageWidth = view.Stage.ActualWidth;
                Assert.Equal(stageWidth, Canvas.GetLeft(strip.Slots[2]) + strip.Slots[2].Width, 0);
                AssertAdjacent(strip, rightToLeft: true);
                Assert.True(strip.Slots.Count <= 8, $"{strip.Slots.Count} slots for a two-page view");

                // Forward moves the content right (the reader looks further left).
                var before = Canvas.GetLeft(strip.Slots[2]);
                strip.ScrollBy(1, animate: false);
                Assert.True(vm.StripCurrent > 2);
                if (strip.Slots.TryGetValue(2, out var stillThere))
                {
                    Assert.True(Canvas.GetLeft(stillThere) > before);
                }

                await WaitFor(() => strip.Slots.Values.All(s => s.Source is not null));
                AssertAdjacent(strip, rightToLeft: true);

                // "Pages shown" (D): strip → one page → two pages → strip, keeping the page.
                var page = vm.StripCurrent;
                vm.ToggleSpread();
                Assert.False(vm.IsStrip);
                Assert.False(strip.IsActive);
                Assert.Equal(page, vm.Frame.First);
                vm.ToggleSpread();
                Assert.False(vm.IsStrip);
                vm.ToggleSpread();
                Assert.True(strip.IsActive);
                Assert.Equal(page, vm.StripCurrent);
                Assert.Equal(
                    [new BookSettings(ReadingDirection.RightToLeft, SpreadMode.Single, true, PageFlow.Paged),
                     new BookSettings(ReadingDirection.RightToLeft, SpreadMode.Double, true, PageFlow.Paged),
                     new BookSettings(ReadingDirection.RightToLeft, SpreadMode.Double, true, PageFlow.Strip)],
                    changed);

                // "Cover alone" does nothing in a strip.
                vm.ToggleCoverAlone();
                Assert.Equal(3, changed.Count);

                strip.End();
                await vm.CloseAsync();
                Assert.True(progress[^1].ReachedEnd);
                Assert.Equal(40, progress[^1].PageCount);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void GalleryStripRunsLeftToRightAndSizesArrivingDoNotMoveThePage()
    {
        RunOnSta(async () =>
        {
            // Pages before 10 are wide: their sizes differ from the portrait placeholder.
            var imaging = new FakeImaging { SizeOf = i => i < 10 ? (120, 80) : (60, 80) };
            var request = new ViewerRequest { Pages = new FakePages(30), StartIndex = 10 };
            var vm = new ViewerViewModel(request, imaging, new FakeShell(),
                                         new ViewerSettings(512, false, GalleryFlow: PageFlow.Strip), new NullLog());
            var view = new ViewerView(vm);
            var window = new Window { Content = view, Width = 800, Height = 600, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            window.Show();
            try
            {
                var strip = view.StripView;
                vm.Start();
                await WaitFor(() => strip.IsActive && Enumerable.Range(0, 10).All(i => vm.Strip!.HasSize(i))
                                    && strip.Slots.Values.All(s => s.Source is not null));
                Assert.Equal(10, vm.StripCurrent);
                Assert.Equal(0, Canvas.GetLeft(strip.Slots[10]), 0); // still at the left edge
                AssertAdjacent(strip, rightToLeft: false);

                var before = Canvas.GetLeft(strip.Slots[10]);
                strip.ScrollBy(0.25, animate: false);
                Assert.True(Canvas.GetLeft(strip.Slots[10]) < before); // forward = content moves left

                strip.Home();
                Assert.Equal(0, vm.StripCurrent);
                strip.ScrollBy(-0.25, animate: false); // at the start: stays and tells the user
                Assert.Equal(0, vm.StripCurrent);
            }
            finally
            {
                await vm.CloseAsync();
                window.Close();
            }
        });
    }

    /// <summary>Every slot touches its neighbour (within half a pixel), in reading order.</summary>
    private static void AssertAdjacent(StripPresenter strip, bool rightToLeft)
    {
        var slots = strip.Slots.OrderBy(p => p.Key).ToList();
        for (var k = 1; k < slots.Count; k++)
        {
            Assert.Equal(slots[k - 1].Key + 1, slots[k].Key);
            var previous = slots[k - 1].Value;
            var next = slots[k].Value;
            var gap = rightToLeft
                ? Canvas.GetLeft(previous) - (Canvas.GetLeft(next) + next.Width)
                : Canvas.GetLeft(next) - (Canvas.GetLeft(previous) + previous.Width);
            Assert.True(Math.Abs(gap) <= 0.5, $"gap {gap} between pages {slots[k - 1].Key} and {slots[k].Key}");
        }
    }

    [Fact]
    public void RightClickSendsThePageToTheImageWindow()
    {
        RunOnSta(async () =>
        {
            var windows = new FakeWindows();
            var request = new ViewerRequest { Pages = new FakePages(3), StartIndex = 2 };
            var vm = new ViewerViewModel(request, new FakeImaging(), new FakeShell(), new ViewerSettings(512, false), new NullLog())
            {
                ImageWindows = windows,
            };
            vm.Start();
            await vm.OpenInWindowAsync(2);
            var sent = Assert.Single(windows.Requests);
            Assert.Equal("page2.png", sent.Name);
            Assert.Null(sent.FilePath);
            await using var stream = await sent.Open(CancellationToken.None);
            Assert.Equal(2, stream.ReadByte());
            await vm.CloseAsync();
        });
    }

    private sealed class FakeWindows : IImageWindowService
    {
        public List<ImageWindowRequest> Requests { get; } = [];

        public Task OpenAsync(ImageWindowRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

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
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The UI thread did not finish.");
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException("UI test failed: " + failure);
        }
    }

    private sealed class FakePages(int count) : IPageSource
    {
        public int FailingIndex { get; init; } = -1;

        public bool Disposed { get; private set; }

        public int Count => count;

        public string GetName(int index) => $"page{index}.png";

        public string? GetFilePath(int index) => null;

        public Task<Stream> OpenAsync(int index, CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream(index == FailingIndex ? [] : [(byte)index]));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeImaging : IImagingService
    {
        private readonly object _lock = new();
        private readonly HashSet<int> _decoded = [];

        public HashSet<int> Decoded
        {
            get
            {
                lock (_lock)
                {
                    return [.. _decoded];
                }
            }
        }

        /// <summary>Pixel size of a page by index.</summary>
        public Func<int, (int Width, int Height)> SizeOf { get; init; } = _ => (400, 300);

        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".png" };

        public bool IsSupported(string pathOrExtension) => true;

        public Task<ImageInfo?> ReadInfoAsync(Stream seekable, CancellationToken ct = default)
        {
            if (seekable.Length == 0)
            {
                return Task.FromResult<ImageInfo?>(null);
            }

            var (width, height) = SizeOf(seekable.ReadByte());
            return Task.FromResult<ImageInfo?>(new ImageInfo(width, height));
        }

        public Task<BitmapSource> DecodeAsync(Stream seekable, DecodeOptions options, CancellationToken ct = default)
        {
            if (seekable.Length == 0)
            {
                throw new ImageDecodeException("empty");
            }

            var index = seekable.ReadByte();
            lock (_lock)
            {
                _decoded.Add(index);
            }

            var (w, h) = SizeOf(index);
            var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, new byte[w * h * 4], w * 4);
            bitmap.Freeze();
            return Task.FromResult<BitmapSource>(bitmap);
        }

        public byte[] EncodeJpeg(BitmapSource source, int quality) => [];

        public void RegisterDecoder(IPluginContext owner, IImageDecoder decoder) { }
    }

    private sealed class NullLog : ILog
    {
        public void Debug(string message) { }

        public void Info(string message) { }

        public void Warn(string message, Exception? ex = null) { }

        public void Error(string message, Exception? ex = null) { }
    }

    private sealed class FakeShell : IShellService
    {
        public List<string> Toasts { get; } = [];

        public Window MainWindow => throw new NotSupportedException();

        public IReadOnlyList<PageDescriptor> Pages => [];

        public string? CurrentPageId => null;

        public bool IsFullScreen { get; set; }

        public AppTheme Theme { get; set; }

        public void AddPage(IPluginContext owner, PageDescriptor page) { }

        public bool Navigate(string pageId, object? parameter = null) => false;

        public void ShowOverlay(IPluginContext owner, FrameworkElement view) { }

        public void CloseOverlay(FrameworkElement view) { }

        public void AddResources(IPluginContext owner, ResourceDictionary dictionary) { }

        public void Notify(string message, NotifySeverity severity = NotifySeverity.Info) => Toasts.Add(message);

        public IBackgroundTask BeginTask(IPluginContext owner, string title) => throw new NotSupportedException();

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

        public Task<bool> ConfirmAsync(string title, string message, string? confirmText = null, string? cancelText = null) =>
            Task.FromResult(false);

        public Task<bool> ShowDialogAsync(DialogDescriptor dialog) => Task.FromResult(false);
    }
}
