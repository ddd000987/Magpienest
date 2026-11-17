using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Magpienest.Sdk;
using Magpienest.Sdk.Imaging;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Viewing;

namespace Magpienest.Plugins.HoverPreview;

/// <summary>Follows one hovered element at a time; see <see cref="IHoverPreviewService"/>.</summary>
internal sealed class HoverPreviewService(IImagingService imaging, IPluginConfig config, ILog log) : IHoverPreviewService, IDisposable
{
    /// <summary>Config key: milliseconds the mouse rests before the preview shows.</summary>
    public const string DelayKey = "delayMs";

    public const int DefaultDelay = 400;

    private HoverSession? _session;

    internal IImagingService Imaging => imaging;

    internal ILog Log => log;

    /// <summary>The configured delay, kept within 100 ms and 3 s.</summary>
    internal TimeSpan Delay => TimeSpan.FromMilliseconds(Math.Clamp(config.Get(DelayKey, DefaultDelay), 100, 3000));

    /// <summary>The element being followed (for tests).</summary>
    internal FrameworkElement? Target => _session?.Target;

    public void Hover(FrameworkElement target, HoverPreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);
        _session?.Close();
        _session = new HoverSession(this, target, request);
        _session.Start();
    }

    internal void Ended(HoverSession session)
    {
        if (_session == session)
        {
            _session = null;
        }
    }

    public void Dispose()
    {
        _session?.Close();
        _session = null;
    }
}

/// <summary>
/// One hover: waits for the delay, opens the pages, shows the current page in a popup beside the cursor and
/// turns pages on the wheel. Ends when the mouse leaves the element, it is clicked, recycled for another item
/// or unloaded, or the window loses focus.
/// </summary>
internal sealed class HoverSession
{
    /// <summary>The frame's border on both sides (DIP).</summary>
    private const double Chrome = 2;

    private readonly HoverPreviewService _owner;
    private readonly HoverPreviewRequest _request;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _timer;
    private Window? _window;
    private FrameworkElement? _root;
    private Point _anchor;
    private IPageSource? _pages;
    private int _index;
    private CancellationTokenSource? _load;
    private Task _loading = Task.CompletedTask;
    private bool _opening;
    private bool _closed;
    private Popup? _popup;
    private Image? _image;
    private Border? _badge;
    private TextBlock? _badgeText;

    public HoverSession(HoverPreviewService owner, FrameworkElement target, HoverPreviewRequest request)
    {
        _owner = owner;
        Target = target;
        _request = request;
        _timer = new DispatcherTimer(DispatcherPriority.Input, target.Dispatcher) { Interval = owner.Delay };
        _timer.Tick += OnDelayElapsed;
    }

    public FrameworkElement Target { get; }

    /// <summary>Whether the popup is on screen (for tests).</summary>
    internal bool IsShown => _popup?.IsOpen == true;

    public void Start()
    {
        _window = Window.GetWindow(Target);
        Target.MouseLeave += OnEnded;
        Target.PreviewMouseDown += OnEnded;
        Target.Unloaded += OnEnded;
        Target.DataContextChanged += OnContextChanged;
        Target.PreviewMouseWheel += OnWheel;
        if (_window is not null)
        {
            _window.Deactivated += OnEnded;
        }

        _timer.Start();
    }

    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _timer.Stop();
        _cts.Cancel();
        _load?.Cancel();
        Target.MouseLeave -= OnEnded;
        Target.PreviewMouseDown -= OnEnded;
        Target.Unloaded -= OnEnded;
        Target.DataContextChanged -= OnContextChanged;
        Target.PreviewMouseWheel -= OnWheel;
        if (_window is not null)
        {
            _window.Deactivated -= OnEnded;
        }

        if (_popup is not null)
        {
            _popup.IsOpen = false;
        }

        if (_pages is { } pages)
        {
            _pages = null;
            _ = DisposeAfterAsync(_loading, pages);
        }

        _owner.Ended(this);
    }

    private void OnEnded(object? sender, EventArgs e) => Close();

    private void OnContextChanged(object sender, DependencyPropertyChangedEventArgs e) => Close();

    private async void OnDelayElapsed(object? sender, EventArgs e)
    {
        _timer.Stop();
        _root = _window?.Content as FrameworkElement;
        if (_closed || _root is null || !Target.IsMouseOver)
        {
            Close();
            return;
        }

        _anchor = Mouse.GetPosition(_root);
        _opening = true;
        try
        {
            var ct = _cts.Token;
            var pages = await Task.Run(() => _request.OpenPages(ct), ct);
            if (_closed)
            {
                await DisposeAfterAsync(Task.CompletedTask, pages);
                return;
            }

            _pages = pages;
            _opening = false;
            if (pages.Count == 0)
            {
                Close();
                return;
            }

            _index = Math.Clamp(_request.StartIndex, 0, pages.Count - 1);
            Load(_index);
        }
        catch (OperationCanceledException)
        {
            // Closed while opening.
        }
        catch (Exception ex)
        {
            _owner.Log.Warn($"Hover preview of '{_request.Title}' could not open its pages.", ex);
            Close();
        }
    }

    /// <summary>The wheel turns pages while a book is shown; the list underneath does not scroll meanwhile.</summary>
    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        if (_pages is null)
        {
            e.Handled = _opening;
            return;
        }

        if (_pages.Count < 2)
        {
            return; // a single picture: the wheel scrolls the list as usual (and the preview closes)
        }

        e.Handled = true;
        var next = Math.Clamp(_index + (e.Delta < 0 ? 1 : -1), 0, _pages.Count - 1);
        if (next != _index)
        {
            _index = next;
            Load(next);
        }
    }

    private void Load(int index)
    {
        _load?.Cancel();
        _load = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _loading = LoadAsync(index, _load.Token);
    }

    private async Task LoadAsync(int index, CancellationToken ct)
    {
        if (_pages is not { } pages || _root is not { } root)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(root);
        var maxWidth = (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX);
        var maxHeight = (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY);
        try
        {
            var imaging = _owner.Imaging;
            var bitmap = await Task.Run(async () =>
            {
                await using var stream = await pages.OpenAsync(index, ct).ConfigureAwait(false);
                return await imaging.DecodeAsync(stream, new DecodeOptions(maxWidth, maxHeight), ct).ConfigureAwait(false);
            }, ct);
            if (!ct.IsCancellationRequested && !_closed)
            {
                Show(bitmap, index, pages.Count, dpi);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer page or the end of the hover.
        }
        catch (Exception ex) when (!_closed)
        {
            _owner.Log.Warn($"Hover preview of '{_request.Title}' could not show page {index}.", ex);
        }
    }

    private void Show(BitmapSource bitmap, int index, int count, DpiScale dpi)
    {
        if (_root is not { } root)
        {
            return;
        }

        var picture = new Size(bitmap.PixelWidth / dpi.DpiScaleX, bitmap.PixelHeight / dpi.DpiScaleY);
        var bounds = HoverLayout.Fit(_anchor, new Size(root.ActualWidth, root.ActualHeight), picture, Chrome);
        if (bounds is not { } rect)
        {
            if (_popup is not null)
            {
                _popup.IsOpen = false;
            }

            return;
        }

        EnsurePopup(root);
        _image!.Source = bitmap;
        _image.Width = rect.Width - Chrome;
        _image.Height = rect.Height - Chrome;
        _badge!.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _badgeText!.Text = Loc.Format("HoverPreview.PageOf", index + 1, count);
        _popup!.HorizontalOffset = rect.X;
        _popup.VerticalOffset = rect.Y;
        _popup.IsOpen = true;
    }

    private void EnsurePopup(FrameworkElement root)
    {
        if (_popup is not null)
        {
            return;
        }

        _image = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _badgeText = new TextBlock { Margin = new Thickness(8, 3, 8, 3), FontSize = 12 };
        _badgeText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        _badge = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(6),
            CornerRadius = new CornerRadius(4),
            Opacity = 0.9,
            Child = _badgeText,
        };
        _badge.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        var content = new Grid { IsHitTestVisible = false };
        content.Children.Add(_image);
        content.Children.Add(_badge);
        var frame = new Border { BorderThickness = new Thickness(Chrome / 2), Child = content, IsHitTestVisible = false };
        frame.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        frame.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        _popup = new Popup
        {
            Child = frame,
            PlacementTarget = root,
            Placement = PlacementMode.Relative,
            AllowsTransparency = false,
            StaysOpen = true,
            Focusable = false,
            IsHitTestVisible = false,
        };
    }

    /// <summary>Lets a page still being read finish before its source closes.</summary>
    private async Task DisposeAfterAsync(Task loading, IPageSource pages)
    {
        try
        {
            await loading;
        }
        catch
        {
            // Its own failure was handled where it happened.
        }

        try
        {
            await pages.DisposeAsync();
        }
        catch (Exception ex)
        {
            _owner.Log.Warn($"Closing the pages of '{_request.Title}' failed.", ex);
        }
    }
}
