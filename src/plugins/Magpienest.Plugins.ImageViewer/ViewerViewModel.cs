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
/// <param name="DefaultBook">Book-mode defaults; null → right-to-left, two pages, cover alone.</param>
/// <param name="GalleryFlow">Paged or strip in gallery mode.</param>
internal sealed record ViewerSettings(int CacheBudgetMb, bool StretchSmall, BookSettings? DefaultBook = null,
                                      PageFlow GalleryFlow = PageFlow.Paged)
{
    public static readonly BookSettings BuiltInBook = new(ReadingDirection.RightToLeft, SpreadMode.Double, true);

    public BookSettings Book => DefaultBook ?? BuiltInBook;
}

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
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(5);

    private readonly ViewerRequest _request;
    private readonly IImagingService _imaging;
    private readonly IShellService _shell;
    private readonly ILog _log;
    private readonly PageCache<PageEntry> _cache;
    private readonly Dictionary<int, CancellationTokenSource> _inflight = [];
    private readonly Dictionary<int, bool> _wide = []; // known page orientations, per book
    private readonly SemaphoreSlim _decodeSlots = new(2, 2);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _spinnerTimer;
    private readonly Stopwatch _sinceProgress = Stopwatch.StartNew();
    private readonly Stopwatch _sinceInput = Stopwatch.StartNew();
    private readonly bool _wasFullScreen;
    private PageFrame _frame;
    private bool _anchorBack;  // the frame was laid out backwards from its last page
    private bool _travelForward = true;
    private int _pendingStep;  // a step requested before the current frame's page sizes were known
    private bool _closed;

    // Strip mode (phase-2b T2.8).
    private readonly Dictionary<int, (int Width, int Height)> _sizes = []; // known oriented pixel sizes, per book
    private readonly HashSet<int> _infoInflight = [];
    private readonly SemaphoreSlim _infoSlots = new(2, 2);
    private CancellationTokenSource _stripCts = new();
    private PageFlow _galleryFlow;
    private int _stripHeight;       // displayed page height in device pixels
    private bool _stripAtEnd;

    public ViewerViewModel(ViewerRequest request, IImagingService imaging, IShellService shell, ViewerSettings settings, ILog log)
    {
        _request = request;
        _imaging = imaging;
        _shell = shell;
        _log = log;
        Settings = settings;
        _galleryFlow = settings.GalleryFlow;
        Book = request.Book ?? settings.Book;
        _cache = new PageCache<PageEntry>(Math.Clamp(settings.CacheBudgetMb, 256, 4096) * 1024L * 1024L);
        _stripCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _wasFullScreen = shell.IsFullScreen;
        _spinnerTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = SpinnerDelay };
        _spinnerTimer.Tick += (_, _) =>
        {
            _spinnerTimer.Stop();
            if (IsLoading)
            {
                ShowSpinner = true;
                ShowFrame(partial: true); // stop showing the previous frame once loading takes noticeably long
            }
        };
    }

    /// <summary>Raised when a different frame (or a newly loaded page of it) is shown; the view resets to Fit.</summary>
    public event Action? PageChanged;

    /// <summary>Raised after the viewer closed.</summary>
    public event Action? Closed;

    public ViewerSettings Settings { get; }

    /// <summary>The Image Window plugin's service, when it is active.</summary>
    public IImageWindowService? ImageWindows { get; init; }

    public int Count => _request.Pages.Count;

    /// <summary>The page's file on disk; null inside an archive.</summary>
    public string? FilePathOf(int index) => index >= 0 && index < Count ? _request.Pages.GetFilePath(index) : null;

    /// <summary>Opens one page in a borderless window (right-click menu).</summary>
    public async Task OpenInWindowAsync(int index)
    {
        if (ImageWindows is not { } windows || index < 0 || index >= Count)
        {
            return;
        }

        var pages = _request.Pages;
        var name = pages.GetName(index);
        try
        {
            await windows.OpenAsync(new ImageWindowRequest
            {
                Name = name,
                FilePath = pages.GetFilePath(index),
                Open = ct => pages.OpenAsync(index, ct),
            }, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            // The viewer closed while the page was being read.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ImageDecodeException or ObjectDisposedException)
        {
            _log.Warn($"Opening page {index} in a window failed.", ex);
            _shell.Notify(Loc.Format("Viewer.DecodeFailed", name), NotifySeverity.Error);
        }
    }

    /// <summary>Selects the page's file in Explorer.</summary>
    public void ShowInExplorer(int index)
    {
        if (FilePathOf(index) is not { } path)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log.Warn("Opening Explorer failed.", ex);
        }
    }

    public bool IsBook => _request.Mode == ViewerMode.Book;

    /// <summary>Paged or one continuous strip.</summary>
    public PageFlow Flow => IsBook ? Book.Flow : _galleryFlow;

    public bool IsStrip => Flow == PageFlow.Strip;

    public bool IsPaged => !IsStrip;

    /// <summary>Tooltip of the "pages shown" button.</summary>
    public string LayoutTip => Loc.Get(IsBook ? "Viewer.Book.SpreadTip" : "Viewer.LayoutTip");

    /// <summary>Called when the user toggles the gallery flow (saved as the viewer default).</summary>
    public Action<PageFlow>? SaveGalleryFlow { get; init; }

    /// <summary>Page positions in strip mode; null when paged.</summary>
    public StripLayout? Strip { get; private set; }

    /// <summary>The current page in strip mode (the page at the reading-start edge + 10 % of the view).</summary>
    public int StripCurrent { get; private set; }

    /// <summary>A page's image or size became known in strip mode: re-render.</summary>
    public event Action? StripChanged;

    /// <summary>The slider or Home/End asked for a page in strip mode; <see cref="Count"/> means the end.</summary>
    public event Action<int>? StripGoToRequested;

    /// <summary>A decoded page, if cached.</summary>
    public PageEntry? EntryAt(int index) => _cache.TryGet(index, out var entry) ? entry : null;

    /// <summary>The page the texts describe: the current strip page, or the frame's first page.</summary>
    private int DisplayIndex => Strip is not null ? StripCurrent : _frame.First;

    /// <summary>Current book settings (book mode).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RightToLeft), nameof(SpreadText), nameof(DirectionText), nameof(CoverText))]
    public partial BookSettings Book { get; private set; }

    /// <summary>Lower page index on the right.</summary>
    public bool RightToLeft => IsBook && Book.Direction == ReadingDirection.RightToLeft;

    /// <summary>First page of the current frame.</summary>
    public int Index => _frame.First;

    /// <summary>Current frame.</summary>
    public PageFrame Frame => _frame;

    /// <summary>The frame shows two pages.</summary>
    public bool IsSpread => _frame.Last > _frame.First;

    /// <summary>The frame's first page; null while loading.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    public partial PageEntry? Current { get; private set; }

    /// <summary>The frame's second page (spreads only); null while loading or for single frames.</summary>
    [ObservableProperty]
    public partial PageEntry? Second { get; private set; }

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

    public string PageName => Count == 0 ? "" : SafeName(DisplayIndex);

    public string TitleText => Count == 0 ? ""
        : IsBook ? Loc.Format("Viewer.BookTitleFormat", _request.Title ?? PageName, PageText)
        : Loc.Format("Viewer.TitleFormat", PageName, DisplayIndex + 1, Count);

    /// <summary>"12–13 / 180" or "12 / 180".</summary>
    public string PageText => Count == 0 ? ""
        : IsSpread ? Loc.Format("Viewer.PageRangeDouble", _frame.First + 1, _frame.Last + 1, Count)
        : Loc.Format("Viewer.PageRangeSingle", _frame.First + 1, Count);

    /// <summary>What the "pages shown" button says: one page / two pages / strip (books), one picture / strip (gallery).</summary>
    public string SpreadText => Loc.Get(
        IsStrip ? "Viewer.Flow.Strip"
        : !IsBook ? "Viewer.Layout.OneImage"
        : Book.Spread == SpreadMode.Double ? "Viewer.Book.TwoPages" : "Viewer.Book.OnePage");

    public string DirectionText => Loc.Get(Book.Direction == ReadingDirection.RightToLeft ? "Viewer.Book.RightToLeft" : "Viewer.Book.LeftToRight");

    public string CoverText => Loc.Get(Book.CoverAlone ? "Viewer.Book.CoverAlone" : "Viewer.Book.CoverPaired");

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

    private SpreadMode Spread => IsBook ? Book.Spread : SpreadMode.Single;

    public void Start()
    {
        var start = Math.Clamp(_request.StartIndex, 0, Math.Max(0, Count - 1));
        if (IsStrip && Count > 0)
        {
            EnterStrip(start);
        }
        else
        {
            Navigate(start, backward: false, initial: true);
        }
    }

    /// <summary>W: paged ↔ strip, keeping the current page.</summary>
    public void ToggleFlow()
    {
        if (_closed || Count == 0)
        {
            return;
        }

        NoteInput();
        var next = IsStrip ? PageFlow.Paged : PageFlow.Strip;
        if (IsBook)
        {
            ChangeBook(Book with { Flow = next });
            return;
        }

        var page = DisplayIndex;
        _galleryFlow = next;
        try
        {
            SaveGalleryFlow?.Invoke(next);
        }
        catch (Exception ex)
        {
            _log.Error("Saving the gallery flow failed.", ex);
        }

        _shell.Notify(SpreadText);
        SwitchFlow(page);
    }

    /// <summary>
    /// Strip mode: the view reports what it shows. Decodes the visible pages ± 2 at the displayed height,
    /// reads sizes for ± 10, and reports progress.
    /// </summary>
    /// <param name="visible">Pages on screen.</param>
    /// <param name="current">Page at the reading-start edge + 10 %.</param>
    /// <param name="atEnd">The end of the strip is on screen.</param>
    /// <param name="pixelHeight">Displayed page height in device pixels.</param>
    public void UpdateStrip(PageFrame visible, int current, bool atEnd, int pixelHeight)
    {
        if (_closed || Strip is null || Count == 0)
        {
            return;
        }

        var first = Math.Clamp(visible.First, 0, Count - 1);
        var frame = new PageFrame(first, Math.Clamp(Math.Max(first, visible.Last), 0, Count - 1));
        current = Math.Clamp(current, 0, Count - 1);
        var moved = current != StripCurrent;
        StripCurrent = current;
        _stripAtEnd = atEnd;
        _stripHeight = Math.Max(1, pixelHeight);
        if (frame != _frame || moved)
        {
            _frame = frame;
            RaiseFrameTexts();
        }

        var from = Math.Max(0, frame.First - 2);
        var to = Math.Min(Count - 1, frame.Last + 2);
        _cache.Pin(Enumerable.Range(from, to - from + 1));
        foreach (var index in _inflight.Keys.Where(k => k < from || k > to).ToList())
        {
            _inflight[index].Cancel();
            _inflight.Remove(index);
        }

        for (var i = from; i <= to; i++)
        {
            if (!_inflight.ContainsKey(i) && !IsSharpEnough(i))
            {
                _ = LoadAsync(i);
            }
        }

        for (var i = Math.Max(0, frame.First - 10); i <= Math.Min(Count - 1, frame.Last + 10); i++)
        {
            if (!_sizes.ContainsKey(i) && !_infoInflight.Contains(i) && !_inflight.ContainsKey(i))
            {
                _ = LoadInfoAsync(i);
            }
        }

        if (moved && _sinceProgress.Elapsed >= ProgressInterval)
        {
            ReportProgress();
        }
    }

    /// <summary>Forward: towards higher page numbers. At the last page nothing happens (the page number says so).</summary>
    public void Next()
    {
        if (IsStrip || DeferStep(+1) || _frame.Last + 1 >= Count)
        {
            return;
        }

        Navigate(_frame.Last + 1, backward: false);
    }

    public void Previous()
    {
        if (IsStrip || DeferStep(-1) || _frame.First <= 0)
        {
            return;
        }

        Navigate(_frame.First - 1, backward: true);
    }

    public void First()
    {
        if (IsStrip)
        {
            NoteInput();
            StripGoToRequested?.Invoke(0);
        }
        else if (_frame.First > 0)
        {
            Navigate(0, backward: false);
        }
    }

    public void Last()
    {
        if (IsStrip)
        {
            NoteInput();
            StripGoToRequested?.Invoke(Count);
        }
        else if (_frame.Last < Count - 1)
        {
            Navigate(Count - 1, backward: true);
        }
    }

    /// <summary>Opens the frame that starts at <paramref name="page"/> (slider, Home/End).</summary>
    public void GoTo(int page)
    {
        if (Count > 0 && IsStrip)
        {
            NoteInput();
            StripGoToRequested?.Invoke(Math.Clamp(page, 0, Count - 1));
        }
        else if (Count > 0)
        {
            page = Math.Clamp(page, 0, Count - 1);
            if (page != _frame.First)
            {
                Navigate(page, backward: false);
            }
        }
    }

    /// <summary>Any user input: keeps reading time counting.</summary>
    public void NoteInput() => _sinceInput.Restart();

    /// <summary>
    /// D or the "pages shown" button. Books cycle one page → two pages → strip → one page;
    /// the gallery switches between one picture and strip.
    /// </summary>
    public void ToggleSpread()
    {
        if (!IsBook)
        {
            ToggleFlow();
        }
        else if (IsStrip)
        {
            ChangeBook(Book with { Flow = PageFlow.Paged, Spread = SpreadMode.Single });
        }
        else if (Book.Spread == SpreadMode.Single)
        {
            ChangeBook(Book with { Spread = SpreadMode.Double });
        }
        else
        {
            ChangeBook(Book with { Flow = PageFlow.Strip });
        }
    }

    public void ToggleDirection() => ChangeBook(Book with
    {
        Direction = Book.Direction == ReadingDirection.RightToLeft ? ReadingDirection.LeftToRight : ReadingDirection.RightToLeft,
    });

    public void ToggleCoverAlone()
    {
        if (!IsStrip)
        {
            ChangeBook(Book with { CoverAlone = !Book.CoverAlone });
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
        Second = null;
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
        RaiseFrameTexts();
        OnPropertyChanged(nameof(ZoomText));
        OnPropertyChanged(nameof(SpreadText));
        OnPropertyChanged(nameof(DirectionText));
        OnPropertyChanged(nameof(CoverText));
        OnPropertyChanged(nameof(LayoutTip));
    }

    private bool IsWide(int index) => _wide.TryGetValue(index, out var wide) && wide;

    private PageFrame Forward(int index) =>
        FrameLayout.Forward(index, Count, Spread, IsBook && Book.CoverAlone, IsWide);

    private PageFrame Backward(int index) =>
        FrameLayout.Backward(index, Count, Spread, IsBook && Book.CoverAlone, IsWide);

    /// <summary>
    /// In two-page mode the frame depends on page sizes that are known only after decoding. A step requested
    /// before then waits, so a wide page is never skipped.
    /// </summary>
    private bool DeferStep(int step)
    {
        NoteInput();
        if (_closed || Count == 0 || IsSettled())
        {
            return false;
        }

        _pendingStep = step;
        return true;
    }

    private bool IsSettled()
    {
        if (Spread == SpreadMode.Single)
        {
            return true;
        }

        // The pages FrameLayout looked at to build the current frame.
        var a = _anchorBack ? _frame.Last : _frame.First;
        var b = _anchorBack ? a - 1 : a + 1;
        return _wide.ContainsKey(a) && (b < 0 || b >= Count || _wide.ContainsKey(b));
    }

    private void ChangeBook(BookSettings next)
    {
        if (!IsBook || _closed || next == Book)
        {
            return;
        }

        NoteInput();
        var page = DisplayIndex;
        var flowChanged = next.Flow != Book.Flow;
        Book = next;
        try
        {
            _request.OnBookSettingsChanged?.Invoke(next);
        }
        catch (Exception ex)
        {
            _log.Error("OnBookSettingsChanged failed.", ex);
        }

        _pendingStep = 0;
        _shell.Notify(Loc.Format("Viewer.Book.Changed", SpreadText, DirectionText, CoverText));
        if (flowChanged)
        {
            SwitchFlow(page);
        }
        else if (IsStrip)
        {
            StripChanged?.Invoke(); // the direction flips the strip
        }
        else
        {
            Navigate(_frame.First, backward: false, initial: true); // keeps the current page
        }
    }

    /// <summary>Drops decoded pages (their resolution suits the other flow) and enters the new flow at <paramref name="page"/>.</summary>
    private void SwitchFlow(int page)
    {
        foreach (var cts in _inflight.Values)
        {
            cts.Cancel();
        }

        _inflight.Clear();
        _stripCts.Cancel();
        _stripCts.Dispose();
        _stripCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _infoInflight.Clear();
        _cache.Clear();
        _pendingStep = 0;
        page = Math.Clamp(page, 0, Math.Max(0, Count - 1));
        if (IsStrip)
        {
            EnterStrip(page);
            return;
        }

        Strip = null;
        OnFlowChanged();
        Navigate(page, backward: false, initial: true);
    }

    private void EnterStrip(int page)
    {
        _spinnerTimer.Stop();
        IsLoading = false;
        ShowSpinner = false;
        HasError = false;
        Current = null;
        Second = null;
        var strip = new StripLayout(Count, StripAxis.Horizontal);
        foreach (var (index, size) in _sizes)
        {
            strip.SetSize(index, size.Width, size.Height);
        }

        StripCurrent = page;
        _stripAtEnd = false;
        _frame = new PageFrame(page, page);
        Strip = strip;
        OnFlowChanged();
        RaiseFrameTexts();
    }

    private void OnFlowChanged()
    {
        OnPropertyChanged(nameof(Flow));
        OnPropertyChanged(nameof(IsStrip));
        OnPropertyChanged(nameof(IsPaged));
        OnPropertyChanged(nameof(SpreadText));
        OnPropertyChanged(nameof(Strip));
    }

    /// <summary>Strip pages are decoded at the displayed height, rounded up so small resizes do not re-decode.</summary>
    private int StripDecodeHeight => (_stripHeight + 255) / 256 * 256;

    /// <summary>The cached page is at least as sharp as the strip needs (or cannot get sharper).</summary>
    private bool IsSharpEnough(int index)
    {
        if (!_cache.Contains(index) || EntryAt(index) is not { } entry)
        {
            return false;
        }

        if (entry.Failed || entry.Bitmap is null)
        {
            return true;
        }

        var best = MaxDecodeHeight(entry.PixelWidth, entry.PixelHeight);
        return entry.Bitmap.PixelHeight >= Math.Min(StripDecodeHeight, best) - 1;
    }

    /// <summary>The tallest decode allowed for a page (pages over <see cref="MaxDecodePixels"/> are reduced).</summary>
    private static int MaxDecodeHeight(int width, int height)
    {
        var pixels = (long)width * height;
        return pixels > MaxDecodePixels ? Math.Max(1, (int)(height * Math.Sqrt(MaxDecodePixels / (double)pixels))) : height;
    }

    /// <summary>Reads one page's size (strip mode), so the strip is laid out before the page is decoded.</summary>
    private async Task LoadInfoAsync(int index)
    {
        var strip = Strip;
        var ct = _stripCts.Token;
        _infoInflight.Add(index); // stays on failure: an unreadable page is not retried in this strip
        ImageInfo? info = null;
        try
        {
            await _infoSlots.WaitAsync(ct);
            try
            {
                info = await Task.Run(
                    async () =>
                    {
                        await using var stream = await _request.Pages.OpenAsync(index, ct);
                        return await _imaging.ReadInfoAsync(stream, ct);
                    },
                    ct);
            }
            finally
            {
                _infoSlots.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Debug($"Reading the size of page {index} failed: {ex.Message}");
        }

        if (info is null || _closed || strip is null || !ReferenceEquals(strip, Strip))
        {
            return;
        }

        _infoInflight.Remove(index);
        _sizes[index] = (info.PixelWidth, info.PixelHeight);
        _wide[index] = info.PixelWidth > info.PixelHeight;
        strip.SetSize(index, info.PixelWidth, info.PixelHeight);
        StripChanged?.Invoke();
    }

    private void Navigate(int anchor, bool backward, bool initial = false)
    {
        if (_closed || Count == 0)
        {
            return;
        }

        if (!initial)
        {
            NoteInput();
        }

        _anchorBack = backward;
        _travelForward = !backward;
        _frame = backward ? Backward(anchor) : Forward(anchor);
        OnFrameChanged();
        if (!initial && _sinceProgress.Elapsed >= ProgressInterval)
        {
            ReportProgress();
        }
    }

    private void OnFrameChanged()
    {
        _cache.Pin(Pages(_frame));
        RaiseFrameTexts();
        ShowFrame(partial: false);
        Preload(_travelForward);
    }

    /// <summary>A page size became known: the current frame may pair differently now.</summary>
    private void Relayout()
    {
        if (Spread == SpreadMode.Single)
        {
            return;
        }

        var frame = _anchorBack ? Backward(_frame.Last) : Forward(_frame.First);
        if (frame != _frame)
        {
            _frame = frame;
            OnFrameChanged();
        }
    }

    private void ShowFrame(bool partial)
    {
        var first = _cache.TryGet(_frame.First, out var a) ? a : null;
        var second = IsSpread && _cache.TryGet(_frame.Last, out var b) ? b : null;
        var complete = first is not null && (!IsSpread || second is not null);
        if (!complete && !partial)
        {
            IsLoading = true;
            HasError = false;
            if (!_spinnerTimer.IsEnabled && !ShowSpinner)
            {
                _spinnerTimer.Start();
            }

            return;
        }

        if (complete)
        {
            _spinnerTimer.Stop();
            IsLoading = false;
            ShowSpinner = false;
        }

        Current = first;
        Second = second;
        HasError = complete && first!.Failed && (second is null || second.Failed);
        OnPropertyChanged(nameof(InfoText));
        OnPropertyChanged(nameof(ErrorText));
        PageChanged?.Invoke();

        if (complete && _pendingStep != 0 && IsSettled())
        {
            var step = _pendingStep;
            _pendingStep = 0;
            if (step > 0)
            {
                Next();
            }
            else
            {
                Previous();
            }
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
        var strip = Strip;
        PageEntry? entry = null;
        try
        {
            entry = await DecodeAsync(index, strip is null ? 0 : StripDecodeHeight, cts.Token);
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

        if (entry is null || _closed || !ReferenceEquals(strip, Strip))
        {
            return; // cancelled, closed, or decoded for the other flow
        }

        _wide[index] = entry.PixelWidth > entry.PixelHeight;
        if (!entry.Failed)
        {
            _sizes[index] = (entry.PixelWidth, entry.PixelHeight);
        }

        _cache.Add(index, entry, entry.Bytes);
        if (strip is not null)
        {
            if (!entry.Failed)
            {
                strip.SetSize(index, entry.PixelWidth, entry.PixelHeight);
            }

            StripChanged?.Invoke();
            return;
        }

        if (index >= _frame.First - 1 && index <= _frame.Last + 1)
        {
            var before = _frame;
            Relayout();
            if (_frame == before && IsLoading && index >= _frame.First && index <= _frame.Last)
            {
                ShowFrame(partial: ShowSpinner);
            }
            else if (_frame == before && !IsLoading && _pendingStep != 0)
            {
                ShowFrame(partial: false); // runs the waiting step now that the sizes are known
            }
        }
    }

    /// <param name="index">Page index.</param>
    /// <param name="maxHeight">Strip mode: the displayed height to decode at; 0 = full size (paged).</param>
    /// <param name="ct">Cancellation.</param>
    private async Task<PageEntry> DecodeAsync(int index, int maxHeight, CancellationToken ct)
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

                        if (maxHeight > 0)
                        {
                            // Only the height limits a strip page; keep any pixel-count limit on the width too.
                            var height = options.MaxPixelHeight > 0 ? Math.Min(options.MaxPixelHeight, maxHeight) : maxHeight;
                            options = new DecodeOptions(options.MaxPixelWidth, height);
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

    private void ReportProgress()
    {
        if (_request.OnProgress is null || Count == 0)
        {
            return;
        }

        // Active time only: stop counting once there was no input for IdleLimit.
        var sinceReport = _sinceProgress.Elapsed;
        var idleBeyondLimit = _sinceInput.Elapsed - IdleLimit;
        var active = idleBeyondLimit > TimeSpan.Zero ? sinceReport - idleBeyondLimit : sinceReport;
        if (active < TimeSpan.Zero)
        {
            active = TimeSpan.Zero;
        }

        _sinceProgress.Restart();
        try
        {
            var page = Strip is not null ? StripCurrent : _frame.First;
            var end = Strip is not null ? _stripAtEnd : _frame.Last >= Count - 1;
            _request.OnProgress(new ViewerProgress(page, Count, end, active));
        }
        catch (Exception ex)
        {
            _log.Error("OnProgress failed.", ex);
        }
    }

    private void RaiseFrameTexts()
    {
        OnPropertyChanged(nameof(Index));
        OnPropertyChanged(nameof(Frame));
        OnPropertyChanged(nameof(IsSpread));
        OnPropertyChanged(nameof(PageName));
        OnPropertyChanged(nameof(PageText));
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
