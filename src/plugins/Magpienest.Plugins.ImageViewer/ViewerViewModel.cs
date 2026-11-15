using System.Diagnostics;
using System.Globalization;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Magpienest.Sdk;
using Magpienest.Sdk.Imaging;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Shell;
using Magpienest.Sdk.Viewing;

namespace Magpienest.Plugins.ImageViewer;

/// <summary>A decoded page (or the reason it could not be decoded).</summary>
/// <param name="Bitmap">The bitmap; null on failure.</param>
/// <param name="PixelWidth">Original oriented width (the bitmap may be reduced).</param>
/// <param name="PixelHeight">Original oriented height.</param>
/// <param name="Failed">Decoding failed.</param>
internal sealed record PageEntry(BitmapSource? Bitmap, int PixelWidth, int PixelHeight, bool Failed)
{
    public long Bytes => Bitmap is null ? 1024 : (long)Bitmap.PixelWidth * Bitmap.PixelHeight * 4;
}

/// <summary>Viewer settings read when a viewer opens.</summary>
/// <param name="CacheBudgetMb">Decoded-page cache budget.</param>
/// <param name="StretchSmall">Fit scales small images up.</param>
internal sealed record ViewerSettings(int CacheBudgetMb, bool StretchSmall);

/// <summary>
/// One open viewer: navigation, decoding, page cache and preloading. UI thread only.
/// Which pages form a frame always comes from <see cref="FrameLayout"/>.
/// </summary>
internal sealed partial class ViewerViewModel : ObservableObject
{
    /// <summary>Images above this are decoded reduced to it (keeping aspect).</summary>
    public const long MaxDecodePixels = 40_000_000;

    private static readonly TimeSpan SpinnerDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan EdgeToastInterval = TimeSpan.FromSeconds(1.5);

    private readonly ViewerRequest _request;
    private readonly IImagingService _imaging;
    private readonly IShellService _shell;
    private readonly ILog _log;
    private readonly PageCache<PageEntry> _cache;
    private readonly Dictionary<int, CancellationTokenSource> _inflight = [];
    private readonly SemaphoreSlim _decodeSlots = new(2, 2);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _spinnerTimer;
    private readonly Stopwatch _sinceProgress = Stopwatch.StartNew();
    private readonly Stopwatch _sinceEdgeToast = Stopwatch.StartNew();
    private readonly bool _wasFullScreen;
    private PageFrame _frame;
    private bool _edgeToastShown;
    private bool _closed;

    public ViewerViewModel(ViewerRequest request, IImagingService imaging, IShellService shell, ViewerSettings settings, ILog log)
    {
        _request = request;
        _imaging = imaging;
        _shell = shell;
        _log = log;
        Settings = settings;
        _cache = new PageCache<PageEntry>(Math.Clamp(settings.CacheBudgetMb, 256, 4096) * 1024L * 1024L);
        _wasFullScreen = shell.IsFullScreen;
        _spinnerTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = SpinnerDelay };
        _spinnerTimer.Tick += (_, _) =>
        {
            _spinnerTimer.Stop();
            if (IsLoading)
            {
                ShowSpinner = true;
                Current = null; // stop showing the previous page once loading takes noticeably long
            }
        };
    }

    /// <summary>Raised when a different page is shown (the view resets its transform to Fit).</summary>
    public event Action? PageChanged;

    /// <summary>Raised after the viewer closed.</summary>
    public event Action? Closed;

    public ViewerSettings Settings { get; }

    public int Count => _request.Pages.Count;

    /// <summary>First page of the current frame.</summary>
    public int Index => _frame.First;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    public partial PageEntry? Current { get; private set; }

    public bool HasImage => Current?.Bitmap is not null;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool ShowSpinner { get; private set; }

    [ObservableProperty]
    public partial bool HasError { get; private set; }

    [ObservableProperty]
    public partial bool IsInfoVisible { get; set; }

    /// <summary>Set by the view from its transform.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText), nameof(InfoText))]
    public partial double ZoomPercent { get; set; } = 100;

    public string PageName => Count == 0 ? "" : SafeName(_frame.First);

    public string TitleText => Count == 0 ? "" : Loc.Format("Viewer.TitleFormat", PageName, _frame.First + 1, Count);

    public string ZoomText => Loc.Format("Viewer.ZoomFormat", Math.Round(ZoomPercent).ToString(CultureInfo.CurrentCulture));

    public string ErrorText => Loc.Format("Viewer.DecodeFailed", PageName);

    public string InfoText
    {
        get
        {
            if (Count == 0)
            {
                return "";
            }

            var size = Current is { Failed: false } entry ? Loc.Format("Viewer.PixelSize", entry.PixelWidth, entry.PixelHeight) : "";
            return Loc.Format("Viewer.InfoFormat", PageName, size, FileSizeText(), ZoomText);
        }
    }

    public bool IsFullScreen => _shell.IsFullScreen;

    public void Start() => Navigate(Math.Clamp(_request.StartIndex, 0, Math.Max(0, Count - 1)), forward: true, initial: true);

    public void Next()
    {
        if (_frame.Last + 1 >= Count)
        {
            EdgeToast("Viewer.LastImage");
            return;
        }

        Navigate(_frame.Last + 1, forward: true);
    }

    public void Previous()
    {
        if (_frame.First <= 0)
        {
            EdgeToast("Viewer.FirstImage");
            return;
        }

        Navigate(Backward(_frame.First - 1).First, forward: false);
    }

    public void First()
    {
        if (_frame.First > 0)
        {
            Navigate(0, forward: false);
        }
    }

    public void Last()
    {
        if (_frame.Last < Count - 1)
        {
            Navigate(Backward(Count - 1).First, forward: true);
        }
    }

    public void ToggleFullScreen()
    {
        _shell.IsFullScreen = !_shell.IsFullScreen;
        OnPropertyChanged(nameof(IsFullScreen));
    }

    /// <summary>Esc: leave fullscreen first, else close.</summary>
    public Task EscapeAsync()
    {
        if (_shell.IsFullScreen)
        {
            _shell.IsFullScreen = false;
            OnPropertyChanged(nameof(IsFullScreen));
            return Task.CompletedTask;
        }

        return CloseAsync();
    }

    public async Task CloseAsync()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _spinnerTimer.Stop();
        _lifetime.Cancel();
        foreach (var cts in _inflight.Values)
        {
            cts.Cancel();
        }

        _inflight.Clear();
        _cache.Clear();
        Current = null;
        ReportProgress();
        if (_shell.IsFullScreen != _wasFullScreen)
        {
            _shell.IsFullScreen = _wasFullScreen;
        }

        Closed?.Invoke();
        try
        {
            await _request.Pages.DisposeAsync();
        }
        catch (Exception ex)
        {
            _log.Warn("Disposing the page source failed.", ex);
        }

        try
        {
            _request.OnClosed?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Error("OnClosed failed.", ex);
        }
    }

    /// <summary>Re-reads localized texts.</summary>
    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(ZoomText));
        OnPropertyChanged(nameof(InfoText));
        OnPropertyChanged(nameof(ErrorText));
    }

    private PageFrame Forward(int index) => FrameLayout.Forward(index, Count, SpreadMode.Single, false, _ => false);

    private PageFrame Backward(int index) => FrameLayout.Backward(index, Count, SpreadMode.Single, false, _ => false);

    private void Navigate(int index, bool forward, bool initial = false)
    {
        if (_closed || Count == 0)
        {
            return;
        }

        _frame = Forward(index);
        _edgeToastShown = false;
        _cache.Pin(Pages(_frame));
        RaiseFrameTexts();
        ShowCurrent();
        Preload(forward);
        if (!initial && _sinceProgress.Elapsed >= ProgressInterval)
        {
            ReportProgress();
        }
    }

    private void ShowCurrent()
    {
        if (_cache.TryGet(_frame.First, out var entry))
        {
            _spinnerTimer.Stop();
            IsLoading = false;
            ShowSpinner = false;
            Current = entry;
            HasError = entry.Failed;
            OnPropertyChanged(nameof(InfoText));
            OnPropertyChanged(nameof(ErrorText));
            PageChanged?.Invoke();
            return;
        }

        IsLoading = true;
        HasError = false;
        if (!_spinnerTimer.IsEnabled && !ShowSpinner)
        {
            _spinnerTimer.Start();
        }
    }

    /// <summary>Current frame first, then 2 frames ahead in the travel direction and 1 behind; drops unwanted work.</summary>
    private void Preload(bool forward)
    {
        var wanted = new List<int>(Pages(_frame));
        var ahead = _frame;
        for (var i = 0; i < 2 && TryStep(ahead, forward, out ahead); i++)
        {
            wanted.AddRange(Pages(ahead));
        }

        if (TryStep(_frame, !forward, out var behind))
        {
            wanted.AddRange(Pages(behind));
        }

        foreach (var index in _inflight.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            _inflight[index].Cancel();
            _inflight.Remove(index);
        }

        foreach (var index in wanted)
        {
            if (!_cache.Contains(index) && !_inflight.ContainsKey(index))
            {
                _ = LoadAsync(index);
            }
        }
    }

    private bool TryStep(PageFrame from, bool forward, out PageFrame next)
    {
        if (forward ? from.Last + 1 < Count : from.First > 0)
        {
            next = forward ? Forward(from.Last + 1) : Backward(from.First - 1);
            return true;
        }

        next = from;
        return false;
    }

    private async Task LoadAsync(int index)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _inflight[index] = cts;
        PageEntry? entry = null;
        try
        {
            entry = await DecodeAsync(index, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_inflight.TryGetValue(index, out var current) && ReferenceEquals(current, cts))
            {
                _inflight.Remove(index);
            }

            cts.Dispose();
        }

        if (entry is null || _closed)
        {
            return;
        }

        _cache.Add(index, entry, entry.Bytes);
        if (index == _frame.First && IsLoading)
        {
            ShowCurrent();
        }
    }

    private async Task<PageEntry> DecodeAsync(int index, CancellationToken ct)
    {
        await _decodeSlots.WaitAsync(ct);
        try
        {
            return await Task.Run(
                async () =>
                {
                    try
                    {
                        await using var stream = await _request.Pages.OpenAsync(index, ct);
                        var info = await _imaging.ReadInfoAsync(stream, ct);
                        stream.Position = 0;
                        var options = new DecodeOptions();
                        if (info is not null && (long)info.PixelWidth * info.PixelHeight > MaxDecodePixels)
                        {
                            var factor = Math.Sqrt(MaxDecodePixels / ((double)info.PixelWidth * info.PixelHeight));
                            options = new DecodeOptions(Math.Max(1, (int)(info.PixelWidth * factor)), Math.Max(1, (int)(info.PixelHeight * factor)));
                        }

                        var bitmap = await _imaging.DecodeAsync(stream, options, ct);
                        return new PageEntry(bitmap, info?.PixelWidth ?? bitmap.PixelWidth, info?.PixelHeight ?? bitmap.PixelHeight, false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"Page {index} could not be decoded.", ex);
                        return new PageEntry(null, 0, 0, true);
                    }
                },
                ct);
        }
        finally
        {
            _decodeSlots.Release();
        }
    }

    private void EdgeToast(string key)
    {
        if (_edgeToastShown && _sinceEdgeToast.Elapsed < EdgeToastInterval)
        {
            return;
        }

        _edgeToastShown = true;
        _sinceEdgeToast.Restart();
        _shell.Notify(Loc.Get(key));
    }

    private void ReportProgress()
    {
        if (_request.OnProgress is null || Count == 0)
        {
            return;
        }

        var elapsed = _sinceProgress.Elapsed;
        _sinceProgress.Restart();
        try
        {
            _request.OnProgress(new ViewerProgress(_frame.First, Count, _frame.Last >= Count - 1, elapsed));
        }
        catch (Exception ex)
        {
            _log.Error("OnProgress failed.", ex);
        }
    }

    private void RaiseFrameTexts()
    {
        OnPropertyChanged(nameof(Index));
        OnPropertyChanged(nameof(PageName));
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(InfoText));
        OnPropertyChanged(nameof(ErrorText));
    }

    private string FileSizeText()
    {
        try
        {
            var path = _request.Pages.GetFilePath(_frame.First);
            if (path is not null && File.Exists(path))
            {
                var bytes = new FileInfo(path).Length;
                return bytes >= 1024 * 1024
                    ? Loc.Format("Viewer.SizeMB", (bytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.CurrentCulture))
                    : Loc.Format("Viewer.SizeKB", Math.Max(1, bytes / 1024).ToString(CultureInfo.CurrentCulture));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return "";
    }

    private string SafeName(int index)
    {
        try
        {
            return _request.Pages.GetName(index);
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static IEnumerable<int> Pages(PageFrame frame) => Enumerable.Range(frame.First, frame.Last - frame.First + 1);
}
