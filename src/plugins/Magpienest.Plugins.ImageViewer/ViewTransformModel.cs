using System.Windows;

namespace Magpienest.Plugins.ImageViewer;

/// <summary>How the scale is chosen.</summary>
internal enum ZoomMode
{
    /// <summary>Scale down to fit the viewport (never above 100 % unless stretching is on).</summary>
    Fit,

    /// <summary>One image pixel per device pixel.</summary>
    Actual,

    /// <summary>Whatever the user zoomed to.</summary>
    Free,
}

/// <summary>
/// Pure zoom/pan math of the viewer. Content size is the (rotated) image size in DIPs at 100 %;
/// <see cref="Offset"/> is the viewport position of the content's top-left corner.
/// </summary>
internal sealed class ViewTransformModel
{
    public const double MinScale = 0.1;
    public const double MaxScale = 16;
    public const double Step = 1.2;

    private Size _unrotated = Size.Empty;

    /// <summary>Content size after rotation, in DIPs at scale 1.</summary>
    public Size Content { get; private set; } = Size.Empty;

    public Size Viewport { get; private set; } = Size.Empty;

    public double Scale { get; private set; } = 1;

    public Vector Offset { get; private set; }

    public ZoomMode Mode { get; private set; } = ZoomMode.Fit;

    /// <summary>0, 90, 180 or 270 degrees clockwise.</summary>
    public int Rotation { get; private set; }

    /// <summary>Fit may scale small images up.</summary>
    public bool StretchSmall { get; set; }

    public bool HasContent => !_unrotated.IsEmpty && _unrotated.Width > 0 && _unrotated.Height > 0;

    /// <summary>True when the scaled content is larger than the viewport on at least one axis (panning is possible).</summary>
    public bool CanPan => HasContent && (Content.Width * Scale > Viewport.Width + 0.5 || Content.Height * Scale > Viewport.Height + 0.5);

    /// <summary>The scale <see cref="ZoomMode.Fit"/> uses for the current content and viewport.</summary>
    public double FitScale
    {
        get
        {
            if (!HasContent || Viewport.IsEmpty || Viewport.Width <= 0 || Viewport.Height <= 0)
            {
                return 1;
            }

            var fit = Math.Min(Viewport.Width / Content.Width, Viewport.Height / Content.Height);
            return StretchSmall ? fit : Math.Min(1, fit);
        }
    }

    /// <summary>New image: resets rotation and returns to Fit.</summary>
    public void SetContent(Size content)
    {
        _unrotated = content;
        Rotation = 0;
        Content = content;
        Fit();
    }

    /// <summary>Viewport resized: Fit stays fitted; other modes keep the viewport centre on the same content point.</summary>
    public void SetViewport(Size viewport)
    {
        var oldCentre = Viewport.IsEmpty ? (Point?)null : new Point(Viewport.Width / 2, Viewport.Height / 2);
        var contentCentre = oldCentre is { } c && HasContent ? ToContent(c) : (Point?)null;
        Viewport = viewport;
        if (Mode == ZoomMode.Fit)
        {
            Fit();
            return;
        }

        if (contentCentre is { } point)
        {
            Offset = new Vector((viewport.Width / 2) - (point.X * Scale), (viewport.Height / 2) - (point.Y * Scale));
        }

        Clamp();
    }

    public void Fit()
    {
        Mode = ZoomMode.Fit;
        Scale = FitScale;
        Offset = default;
        Clamp();
    }

    /// <summary>100 %, anchored at <paramref name="anchor"/> (viewport coordinates; null = centre).</summary>
    public void ActualSize(Point? anchor = null)
    {
        ZoomTo(1, anchor);
        Mode = ZoomMode.Actual;
    }

    /// <summary>Double-click: Fit ↔ 100 %.</summary>
    public void ToggleFitActual(Point? anchor = null)
    {
        if (Mode != ZoomMode.Fit)
        {
            Fit();
        }
        else if (Math.Abs(Scale - 1) > 1e-9)
        {
            ActualSize(anchor);
        }

        // Fit that already equals 100 % (a small image) has nothing to toggle to.
    }

    /// <summary>Multiplies the scale by <paramref name="factor"/>, keeping the content point under the anchor fixed.</summary>
    public void ZoomBy(double factor, Point? anchor = null) => ZoomTo(Scale * factor, anchor);

    /// <summary>Sets the scale (clamped to 10 %–1600 %), keeping the content point under the anchor fixed.</summary>
    public void ZoomTo(double scale, Point? anchor = null)
    {
        if (!HasContent)
        {
            return;
        }

        var at = anchor ?? new Point(Viewport.Width / 2, Viewport.Height / 2);
        var contentPoint = ToContent(at);
        Scale = Math.Clamp(scale, MinScale, MaxScale);
        Mode = ZoomMode.Free;
        Offset = new Vector(at.X - (contentPoint.X * Scale), at.Y - (contentPoint.Y * Scale));
        Clamp();
    }

    /// <summary>Moves the content by a viewport delta (drag).</summary>
    public void Pan(Vector delta)
    {
        Offset += delta;
        Clamp();
    }

    /// <summary>Rotates the view by 90° clockwise; Fit stays fitted, otherwise the centre is kept.</summary>
    public void Rotate()
    {
        Rotation = (Rotation + 90) % 360;
        Content = Rotation % 180 == 0 ? _unrotated : new Size(_unrotated.Height, _unrotated.Width);
        if (Mode == ZoomMode.Fit)
        {
            Fit();
        }
        else
        {
            Offset = new Vector((Viewport.Width - (Content.Width * Scale)) / 2, (Viewport.Height - (Content.Height * Scale)) / 2);
            Clamp();
        }
    }

    /// <summary>Viewport point → content point (DIPs at scale 1, rotated space).</summary>
    public Point ToContent(Point viewportPoint) =>
        new((viewportPoint.X - Offset.X) / Scale, (viewportPoint.Y - Offset.Y) / Scale);

    /// <summary>Content point → viewport point.</summary>
    public Point ToViewport(Point contentPoint) =>
        new((contentPoint.X * Scale) + Offset.X, (contentPoint.Y * Scale) + Offset.Y);

    private void Clamp()
    {
        if (!HasContent || Viewport.IsEmpty)
        {
            return;
        }

        Offset = new Vector(ClampAxis(Offset.X, Content.Width * Scale, Viewport.Width),
                            ClampAxis(Offset.Y, Content.Height * Scale, Viewport.Height));
    }

    /// <summary>Smaller than the viewport → centred; larger → no gap at either edge.</summary>
    private static double ClampAxis(double offset, double size, double viewport) =>
        size <= viewport ? (viewport - size) / 2 : Math.Clamp(offset, viewport - size, 0);
}
