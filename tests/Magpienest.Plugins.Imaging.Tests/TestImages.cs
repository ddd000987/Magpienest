using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Magpienest.Plugins.Imaging.Tests;

/// <summary>Generates the sample images in memory with the WPF encoders.</summary>
internal static class TestImages
{
    public const char Red = 'R';
    public const char Green = 'G';
    public const char Blue = 'B';
    public const char Yellow = 'Y';

    private static readonly Lazy<byte[]> LargeJpegData = new(() => Encode(new JpegBitmapEncoder { QualityLevel = 60 }, Gradient(4000, 3000)));

    /// <summary>Top-left red, top-right green, bottom-left blue, bottom-right yellow.</summary>
    public static BitmapSource Quadrants(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var top = y < height / 2;
                var left = x < width / 2;
                var (b, g, r) = (top, left) switch
                {
                    (true, true) => (0, 0, 255),
                    (true, false) => (0, 200, 0),
                    (false, true) => (255, 0, 0),
                    _ => (0, 255, 255),
                };
                var i = (y * stride) + (x * 4);
                pixels[i] = (byte)b;
                pixels[i + 1] = (byte)g;
                pixels[i + 2] = (byte)r;
                pixels[i + 3] = 255;
            }
        }

        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
    }

    /// <summary>Opaque smooth gradient (compresses well, so big sizes stay cheap).</summary>
    public static BitmapSource Gradient(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * stride) + (x * 4);
                pixels[i] = (byte)(x * 255 / width);
                pixels[i + 1] = (byte)(y * 255 / height);
                pixels[i + 2] = 128;
                pixels[i + 3] = 255;
            }
        }

        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
    }

    /// <summary>Left half fully transparent (with a red colour hidden in it), right half opaque blue.</summary>
    public static BitmapSource WithTransparency(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * stride) + (x * 4);
                if (x < width / 2)
                {
                    pixels[i + 2] = 255;
                    pixels[i + 3] = 0;
                }
                else
                {
                    pixels[i] = 255;
                    pixels[i + 3] = 255;
                }
            }
        }

        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
    }

    public static byte[] Encode(BitmapEncoder encoder, BitmapSource source, BitmapMetadata? metadata = null)
    {
        encoder.Frames.Add(BitmapFrame.Create(source, null, metadata, null));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    public static byte[] Png(BitmapSource source) => Encode(new PngBitmapEncoder(), source);

    public static byte[] Jpeg(BitmapSource source) => Encode(new JpegBitmapEncoder { QualityLevel = 95 }, source);

    public static byte[] Bmp(BitmapSource source) => Encode(new BmpBitmapEncoder(), source);

    public static byte[] Gif(BitmapSource source) => Encode(new GifBitmapEncoder(), source);

    public static byte[] Tiff(BitmapSource source) => Encode(new TiffBitmapEncoder(), source);

    /// <summary>A JPEG whose EXIF Orientation tag is <paramref name="orientation"/>.</summary>
    public static byte[] JpegWithOrientation(BitmapSource source, ushort orientation)
    {
        var metadata = new BitmapMetadata("jpg");
        metadata.SetQuery("/app1/ifd/{ushort=274}", orientation);
        return Encode(new JpegBitmapEncoder { QualityLevel = 95 }, source, metadata);
    }

    /// <summary>A 4000x3000 landscape JPEG without orientation.</summary>
    public static byte[] LargeJpeg() => LargeJpegData.Value;

    /// <summary>Colour letter of the pixel (nearest of red, green, blue, yellow).</summary>
    public static char ColorAt(BitmapSource source, int x, int y)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        converted.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
        var (b, g, r) = (pixel[0], pixel[1], pixel[2]);
        (char Name, int B, int G, int R)[] palette =
        [
            (Red, 0, 0, 255), (Green, 0, 200, 0), (Blue, 255, 0, 0), (Yellow, 0, 255, 255),
        ];
        return palette.MinBy(p => Math.Abs(p.B - b) + Math.Abs(p.G - g) + Math.Abs(p.R - r)).Name;
    }

    /// <summary>The colours at the centres of the four quadrants: top-left, top-right, bottom-left, bottom-right.</summary>
    public static string Corners(BitmapSource source)
    {
        var w = source.PixelWidth;
        var h = source.PixelHeight;
        return new string(
        [
            ColorAt(source, w / 4, h / 4),
            ColorAt(source, 3 * w / 4, h / 4),
            ColorAt(source, w / 4, 3 * h / 4),
            ColorAt(source, 3 * w / 4, 3 * h / 4),
        ]);
    }
}
