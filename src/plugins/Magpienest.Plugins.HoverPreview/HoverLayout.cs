using System.Windows;

namespace Magpienest.Plugins.HoverPreview;

/// <summary>Where the preview goes: beside the cursor, never under it, as large as fits without enlarging the picture.</summary>
internal static class HoverLayout
{
    /// <summary>Distance from the area's edges (DIP).</summary>
    public const double Margin = 8;

    /// <summary>Distance between the cursor and the preview (DIP).</summary>
    public const double Gap = 16;

    /// <summary>A picture that would show smaller than this on every side is not shown (DIP).</summary>
    public const double MinSide = 48;

    private enum Side
    {
        Right,
        Left,
        Below,
        Above,
    }

    /// <summary>
    /// Picks the side of the cursor (right, left, below, above; the first wins a tie) where the picture can be
    /// largest, and returns the preview's bounds, or null when it would be too small everywhere.
    /// </summary>
    /// <param name="cursor">Cursor position in the area.</param>
    /// <param name="area">Size of the area the preview stays inside.</param>
    /// <param name="picture">The picture's size at 100 %.</param>
    /// <param name="chrome">Space the frame adds around the picture on both axes.</param>
    public static Rect? Fit(Point cursor, Size area, Size picture, double chrome = 0)
    {
        if (picture.Width <= 0 || picture.Height <= 0 || area.Width <= 0 || area.Height <= 0)
        {
            return null;
        }

        var fullWidth = area.Width - 2 * Margin - chrome;
        var fullHeight = area.Height - 2 * Margin - chrome;
        (Side Side, double Width, double Height)[] boxes =
        [
            (Side.Right, area.Width - Margin - (cursor.X + Gap) - chrome, fullHeight),
            (Side.Left, cursor.X - Gap - Margin - chrome, fullHeight),
            (Side.Below, fullWidth, area.Height - Margin - (cursor.Y + Gap) - chrome),
            (Side.Above, fullWidth, cursor.Y - Gap - Margin - chrome),
        ];

        Side? best = null;
        var bestScale = 0.0;
        foreach (var (side, width, height) in boxes)
        {
            if (width <= 0 || height <= 0)
            {
                continue;
            }

            var scale = Math.Min(1, Math.Min(width / picture.Width, height / picture.Height));
            if (scale > bestScale)
            {
                bestScale = scale;
                best = side;
            }
        }

        var shownWidth = Math.Floor(picture.Width * bestScale);
        var shownHeight = Math.Floor(picture.Height * bestScale);
        if (best is null || Math.Max(shownWidth, shownHeight) < MinSide)
        {
            return null;
        }

        var totalWidth = shownWidth + chrome;
        var totalHeight = shownHeight + chrome;
        var centredX = Clamp(cursor.X - totalWidth / 2, Margin, area.Width - Margin - totalWidth);
        var centredY = Clamp(cursor.Y - totalHeight / 2, Margin, area.Height - Margin - totalHeight);
        var origin = best switch
        {
            Side.Right => new Point(cursor.X + Gap, centredY),
            Side.Left => new Point(cursor.X - Gap - totalWidth, centredY),
            Side.Below => new Point(centredX, cursor.Y + Gap),
            _ => new Point(centredX, cursor.Y - Gap - totalHeight),
        };
        return new Rect(origin, new Size(totalWidth, totalHeight));
    }

    private static double Clamp(double value, double min, double max) => max < min ? min : Math.Clamp(value, min, max);
}
