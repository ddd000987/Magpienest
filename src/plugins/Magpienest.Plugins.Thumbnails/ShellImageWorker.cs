using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Magpienest.Sdk;

namespace Magpienest.Plugins.Thumbnails;

/// <summary>
/// Runs <c>IShellItemImageFactory.GetImage</c> calls on one dedicated STA thread (the shell requires an STA).
/// Jobs are served first-in first-out; a job whose token is cancelled before it starts is dropped.
/// </summary>
internal sealed class ShellImageWorker : IDisposable
{
    private const int ThumbnailOnly = 0x8; // SIIGBF_THUMBNAILONLY
    private const int IconOnly = 0x4;      // SIIGBF_ICONONLY
    private const int BiggerSizeOk = 0x1;  // SIIGBF_BIGGERSIZEOK

    private static readonly Guid ImageFactoryIid = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    private sealed record Job(string Path, int PixelSize, bool IconOnly, CancellationToken Token, TaskCompletionSource<BitmapSource?> Done);

    private readonly BlockingCollection<Job> _queue = new();
    private readonly ILog _log;
    private readonly Thread _thread;
    private int _disposed;

    public ShellImageWorker(ILog log)
    {
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "Magpienest shell images" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Queues a request; the task result is null on any failure.</summary>
    public Task<BitmapSource?> GetImageAsync(string path, int pixelSize, bool iconOnly, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<BitmapSource?>(ct);
        }

        var job = new Job(path, pixelSize, iconOnly, ct, new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            _queue.Add(job);
        }
        catch (InvalidOperationException)
        {
            return Task.FromResult<BitmapSource?>(null); // shut down
        }

        return job.Done.Task;
    }

    /// <summary>Stops accepting jobs, cancels the waiting ones and stops the thread.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.CompleteAdding();
        // The thread is a background thread: a shell call stuck on a dead network share cannot keep the app alive.
        _thread.Join(TimeSpan.FromSeconds(2));
        while (_queue.TryTake(out var job))
        {
            job.Done.TrySetResult(null);
        }
    }

    private void Run()
    {
        foreach (var job in _queue.GetConsumingEnumerable())
        {
            if (job.Token.IsCancellationRequested)
            {
                job.Done.TrySetCanceled(job.Token);
                continue;
            }

            BitmapSource? result = null;
            try
            {
                result = Create(job);
            }
            catch (Exception ex)
            {
                _log.Debug($"Shell image for '{job.Path}' failed: {ex.Message}");
            }

            job.Done.TrySetResult(result);
        }
    }

    private static BitmapSource? Create(Job job)
    {
        if (job.PixelSize <= 0 || string.IsNullOrWhiteSpace(job.Path))
        {
            return null;
        }

        var iid = ImageFactoryIid;
        SHCreateItemFromParsingName(job.Path, IntPtr.Zero, ref iid, out var factory);
        var hBitmap = IntPtr.Zero;
        try
        {
            var flags = BiggerSizeOk | (job.IconOnly ? IconOnly : ThumbnailOnly);
            var hr = factory.GetImage(new NativeSize { Cx = job.PixelSize, Cy = job.PixelSize }, flags, out hBitmap);
            if (hr < 0 || hBitmap == IntPtr.Zero)
            {
                return null;
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero)
            {
                DeleteObject(hBitmap);
            }

            Marshal.ReleaseComObject(factory);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Cx;
        public int Cy;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindContext,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface, IidParameterIndex = 2)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);
}
