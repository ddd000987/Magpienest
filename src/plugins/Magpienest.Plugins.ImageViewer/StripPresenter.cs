using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Magpienest.Sdk.Viewing;

namespace Magpienest.Plugins.ImageViewer;

/// <summary>
/// Strip mode: every page edge to edge at one height, scrolled continuously.
/// Positions come from the view model's <see cref="StripLayout"/>; the scroll position is kept as a
/// <see cref="StripAnchor"/> so pages whose size arrives later never move the page being read.
/// Only the pages near the viewport have elements, each placed relative to the current scroll.
/// </summary>
internal sealed class StripPresenter
{
    public const double MinZoom = 1;
    public const double MaxZoom = 4;
    private static readonly TimeSpan AnimationTime = TimeSpan.FromMilliseconds(120);

    private readonly FrameworkElement _stage;
    private readonly Canvas _canvas;
    private readonly ViewerViewModel _vm;
    private readonly Dictionary<int, StripSlot> _slots = [];
    private readonly Stack<StripSlot> _pool = new();
    private readonly Stopwatch _clock = new();
    private StripAnchor _anchor;
    private double _cross;          // pan across the strip when zoomed (DIP)
    private double _animFrom;       // layout units
    private double _animTo;
    private bool _animating;
    private double _lastScroll;     // layout units, as last rendered

    public StripPresenter(FrameworkElement stage, Canvas canvas, ViewerViewModel vm)
    {
        _stage = stage;
        _canvas = canvas;
        _vm = vm;
        vm.StripChanged += OnStripChanged;
        vm.StripGoToRequested += page =>
        {
            if (page >= _vm.Count)
                End();
            else
                GoTo(page);
        };
    }

    public bool IsActive { get; private set; }

    /// <summary>1 = pages as tall as the view.</summary>
    public double Zoom { get; private set; } = MinZoom;

    /// <summary>The slots on screen, by page index (for tests).</summary>
    internal IReadOnlyDictionary<int, StripSlot> Slots => _slots;

    private StripLayout? Layout => _vm.Strip;

    private bool RightToLeft => _vm.RightToLeft;

    /// <summary>Displayed page height (DIP).</summary>
    private double Cross => _stage.ActualHeight * Zoom;

    /// <summary>Viewport length in layout units.</summary>
    private double ViewLength => Cross > 0 ? _stage.ActualWidth / Cross : 0;

    private double MaxScroll => Layout is { } layout ? Math.Max(0, layout.Extent - ViewLength) : 0;

    private double Scroll => Layout is { } layout ? Math.Clamp(layout.OffsetOf(_anchor), 0, MaxScroll) : 0;

    public void Enter(int page)
    {
        IsActive = true;
        _canvas.Visibility = Visibility.Visible;
        _anchor = new StripAnchor(page, 0);
        Zoom = MinZoom;
        _cross = 0;
        StopAnimation();
        Render();
    }

    public void Exit()
    {
        IsActive = false;
        StopAnimation();
        foreach (var slot in _slots.Values)
        {
            Release(slot);
        }

        _slots.Clear();
        _canvas.Visibility = Visibility.Collapsed;
    }

    /// <summary>Scrolls forward (positive) or backward by a share of the viewport.</summary>
    public void ScrollBy(double viewports, bool animate = true)
    {
        if (!IsActive || Layout is null || ViewLength <= 0)
        {
            return;
        }

        var from = _animating ? _animTo : Scroll;
        var target = Math.Clamp(from + viewports * ViewLength, 0, MaxScroll);
        if (target == from)
        {
            return; // at an end: nothing to do (no notification, the page number says so)
        }

        if (!animate)
        {
            StopAnimation();
            SetScroll(target);
            return;
        }

        _animFrom = Scroll;
        _animTo = target;
        _clock.Restart();
        if (!_animating)
        {
            _animating = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    /// <summary>Aligns the page's reading-start edge with the viewport's.</summary>
    public void GoTo(int page)
    {
        if (!IsActive || Layout is not { } layout || layout.Count == 0)
        {
            return;
        }

        StopAnimation();
        SetScroll(layout.OffsetOf(Math.Clamp(page, 0, layout.Count - 1)));
    }

    public void Home()
    {
        StopAnimation();
        SetScroll(0);
    }

    public void End()
    {
        StopAnimation();
        SetScroll(MaxScroll);
    }

    /// <summary>Grab-scroll: the content follows the mouse.</summary>
    public void DragBy(Vector delta)
    {
        if (!IsActive || Cross <= 0)
        {
            return;
        }

        StopAnimation();
        _cross -= delta.Y;
        SetScroll(Scroll + (RightToLeft ? delta.X : -delta.X) / Cross);
    }

    /// <summary>Ctrl+wheel: zoom between 1 and 4 keeping the point under the cursor still.</summary>
    public void ZoomAt(double factor, Point point)
    {
        if (!IsActive || Cross <= 0)
        {
            return;
        }

        StopAnimation();
        var oldCross = Cross;
        var reading = ReadingDistance(point.X);
        var at = Scroll + reading / oldCross;
        Zoom = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);
        var newCross = Cross;
        _cross = (_cross + point.Y) * (newCross / oldCross) - point.Y;
        SetScroll(at - reading / newCross);
    }

    public void ResetZoom()
    {
        var page = Layout is { } layout ? layout.AnchorAt(Scroll) : _anchor;
        Zoom = MinZoom;
        _cross = 0;
        _anchor = page;
        Render();
    }

    /// <summary>The page under a point of the stage (right-click).</summary>
    public int IndexAt(Point point) =>
        Layout is { } layout && Cross > 0 ? layout.IndexAt(Scroll + ReadingDistance(point.X) / Cross) : _vm.StripCurrent;

    /// <summary>Lays out the pages near the viewport and tells the view model what is on screen.</summary>
    public void Render()
    {
        if (!IsActive || Layout is not { } layout || layout.Count == 0)
        {
            return;
        }

        var width = _stage.ActualWidth;
        var cross = Cross;
        if (width <= 0 || cross <= 0)
        {
            return;
        }

        _canvas.Width = width;
        _canvas.Height = _stage.ActualHeight;
        var scroll = Scroll;
        _anchor = layout.AnchorAt(scroll);
        _lastScroll = scroll;
        _cross = Math.Clamp(_cross, 0, Math.Max(0, cross - _stage.ActualHeight));

        var view = ViewLength;
        var range = layout.Visible(scroll, view, view);
        foreach (var index in _slots.Keys.Where(i => i < range.First || i > range.Last).ToList())
        {
            Release(_slots[index]);
            _slots.Remove(index);
        }

        // Edges are snapped to device pixels from the page offsets, so neighbours always meet exactly.
        var dpi = VisualTreeHelper.GetDpi(_stage).DpiScaleX;
        double Snap(double v) => Math.Round(v * dpi) / dpi;
        for (var i = range.First; i <= range.Last; i++)
        {
            if (!_slots.TryGetValue(i, out var slot))
            {
                slot = _pool.Count > 0 ? _pool.Pop() : new StripSlot();
                if (slot.Parent is null)
                {
                    _canvas.Children.Add(slot);
                }

                slot.Visibility = Visibility.Visible;
                _slots[i] = slot;
            }

            var start = Snap((layout.OffsetOf(i) - scroll) * cross);
            var end = Snap((layout.OffsetOf(i + 1) - scroll) * cross);
            slot.Width = Math.Max(0, end - start);
            slot.Height = cross;
            Canvas.SetLeft(slot, RightToLeft ? width - end : start);
            Canvas.SetTop(slot, -_cross);
            slot.Show(_vm.EntryAt(i));
        }

        var core = layout.Visible(scroll, view, 0);
        var current = layout.IndexAt(scroll + 0.1 * view);
        var atEnd = scroll + view >= layout.Extent - 1e-6;
        _vm.ZoomPercent = Zoom * 100;
        _vm.UpdateStrip(core, current, atEnd, (int)Math.Ceiling(cross * VisualTreeHelper.GetDpi(_stage).DpiScaleY));
    }

    private double ReadingDistance(double x) => RightToLeft ? _stage.ActualWidth - x : x;

    private void SetScroll(double scroll)
    {
        if (Layout is not { } layout)
        {
            return;
        }

        _anchor = layout.AnchorAt(Math.Clamp(scroll, 0, MaxScroll));
        Render();
    }

    /// <summary>Sizes arrived: the anchor keeps the reading position; a running animation shifts with it.</summary>
    private void OnStripChanged()
    {
        if (!IsActive || Layout is not { } layout)
        {
            return;
        }

        var shift = Math.Clamp(layout.OffsetOf(_anchor), 0, MaxScroll) - _lastScroll;
        _animFrom += shift;
        _animTo += shift;
        Render();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var t = Math.Min(1, _clock.Elapsed / AnimationTime);
        var eased = 1 - Math.Pow(1 - t, 3);
        SetScroll(_animFrom + (_animTo - _animFrom) * eased);
        if (t >= 1)
        {
            StopAnimation();
        }
    }

    private void StopAnimation()
    {
        if (_animating)
        {
            _animating = false;
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private void Release(StripSlot slot)
    {
        slot.Show(null);
        slot.Visibility = Visibility.Collapsed;
        _pool.Push(slot);
    }
}

/// <summary>One page of the strip: a placeholder fill until the page is decoded, a glyph when it failed.</summary>
internal sealed class StripSlot : Border
{
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly TextBlock _broken = new()
    {
        Text = "",
        FontSize = 36,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed,
    };

    public StripSlot()
    {
        IsHitTestVisible = false;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _broken.SetResourceReference(TextBlock.FontFamilyProperty, "SymbolThemeFontFamily");
        _broken.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        SetResourceReference(BackgroundProperty, "ControlFillColorDefaultBrush");
        Child = new Grid { Children = { _image, _broken } };
    }

    /// <summary>The page shown (null while loading).</summary>
    public ImageSource? Source => _image.Source;

    public void Show(PageEntry? entry)
    {
        _image.Source = entry?.Bitmap;
        _broken.Visibility = entry is { Failed: true } ? Visibility.Visible : Visibility.Collapsed;
    }
}
