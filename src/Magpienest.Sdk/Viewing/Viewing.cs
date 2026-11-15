using System.IO;

namespace Magpienest.Sdk.Viewing;

/// <summary>Provided by the ImageViewer plugin.</summary>
public interface IImageViewerService
{
    /// <summary>Whether a viewer is open.</summary>
    bool IsOpen { get; }

    /// <summary>Opens the viewer as a Shell overlay. If a viewer is already open it is replaced.</summary>
    /// <param name="request">The request.</param>
    void Open(ViewerRequest request);
}

/// <summary>Viewer mode.</summary>
public enum ViewerMode
{
    /// <summary>One image at a time.</summary>
    Gallery,
    /// <summary>Pages with spreads.</summary>
    Book,
}

/// <summary>Reading direction.</summary>
public enum ReadingDirection
{
    /// <summary>Manga order.</summary>
    RightToLeft,
    /// <summary>Western order.</summary>
    LeftToRight,
}

/// <summary>Spread mode.</summary>
public enum SpreadMode
{
    /// <summary>One page.</summary>
    Single,
    /// <summary>Two pages.</summary>
    Double,
}

/// <summary>How pages follow each other on screen.</summary>
public enum PageFlow
{
    /// <summary>One frame at a time.</summary>
    Paged,
    /// <summary>All pages edge to edge in one continuous strip.</summary>
    Strip,
}

/// <summary>A viewer request.</summary>
public sealed record ViewerRequest
{
    /// <summary>Disposed by the viewer when it closes.</summary>
    public required IPageSource Pages { get; init; }
    /// <summary>First page shown.</summary>
    public int StartIndex { get; init; }
    /// <summary>Mode.</summary>
    public ViewerMode Mode { get; init; } = ViewerMode.Gallery;
    /// <summary>Title.</summary>
    public string? Title { get; init; }
    /// <summary>Book mode only; null → viewer defaults.</summary>
    public BookSettings? Book { get; init; }
    /// <summary>Called when the user changes book settings.</summary>
    public Action<BookSettings>? OnBookSettingsChanged { get; init; }
    /// <summary>Debounced ≤ 1/s, and once on close.</summary>
    public Action<ViewerProgress>? OnProgress { get; init; }
    /// <summary>Called after the viewer closed.</summary>
    public Action? OnClosed { get; init; }
}

/// <summary>Per-book settings.</summary>
/// <param name="Direction">Reading direction.</param>
/// <param name="Spread">Spread mode.</param>
/// <param name="CoverAlone">First page shown alone.</param>
/// <param name="Flow">Paged or continuous strip.</param>
public sealed record BookSettings(ReadingDirection Direction, SpreadMode Spread, bool CoverAlone,
                                  PageFlow Flow = PageFlow.Paged);

/// <summary>Viewing progress.</summary>
/// <param name="PageIndex">First page of the current frame.</param>
/// <param name="PageCount">Page count.</param>
/// <param name="ReachedEnd">The current frame contains the last page.</param>
/// <param name="ElapsedSinceLastReport">Active viewing time since the previous report.</param>
public sealed record ViewerProgress(int PageIndex, int PageCount, bool ReachedEnd, TimeSpan ElapsedSinceLastReport);

/// <summary>An ordered list of pages.</summary>
public interface IPageSource : IAsyncDisposable
{
    /// <summary>Page count.</summary>
    int Count { get; }

    /// <summary>Display name of a page.</summary>
    /// <param name="index">Page index.</param>
    string GetName(int index);

    /// <summary>File path; null when the page is inside an archive.</summary>
    /// <param name="index">Page index.</param>
    string? GetFilePath(int index);

    /// <summary>Seekable stream; caller disposes; thread-safe.</summary>
    /// <param name="index">Page index.</param>
    /// <param name="ct">Cancellation.</param>
    Task<Stream> OpenAsync(int index, CancellationToken ct = default);
}

/// <summary>Ready-made source for a list of image files.</summary>
public sealed class FilePageSource : IPageSource
{
    private readonly IReadOnlyList<string> _paths;

    /// <summary>Creates the source.</summary>
    /// <param name="paths">Absolute file paths in order.</param>
    public FilePageSource(IReadOnlyList<string> paths) => _paths = paths;

    /// <inheritdoc />
    public int Count => _paths.Count;

    /// <inheritdoc />
    public string GetName(int index) => Path.GetFileName(_paths[index]);

    /// <inheritdoc />
    public string? GetFilePath(int index) => _paths[index];

    /// <inheritdoc />
    public Task<Stream> OpenAsync(int index, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Stream s = new FileStream(_paths[index], FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
        return Task.FromResult(s);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Which pages are shown together.</summary>
/// <param name="First">First page index.</param>
/// <param name="Last">Last page index; equals <paramref name="First"/> for a single page.</param>
public readonly record struct PageFrame(int First, int Last);

/// <summary>
/// Which pages are shown together (lives in the SDK so every front end pairs pages the
/// same way). Pure functions; algorithm in phase-2 T2.3 "Frame layout".
/// </summary>
public static class FrameLayout
{
    /// <summary>The frame that starts at page <paramref name="index"/>.</summary>
    /// <param name="index">Page index.</param>
    /// <param name="count">Page count.</param>
    /// <param name="spread">Spread mode.</param>
    /// <param name="coverAlone">First page alone.</param>
    /// <param name="isWide">Whether a page is wider than tall (unknown → false).</param>
    public static PageFrame Forward(int index, int count, SpreadMode spread, bool coverAlone, Func<int, bool> isWide)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);
        if (spread == SpreadMode.Single || isWide(index) || index == count - 1
            || isWide(index + 1) || (coverAlone && index == 0))
            return new PageFrame(index, index);
        return new PageFrame(index, index + 1);
    }

    /// <summary>The frame that ends at page <paramref name="index"/> (used when paging back).</summary>
    /// <param name="index">Page index.</param>
    /// <param name="count">Page count.</param>
    /// <param name="spread">Spread mode.</param>
    /// <param name="coverAlone">First page alone.</param>
    /// <param name="isWide">Whether a page is wider than tall (unknown → false).</param>
    public static PageFrame Backward(int index, int count, SpreadMode spread, bool coverAlone, Func<int, bool> isWide)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);
        if (spread == SpreadMode.Single || isWide(index) || index == 0
            || isWide(index - 1) || (coverAlone && index - 1 == 0))
            return new PageFrame(index, index);
        return new PageFrame(index - 1, index);
    }
}
