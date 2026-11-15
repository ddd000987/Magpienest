using System.Windows;
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

                vm.Next(); // at the end: stays and tells the user
                Assert.Equal(4, vm.Index);
                Assert.Single(shell.Toasts);

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

        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".png" };

        public bool IsSupported(string pathOrExtension) => true;

        public Task<ImageInfo?> ReadInfoAsync(Stream seekable, CancellationToken ct = default) =>
            Task.FromResult<ImageInfo?>(seekable.Length == 0 ? null : new ImageInfo(400, 300));

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

            var bitmap = BitmapSource.Create(400, 300, 96, 96, PixelFormats.Bgr32, null, new byte[400 * 300 * 4], 400 * 4);
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
