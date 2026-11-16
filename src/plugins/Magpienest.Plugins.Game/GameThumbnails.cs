using System.Windows.Media;
using System.Windows.Media.Imaging;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Thumbnails;

namespace Magpienest.Plugins.Game;

/// <summary>A custom cover when there is one, else a card with the program's own icon.</summary>
internal sealed class GameThumbnails(IThumbnailService thumbnails) : IThumbnailProvider
{
    public const int IconSize = 256;

    public string GetStamp(LibraryItem item) => ThumbnailDefaults.DefaultStamp(item);

    public async Task<BitmapSource?> CreateAsync(LibraryItem item, int maxPixelSize, CancellationToken ct)
    {
        if (item.CoverPath is not null &&
            await thumbnails.GetShellImageAsync(item.CoverPath, maxPixelSize, iconOnly: false, ct) is { } cover)
        {
            return Fit(cover, maxPixelSize);
        }

        var icon = await thumbnails.GetShellImageAsync(item.Path, IconSize, iconOnly: true, ct);
        return icon is null ? null : GameCard.Compose(icon, maxPixelSize);
    }

    /// <summary>The shell may return a larger image than asked for; scale it down.</summary>
    internal static BitmapSource Fit(BitmapSource source, int maxPixelSize)
    {
        var longSide = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longSide <= maxPixelSize)
        {
            return source;
        }

        var scale = maxPixelSize / (double)longSide;
        var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }
}

/// <summary>Draws the portrait card of a game without a cover: a plain background with the icon in the middle.</summary>
internal static class GameCard
{
    /// <summary>Card size at the standard thumbnail size (aspect ratio 0.7 like the tile).</summary>
    public const int Width = 268;

    /// <summary>Card height.</summary>
    public const int Height = 384;

    // Fixed neutral grey: the card is drawn into a bitmap, outside any theme (accepted deviation from "theme brushes only").
    private const byte Grey = 0x3A;

    /// <summary>Puts the icon, at its natural size, in the middle of the card. Pure pixel work, safe on any thread.</summary>
    /// <param name="icon">The program's icon.</param>
    /// <param name="maxPixelSize">Longest side of the result.</param>
    public static BitmapSource Compose(BitmapSource icon, int maxPixelSize)
    {
        var converted = new FormatConvertedBitmap(icon, PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);

        var card = new byte[Width * Height * 4];
        for (var i = 0; i < card.Length; i += 4)
        {
            card[i] = Grey;
            card[i + 1] = Grey;
            card[i + 2] = Grey;
            card[i + 3] = 0xFF;
        }

        // An icon larger than the card is cropped by the loop bounds below; the shell icon is at most 256 pixels.
        var left = (Width - width) / 2;
        var top = (Height - height) / 2;
        for (var y = 0; y < height; y++)
        {
            var cardY = top + y;
            if (cardY < 0 || cardY >= Height)
            {
                continue;
            }

            for (var x = 0; x < width; x++)
            {
                var cardX = left + x;
                if (cardX < 0 || cardX >= Width)
                {
                    continue;
                }

                var source = (y * width + x) * 4;
                var target = (cardY * Width + cardX) * 4;
                var alpha = pixels[source + 3];
                if (alpha == 0)
                {
                    continue;
                }

                // Bgra32 from the converter is not premultiplied.
                for (var channel = 0; channel < 3; channel++)
                {
                    card[target + channel] = (byte)((pixels[source + channel] * alpha + card[target + channel] * (255 - alpha)) / 255);
                }
            }
        }

        var result = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, card, Width * 4);
        result.Freeze();
        return maxPixelSize >= Height ? result : GameThumbnails.Fit(result, maxPixelSize);
    }
}
