using System.Windows;

namespace Magpienest.Plugins.ImageWindow;

/// <summary>Window placement maths, in physical pixels. Pure, so it is unit-tested without a window.</summary>
internal static class WindowGeometry
{
    /// <summary>Smallest allowed short side of a window.</summary>
    public const double MinShortSide = 96;

    /// <summary>Largest allowed size relative to the image's pixel size.</summary>
    public const double MaxScale = 8;

    /// <summary>Share of the working area a new or "fit" window may use.</summary>
    public const double FitShare = 0.8;

    /// <summary>Offset between windows opened in one batch.</summary>
    public const double CascadeStep = 24;

    // WM_SIZING edges (WMSZ_*).
    public const int Left = 1, Right = 2, Top = 3, TopLeft = 4, TopRight = 5, Bottom = 6, BottomLeft = 7, BottomRight = 8;

    /// <summary>Size and place of a new window: fitted into the working area, centred on the cursor, kept on screen.</summary>
    public static Rect InitialBounds(Size image, Rect workArea, Point cursor, int cascade)
    {
        var size = FitSize(image, workArea);
        var offset = cascade * CascadeStep;
        return KeepInside(new Rect(cursor.X - size.Width / 2 + offset, cursor.Y - size.Height / 2 + offset,
                                   size.Width, size.Height), workArea);
    }

    /// <summary>The image scaled down (never up, except to the minimum size) to fit 80 % of the working area.</summary>
    public static Size FitSize(Size image, Rect workArea)
    {
        var scale = Math.Min(1, Math.Min(FitShare * workArea.Width / image.Width, FitShare * workArea.Height / image.Height));
        return Clamp(new Size(image.Width * scale, image.Height * scale), image);
    }

    /// <summary>A rectangle of <paramref name="size"/> centred on the centre of <paramref name="current"/>.</summary>
    public static Rect CentredOn(Rect current, Size size) =>
        new(current.X + (current.Width - size.Width) / 2, current.Y + (current.Height - size.Height) / 2, size.Width, size.Height);

    /// <summary>Moves <paramref name="bounds"/> fully into <paramref name="workArea"/> (top-left wins when it is larger).</summary>
    public static Rect KeepInside(Rect bounds, Rect workArea)
    {
        var x = Math.Max(workArea.Left, Math.Min(bounds.X, workArea.Right - bounds.Width));
        var y = Math.Max(workArea.Top, Math.Min(bounds.Y, workArea.Bottom - bounds.Height));
        return new Rect(x, y, bounds.Width, bounds.Height);
    }

    /// <summary>Scales the window by <paramref name="factor"/> keeping the point under the cursor still.</summary>
    public static Rect ScaleAround(Rect bounds, Point cursor, double factor, Size image)
    {
        var size = Clamp(new Size(bounds.Width * factor, bounds.Height * factor), image);
        var f = size.Width / bounds.Width;
        return new Rect(cursor.X - (cursor.X - bounds.X) * f, cursor.Y - (cursor.Y - bounds.Y) * f, size.Width, size.Height);
    }

    /// <summary>
    /// Keeps the image's aspect ratio while the user drags an edge (WM_SIZING). The opposite edge stays put;
    /// side edges drive by width, top and bottom by height.
    /// </summary>
    public static Rect ConstrainSizing(Rect proposed, int edge, Size image)
    {
        var aspect = image.Width / image.Height;
        var byHeight = edge is Top or Bottom;
        var size = byHeight
            ? new Size(proposed.Height * aspect, proposed.Height)
            : new Size(proposed.Width, proposed.Width / aspect);
        size = Clamp(size, image);
        var left = edge is Left or TopLeft or BottomLeft ? proposed.Right - size.Width : proposed.Left;
        var top = edge is Top or TopLeft or TopRight ? proposed.Bottom - size.Height : proposed.Top;
        return new Rect(left, top, size.Width, size.Height);
    }

    /// <summary>Keeps the aspect ratio of <paramref name="image"/> and the size between the minimum and the maximum.</summary>
    public static Size Clamp(Size size, Size image)
    {
        var aspect = image.Width / image.Height;
        var width = size.Width;
        var minWidth = aspect >= 1 ? MinShortSide * aspect : MinShortSide;
        var maxWidth = Math.Max(minWidth, image.Width * MaxScale);
        width = Math.Max(minWidth, Math.Min(maxWidth, width));
        return new Size(width, width / aspect);
    }
}
