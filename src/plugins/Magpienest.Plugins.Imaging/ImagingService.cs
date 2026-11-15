using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Magpienest.Sdk;
using Magpienest.Sdk.Imaging;

namespace Magpienest.Plugins.Imaging;

/// <summary>WIC based <see cref="IImagingService"/> with a pluggable decoder registry.</summary>
internal sealed class ImagingService : IImagingService
{
    private static readonly string[] BuiltInExtensions =
    [
        ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff", ".ico",
        ".webp", ".wdp", ".jxr", ".heic", ".heif", ".avif", ".jxl",
    ];

    private readonly ILog _log;
    private readonly SemaphoreSlim _decodeGate = new(Environment.ProcessorCount);
    private readonly object _registryLock = new();
    private volatile Registry _registry = Registry.Create([]);

    public ImagingService(ILog log) => _log = log;

    /// <inheritdoc />
    public IReadOnlySet<string> SupportedExtensions => _registry.Extensions;

    /// <inheritdoc />
    public bool IsSupported(string pathOrExtension)
    {
        var extension = ExtensionOf(pathOrExtension);
        return extension.Length > 0 && _registry.Extensions.Contains(extension);
    }

    /// <inheritdoc />
    public async Task<BitmapSource> DecodeAsync(Stream seekable, DecodeOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(seekable);
        ArgumentNullException.ThrowIfNull(options);
        await _decodeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => DecodeCore(seekable, options, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    /// <inheritdoc />
    public Task<ImageInfo?> ReadInfoAsync(Stream seekable, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(seekable);
        return Task.Run(() => ReadInfoCore(seekable, ct), ct);
    }

    /// <inheritdoc />
    public byte[] EncodeJpeg(BitmapSource source, int quality)
    {
        ArgumentNullException.ThrowIfNull(source);
        var flattened = FlattenOnWhite(source);
        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
        encoder.Frames.Add(BitmapFrame.Create(flattened));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    /// <inheritdoc />
    public void RegisterDecoder(IPluginContext owner, IImageDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(decoder);
        lock (_registryLock)
        {
            _registry = Registry.Create([.. _registry.Decoders, decoder]);
        }

        owner.Track(() =>
        {
            lock (_registryLock)
            {
                _registry = Registry.Create(_registry.Decoders.Where(d => !ReferenceEquals(d, decoder)).ToArray());
            }
        });
    }

    private static string ExtensionOf(string? pathOrExtension)
    {
        if (string.IsNullOrWhiteSpace(pathOrExtension))
        {
            return string.Empty;
        }

        var text = pathOrExtension.Trim();
        var extension = Path.GetExtension(text);
        if (extension.Length == 0 && text.IndexOfAny(['.', '\\', '/', ':']) < 0)
        {
            extension = "." + text;
        }

        return extension.ToLowerInvariant();
    }

    /// <summary>
    /// The stream has no extension of its own. A file stream tells us its name; for other streams every registered
    /// decoder is probed with <see cref="IImageDecoder.ReadInfo"/> (null = cannot read it).
    /// </summary>
    private IImageDecoder? FindCustomDecoder(Stream stream)
    {
        var decoders = _registry.Decoders;
        if (decoders.Length == 0)
        {
            return null;
        }

        if (stream is FileStream file)
        {
            var extension = ExtensionOf(Path.GetExtension(file.Name));
            if (extension.Length > 0)
            {
                // Latest registration wins.
                for (var i = decoders.Length - 1; i >= 0; i--)
                {
                    if (decoders[i].Extensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase)))
                    {
                        return decoders[i];
                    }
                }

                return null;
            }
        }

        for (var i = decoders.Length - 1; i >= 0; i--)
        {
            try
            {
                Rewind(stream);
                if (decoders[i].ReadInfo(stream) is not null)
                {
                    return decoders[i];
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Debug($"Decoder probe failed: {ex.Message}");
            }
        }

        return null;
    }

    private static void Rewind(Stream stream) => stream.Position = 0;

    private BitmapSource DecodeCore(Stream stream, DecodeOptions options, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var custom = FindCustomDecoder(stream);
            Rewind(stream);
            BitmapSource result;
            if (custom is not null)
            {
                result = custom.Decode(stream, options, ct);
            }
            else
            {
                result = DecodeWic(stream, options, ct);
            }

            ct.ThrowIfCancellationRequested();
            return Normalize(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ImageDecodeException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ImageDecodeException("The image could not be decoded.", ex);
        }
    }

    private static BitmapSource DecodeWic(Stream stream, DecodeOptions options, CancellationToken ct)
    {
        var raw = ReadRaw(stream);
        ct.ThrowIfCancellationRequested();
        Rewind(stream);

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.StreamSource = stream;

        // The limits apply to the oriented image; the decoder scales the raw pixels, so swap the axes for 5..8.
        var swapped = raw.Orientation >= 5;
        var boxWidth = swapped ? options.MaxPixelHeight : options.MaxPixelWidth;
        var boxHeight = swapped ? options.MaxPixelWidth : options.MaxPixelHeight;
        var widthRatio = boxWidth > 0 ? (double)boxWidth / raw.Width : double.PositiveInfinity;
        var heightRatio = boxHeight > 0 ? (double)boxHeight / raw.Height : double.PositiveInfinity;
        if (Math.Min(widthRatio, heightRatio) < 1)
        {
            // Only one of the two is set: setting both distorts. The limiting axis keeps the other inside the box.
            if (widthRatio <= heightRatio)
            {
                image.DecodePixelWidth = boxWidth;
            }
            else
            {
                image.DecodePixelHeight = boxHeight;
            }
        }

        image.EndInit();
        image.Freeze();
        ct.ThrowIfCancellationRequested();

        BitmapSource result = image;
        var transform = OrientationTransform(raw.Orientation);
        if (transform is not null)
        {
            var transformed = new TransformedBitmap(image, transform);
            transformed.Freeze();
            result = transformed;
        }

        return result;
    }

    private static ImageInfo? ReadInfoCoreWic(Stream stream)
    {
        var raw = ReadRaw(stream);
        return raw.Orientation >= 5 ? new ImageInfo(raw.Height, raw.Width) : new ImageInfo(raw.Width, raw.Height);
    }

    private ImageInfo? ReadInfoCore(Stream stream, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var custom = FindCustomDecoder(stream);
            Rewind(stream);
            return custom is not null ? custom.ReadInfo(stream) : ReadInfoCoreWic(stream);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug($"Image info could not be read: {ex.Message}");
            return null;
        }
    }

    private static (int Width, int Height, int Orientation) ReadRaw(Stream stream)
    {
        Rewind(stream);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        return (frame.PixelWidth, frame.PixelHeight, ReadOrientation(frame));
    }

    private static int ReadOrientation(BitmapFrame frame)
    {
        string[] queries = ["/app1/ifd/{ushort=274}", "/ifd/{ushort=274}"];
        BitmapMetadata? metadata;
        try
        {
            metadata = frame.Metadata as BitmapMetadata;
        }
        catch (Exception)
        {
            return 1;
        }

        if (metadata is null)
        {
            return 1;
        }

        foreach (var query in queries)
        {
            try
            {
                if (metadata.GetQuery(query) is { } value)
                {
                    var orientation = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
                    if (orientation is >= 1 and <= 8)
                    {
                        return orientation;
                    }
                }
            }
            catch (Exception)
            {
                // Many formats have no such metadata.
            }
        }

        return 1;
    }

    /// <summary>EXIF orientation to the transform that displays the image upright (null = nothing to do).</summary>
    private static Transform? OrientationTransform(int orientation) => orientation switch
    {
        2 => new ScaleTransform(-1, 1),
        3 => new RotateTransform(180),
        4 => new ScaleTransform(1, -1),
        5 => new TransformGroup { Children = { new RotateTransform(90), new ScaleTransform(-1, 1) } },
        6 => new RotateTransform(90),
        7 => new TransformGroup { Children = { new RotateTransform(270), new ScaleTransform(-1, 1) } },
        8 => new RotateTransform(270),
        _ => null,
    };

    /// <summary>Makes sure the result is a frozen Pbgra32 or Bgr32 bitmap.</summary>
    private static BitmapSource Normalize(BitmapSource source)
    {
        var result = source;
        if (source.Format != PixelFormats.Pbgra32 && source.Format != PixelFormats.Bgr32)
        {
            result = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        }

        if (!result.IsFrozen)
        {
            result.Freeze();
        }

        return result;
    }

    private static BitmapSource FlattenOnWhite(BitmapSource source)
    {
        var opaque = source.Format == PixelFormats.Bgr32
            || source.Format == PixelFormats.Bgr24
            || source.Format == PixelFormats.Gray8
            || source.Format == PixelFormats.Rgb24
            || source.Format == PixelFormats.Bgr565
            || source.Format == PixelFormats.Bgr555
            || source.Format == PixelFormats.Gray16
            || source.Format == PixelFormats.Rgb48
            || source.Format == PixelFormats.BlackWhite;
        if (opaque)
        {
            return source;
        }

        // Premultiplied over white: out = premultiplied colour + (255 - alpha).
        var premultiplied = source.Format == PixelFormats.Pbgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        var width = premultiplied.PixelWidth;
        var height = premultiplied.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        premultiplied.CopyPixels(pixels, stride, 0);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var inverse = 255 - pixels[i + 3];
            pixels[i] = (byte)(pixels[i] + inverse);
            pixels[i + 1] = (byte)(pixels[i + 1] + inverse);
            pixels[i + 2] = (byte)(pixels[i + 2] + inverse);
            pixels[i + 3] = 255;
        }

        var flattened = BitmapSource.Create(width, height, source.DpiX, source.DpiY, PixelFormats.Bgr32, null, pixels, stride);
        flattened.Freeze();
        return flattened;
    }

    /// <summary>Immutable snapshot, replaced as a whole so readers on any thread need no lock.</summary>
    private sealed class Registry
    {
        private Registry(IImageDecoder[] decoders, HashSet<string> extensions)
        {
            Decoders = decoders;
            Extensions = extensions;
        }

        public IImageDecoder[] Decoders { get; }

        public HashSet<string> Extensions { get; }

        public static Registry Create(IImageDecoder[] decoders)
        {
            var extensions = new HashSet<string>(BuiltInExtensions, StringComparer.OrdinalIgnoreCase);
            foreach (var decoder in decoders)
            {
                foreach (var extension in decoder.Extensions)
                {
                    var normalized = ExtensionOf(extension);
                    if (normalized.Length > 0)
                    {
                        extensions.Add(normalized);
                    }
                }
            }

            return new Registry(decoders, extensions);
        }
    }
}
