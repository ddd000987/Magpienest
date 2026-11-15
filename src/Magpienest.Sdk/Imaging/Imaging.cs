using System.IO;
using System.Windows.Media.Imaging;

namespace Magpienest.Sdk.Imaging;

/// <summary>Provided by the Imaging plugin.</summary>
public interface IImagingService
{
    /// <summary>Lower-case with dot, e.g. ".jpg".</summary>
    IReadOnlySet<string> SupportedExtensions { get; }

    /// <summary>Whether the path or extension can be decoded.</summary>
    /// <param name="pathOrExtension">A path or an extension.</param>
    bool IsSupported(string pathOrExtension);

    /// <summary>
    /// Decodes off the UI thread. Result is frozen, EXIF orientation applied, Pbgra32/Bgr32.
    /// Throws <see cref="ImageDecodeException"/> on failure.
    /// </summary>
    /// <param name="seekable">Seekable stream.</param>
    /// <param name="options">Size limits.</param>
    /// <param name="ct">Cancellation.</param>
    Task<BitmapSource> DecodeAsync(Stream seekable, DecodeOptions options, CancellationToken ct = default);

    /// <summary>Oriented pixel size without a full decode; null when unreadable.</summary>
    /// <param name="seekable">Seekable stream.</param>
    /// <param name="ct">Cancellation.</param>
    Task<ImageInfo?> ReadInfoAsync(Stream seekable, CancellationToken ct = default);

    /// <summary>Encodes as JPEG, flattening transparency onto white.</summary>
    /// <param name="source">The bitmap.</param>
    /// <param name="quality">1..100.</param>
    byte[] EncodeJpeg(BitmapSource source, int quality);

    /// <summary>Adds a decoder; removed when <paramref name="owner"/> deactivates.</summary>
    /// <param name="owner">Registering plugin.</param>
    /// <param name="decoder">The decoder.</param>
    void RegisterDecoder(IPluginContext owner, IImageDecoder decoder);
}

/// <summary>0 = unlimited. The result fits inside the box, keeps aspect ratio, is never upscaled.</summary>
/// <param name="MaxPixelWidth">Maximum width.</param>
/// <param name="MaxPixelHeight">Maximum height.</param>
public sealed record DecodeOptions(int MaxPixelWidth = 0, int MaxPixelHeight = 0);

/// <summary>Oriented pixel size.</summary>
/// <param name="PixelWidth">Width.</param>
/// <param name="PixelHeight">Height.</param>
public sealed record ImageInfo(int PixelWidth, int PixelHeight);

/// <summary>An image could not be decoded.</summary>
public sealed class ImageDecodeException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ImageDecodeException() { }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">Message.</param>
    public ImageDecodeException(string message) : base(message) { }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">Message.</param>
    /// <param name="inner">Cause.</param>
    public ImageDecodeException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Extension point for extra formats. Registered decoders win over the built-in WIC decoder.</summary>
public interface IImageDecoder
{
    /// <summary>Lower-case extensions with dot.</summary>
    IReadOnlyCollection<string> Extensions { get; }

    /// <summary>Decodes on a background thread; returns a frozen bitmap.</summary>
    /// <param name="seekable">Seekable stream.</param>
    /// <param name="options">Size limits.</param>
    /// <param name="ct">Cancellation.</param>
    BitmapSource Decode(Stream seekable, DecodeOptions options, CancellationToken ct);

    /// <summary>Oriented pixel size, or null.</summary>
    /// <param name="seekable">Seekable stream.</param>
    ImageInfo? ReadInfo(Stream seekable);
}
