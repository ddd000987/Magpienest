using System.Windows.Media;
using System.Windows.Media.Imaging;
using Magpienest.Sdk.Library;

namespace Magpienest.Sdk.Thumbnails;

/// <summary>Provided by the Thumbnails plugin.</summary>
public interface IThumbnailService
{
    /// <summary>Registers the provider for a kind; removed when <paramref name="owner"/> deactivates.</summary>
    /// <param name="owner">Registering plugin.</param>
    /// <param name="kind">Kind id.</param>
    /// <param name="provider">The provider.</param>
    void RegisterProvider(IPluginContext owner, string kind, IThumbnailProvider provider);

    /// <summary>
    /// Memory cache → disk cache → provider. null = no thumbnail (caller shows a placeholder).
    /// The result is frozen and at most <see cref="ThumbnailDefaults.ThumbnailSize"/> pixels on its long edge.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="ct">Cancellation.</param>
    Task<ImageSource?> GetAsync(LibraryItem item, CancellationToken ct = default);

    /// <summary>
    /// Same pipeline and cache as <see cref="GetAsync"/>, but returns the cached JPEG bytes
    /// (for non-WPF front ends such as the web interface). null = no thumbnail. Any thread.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="ct">Cancellation.</param>
    Task<byte[]?> GetJpegAsync(LibraryItem item, CancellationToken ct = default);

    /// <summary>Drops the cached thumbnail of an item.</summary>
    /// <param name="item">The item.</param>
    Task InvalidateAsync(LibraryItem item);

    /// <summary>Clears the memory and disk caches.</summary>
    Task ClearCacheAsync();

    /// <summary>Windows shell thumbnail or icon for any file (IShellItemImageFactory). null on failure.</summary>
    /// <param name="path">File path.</param>
    /// <param name="pixelSize">Requested size.</param>
    /// <param name="iconOnly">Icon instead of a thumbnail.</param>
    /// <param name="ct">Cancellation.</param>
    Task<BitmapSource?> GetShellImageAsync(string path, int pixelSize, bool iconOnly, CancellationToken ct = default);
}

/// <summary>Creates thumbnails for one kind.</summary>
public interface IThumbnailProvider
{
    /// <summary>Changes whenever the cached thumbnail must be regenerated.</summary>
    /// <param name="item">The item.</param>
    string GetStamp(LibraryItem item);

    /// <summary>Background thread. Return a frozen bitmap no larger than maxPixelSize on its long edge, or null.</summary>
    /// <param name="item">The item.</param>
    /// <param name="maxPixelSize">Long-edge limit.</param>
    /// <param name="ct">Cancellation.</param>
    Task<BitmapSource?> CreateAsync(LibraryItem item, int maxPixelSize, CancellationToken ct);
}

/// <summary>Thumbnail constants.</summary>
public static class ThumbnailDefaults
{
    /// <summary>Long edge, pixels.</summary>
    public const int ThumbnailSize = 384;

    /// <summary>JPEG quality.</summary>
    public const int JpegQuality = 80;

    /// <summary>File size, mtime and cover.</summary>
    /// <param name="item">The item.</param>
    public static string DefaultStamp(LibraryItem item)
        => $"{item.FileSize}:{item.FileModified.UtcTicks}:{item.CoverPath}";
}
