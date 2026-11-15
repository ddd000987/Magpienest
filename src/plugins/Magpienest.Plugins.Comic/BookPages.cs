using System.Globalization;
using Magpienest.Sdk.Archives;
using Magpienest.Sdk.Imaging;
using Magpienest.Sdk.Viewing;

namespace Magpienest.Plugins.Comic;

/// <summary>Lists a book's pages in reading order: a folder's images or an archive's image entries.</summary>
internal sealed class BookPages(IImagingService imaging, IArchiveService? archives)
{
    /// <summary>Natural order (<c>2</c> before <c>10</c>), case-insensitive (architecture pitfall 12).</summary>
    public static readonly StringComparer Natural =
        StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.NumericOrdering | CompareOptions.IgnoreCase);

    /// <summary>Archive keys compared segment by segment, so <c>ch2/1.jpg</c> precedes <c>ch10/1.jpg</c>.</summary>
    public static readonly IComparer<string> KeyOrder = Comparer<string>.Create(CompareKeys);

    /// <summary>Whether the book is an archive file (as opposed to a folder).</summary>
    public static bool IsArchiveBook(string bookPath) => File.Exists(bookPath);

    /// <summary>The caller disposes the result. Throws IOException when the book cannot be read.</summary>
    public async Task<IPageSource> CreateAsync(string bookPath, CancellationToken ct = default)
    {
        if (Directory.Exists(bookPath))
        {
            return await Task.Run(() => FolderPages(bookPath), ct).ConfigureAwait(false);
        }

        if (!File.Exists(bookPath))
        {
            throw new FileNotFoundException("The book was not found.", bookPath);
        }

        if (archives is null || !archives.IsSupported(bookPath))
        {
            throw new ArchiveSupportMissingException(bookPath);
        }

        var archive = await archives.OpenAsync(bookPath, ct).ConfigureAwait(false);
        try
        {
            var pages = archive.Entries.Where(e => !IsJunk(e.Key) && imaging.IsSupported(e.Key))
                                       .OrderBy(e => e.Key, KeyOrder)
                                       .ToList();
            return new ArchivePageSource(archive, pages);
        }
        catch
        {
            await archive.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private FilePageSource FolderPages(string folder)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        var files = new DirectoryInfo(folder).EnumerateFiles("*", options)
                                            .Where(f => !f.Name.StartsWith('.') && imaging.IsSupported(f.Extension))
                                            .OrderBy(f => f.Name, Natural)
                                            .Select(f => f.FullName)
                                            .ToList();
        return new FilePageSource(files);
    }

    /// <summary>macOS resource forks and hidden files.</summary>
    internal static bool IsJunk(string key)
    {
        var segments = key.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0
               || segments.Any(s => s.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase))
               || segments[^1].StartsWith('.');
    }

    private static int CompareKeys(string? a, string? b)
    {
        var left = (a ?? "").Split('/');
        var right = (b ?? "").Split('/');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var c = Natural.Compare(left[i], right[i]);
            if (c != 0)
            {
                return c;
            }
        }

        return left.Length.CompareTo(right.Length);
    }
}

/// <summary>Pages inside an open archive; disposing it closes the archive.</summary>
internal sealed class ArchivePageSource(IArchive archive, IReadOnlyList<ArchiveEntry> pages) : IPageSource
{
    public int Count => pages.Count;

    public string GetName(int index)
    {
        var key = pages[index].Key;
        var slash = key.LastIndexOf('/');
        return slash < 0 ? key : key[(slash + 1)..];
    }

    public string? GetFilePath(int index) => null;

    public Task<Stream> OpenAsync(int index, CancellationToken ct = default) => archive.OpenEntryAsync(pages[index], ct);

    public ValueTask DisposeAsync() => archive.DisposeAsync();
}

/// <summary>An archive book cannot be opened because the Archives plugin is off.</summary>
internal sealed class ArchiveSupportMissingException(string path)
    : IOException($"Archive support is not available for {path}.");
