using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using Magpienest.Sdk.Viewing;

namespace Magpienest.Sdk.Library;

/// <summary>User-visible state of an item.</summary>
public enum ItemStatus
{
    /// <summary>No status.</summary>
    None = 0,
    /// <summary>Started.</summary>
    InProgress = 1,
    /// <summary>Finished.</summary>
    Completed = 2,
    /// <summary>Paused by the user.</summary>
    OnHold = 3,
    /// <summary>Abandoned by the user.</summary>
    Dropped = 4,
    /// <summary>Planned by the user.</summary>
    Planned = 5,
}

/// <summary>One library entry.</summary>
public sealed record LibraryItem
{
    /// <summary>Database id.</summary>
    public long Id { get; init; }
    /// <summary>Kind id, e.g. "illustration".</summary>
    public required string Kind { get; init; }
    /// <summary>Source folder id; null for manually added items.</summary>
    public long? SourceId { get; init; }
    /// <summary>Absolute path.</summary>
    public required string Path { get; init; }
    /// <summary>Parent directory of <see cref="Path"/>.</summary>
    public required string Directory { get; init; }
    /// <summary>Display title.</summary>
    public required string Title { get; init; }
    /// <summary>File size in bytes.</summary>
    public long FileSize { get; init; }
    /// <summary>Last write time of the file.</summary>
    public DateTimeOffset FileModified { get; init; }
    /// <summary>When the item was added.</summary>
    public DateTimeOffset AddedAt { get; init; }
    /// <summary>When the item was last opened.</summary>
    public DateTimeOffset? LastOpenedAt { get; init; }
    /// <summary>How often the item was opened.</summary>
    public int OpenCount { get; init; }
    /// <summary>0 = unrated, 1..5.</summary>
    public int Rating { get; init; }
    /// <summary>Favourite flag.</summary>
    public bool IsFavorite { get; init; }
    /// <summary>Status.</summary>
    public ItemStatus Status { get; init; }
    /// <summary>Progress position; meaning per kind.</summary>
    public long ProgressPosition { get; init; }
    /// <summary>Progress total; meaning per kind.</summary>
    public long ProgressTotal { get; init; }
    /// <summary>Time spent in seconds.</summary>
    public long TimeSpentSeconds { get; init; }
    /// <summary>The file was not found by the last scan.</summary>
    public bool IsMissing { get; init; }
    /// <summary>User notes.</summary>
    public string Notes { get; init; } = "";
    /// <summary>Absolute path of a user-chosen cover, or null.</summary>
    public string? CoverPath { get; init; }
    /// <summary>Kind-specific JSON.</summary>
    public string Extra { get; init; } = "{}";
    /// <summary>Tag names.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>Helpers for <see cref="LibraryItem.Extra"/>.</summary>
public static class LibraryItemExtensions
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Reads <see cref="LibraryItem.Extra"/>; returns a new instance when it is empty or invalid.</summary>
    /// <typeparam name="T">Extra type.</typeparam>
    /// <param name="item">The item.</param>
    public static T GetExtra<T>(this LibraryItem item) where T : new()
    {
        if (string.IsNullOrWhiteSpace(item.Extra))
            return new T();
        try
        {
            return JsonSerializer.Deserialize<T>(item.Extra, Options) ?? new T();
        }
        catch (JsonException)
        {
            return new T();
        }
    }

    /// <summary>Serializes a value for <see cref="ItemPatch.Extra"/> / <see cref="NewItem.Extra"/>.</summary>
    /// <typeparam name="T">Extra type.</typeparam>
    /// <param name="value">The value.</param>
    public static string SerializeExtra<T>(T value) => JsonSerializer.Serialize(value, Options);
}

/// <summary>An item to add manually.</summary>
public sealed record NewItem
{
    /// <summary>Kind id.</summary>
    public required string Kind { get; init; }
    /// <summary>Absolute path.</summary>
    public required string Path { get; init; }
    /// <summary>Title.</summary>
    public required string Title { get; init; }
    /// <summary>Kind-specific JSON.</summary>
    public string Extra { get; init; } = "{}";
}

/// <summary>A change to an item. Only non-null members are applied.</summary>
public sealed record ItemPatch
{
    /// <summary>New title.</summary>
    public string? Title { get; init; }
    /// <summary>New path; allowed only for items without a source (manual items).</summary>
    public string? Path { get; init; }
    /// <summary>New rating 0..5.</summary>
    public int? Rating { get; init; }
    /// <summary>New favourite flag.</summary>
    public bool? IsFavorite { get; init; }
    /// <summary>New status.</summary>
    public ItemStatus? Status { get; init; }
    /// <summary>New progress position.</summary>
    public long? ProgressPosition { get; init; }
    /// <summary>New progress total.</summary>
    public long? ProgressTotal { get; init; }
    /// <summary>Seconds added to the stored time spent.</summary>
    public long? AddTimeSpentSeconds { get; init; }
    /// <summary>Sets LastOpenedAt = now and OpenCount += 1.</summary>
    public bool MarkOpened { get; init; }
    /// <summary>New notes.</summary>
    public string? Notes { get; init; }
    /// <summary>New kind-specific JSON.</summary>
    public string? Extra { get; init; }
    /// <summary>Copies this file into the item's media folder and uses it as cover.</summary>
    public string? SetCoverFromFile { get; init; }
    /// <summary>Removes the cover.</summary>
    public bool ClearCover { get; init; }
}

/// <summary>Sort keys.</summary>
public enum ItemSort
{
    /// <summary>Natural title order.</summary>
    Title,
    /// <summary>Natural path order.</summary>
    Path,
    /// <summary>Date added.</summary>
    AddedAt,
    /// <summary>Last opened.</summary>
    LastOpenedAt,
    /// <summary>Rating.</summary>
    Rating,
    /// <summary>File date.</summary>
    FileModified,
    /// <summary>File size.</summary>
    FileSize,
    /// <summary>Time spent.</summary>
    TimeSpent,
    /// <summary>Progress fraction.</summary>
    Progress,
}

/// <summary>An item query.</summary>
public sealed record ItemQuery
{
    /// <summary>Kind id.</summary>
    public required string Kind { get; init; }
    /// <summary>Case-insensitive substring of the title or of any tag.</summary>
    public string? Text { get; init; }
    /// <summary>Directory filter.</summary>
    public string? Directory { get; init; }
    /// <summary>Whether <see cref="Directory"/> includes its subdirectories.</summary>
    public bool IncludeSubdirectories { get; init; } = true;
    /// <summary>Source filter.</summary>
    public long? SourceId { get; init; }
    /// <summary>The item must have all of these tags.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
    /// <summary>Favourites only.</summary>
    public bool FavoritesOnly { get; init; }
    /// <summary>Minimum rating.</summary>
    public int MinRating { get; init; }
    /// <summary>Status filter.</summary>
    public ItemStatus? Status { get; init; }
    /// <summary>Include items flagged missing.</summary>
    public bool IncludeMissing { get; init; }
    /// <summary>Sort key.</summary>
    public ItemSort Sort { get; init; } = ItemSort.Title;
    /// <summary>Descending order.</summary>
    public bool Descending { get; init; }
    /// <summary>Maximum number of results (applied after sorting).</summary>
    public int? Limit { get; init; }
}

/// <summary>A library folder.</summary>
/// <param name="Id">Database id.</param>
/// <param name="Kind">Kind id.</param>
/// <param name="Path">Absolute folder path.</param>
/// <param name="AddedAt">When it was added.</param>
/// <param name="LastScanAt">When it was last scanned.</param>
/// <param name="ItemCount">Number of items in it.</param>
public sealed record LibrarySource(long Id, string Kind, string Path, DateTimeOffset AddedAt,
                                   DateTimeOffset? LastScanAt, int ItemCount);

/// <summary>Scan progress.</summary>
/// <param name="Found">Entries found so far.</param>
/// <param name="CurrentDirectory">Directory being scanned.</param>
public sealed record ScanProgress(int Found, string? CurrentDirectory);

/// <summary>Scan outcome.</summary>
/// <param name="Added">New rows.</param>
/// <param name="Updated">Changed rows.</param>
/// <param name="MarkedMissing">Rows flagged missing.</param>
/// <param name="Relinked">Moved/renamed rows relinked.</param>
/// <param name="Total">Entries the scanner returned.</param>
public sealed record ScanResult(int Added, int Updated, int MarkedMissing, int Relinked, int Total);

/// <summary>A tag and the number of items carrying it.</summary>
/// <param name="Name">Tag name.</param>
/// <param name="Count">Item count.</param>
public sealed record TagCount(string Name, int Count);

/// <summary>Describes an item kind.</summary>
public sealed record ItemKindDescriptor
{
    /// <summary>"illustration" | "comic" | "video" | "game" | …</summary>
    public required string Id { get; init; }
    /// <summary>Localization key of the display name.</summary>
    public required string DisplayNameKey { get; init; }
    /// <summary>null → items are only added manually.</summary>
    public IItemScanner? Scanner { get; init; }
    /// <summary>
    /// For page-based kinds (comic): the item's pages in reading order; null for other kinds.
    /// Lets any front end open a book without knowing how the kind lists pages. The caller disposes the result.
    /// </summary>
    public Func<LibraryItem, CancellationToken, Task<IPageSource>>? OpenPages { get; init; }
}

/// <summary>Finds items under a source folder.</summary>
public interface IItemScanner
{
    /// <summary>
    /// Enumerates every item under root. Called on a background thread.
    /// Must skip unreadable folders/files silently and honour ct promptly.
    /// </summary>
    /// <param name="root">Source folder.</param>
    /// <param name="ct">Cancellation.</param>
    IEnumerable<ScannedEntry> Scan(string root, CancellationToken ct);
}

/// <summary>One scanner result.</summary>
/// <param name="Path">Absolute path.</param>
/// <param name="Title">Automatic title.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="Modified">Last write time.</param>
public sealed record ScannedEntry(string Path, string Title, long Size, DateTimeOffset Modified);

/// <summary>Provided by the Library plugin.</summary>
public interface ILibraryService
{
    /// <summary>Registers a kind; removed when <paramref name="owner"/> deactivates.</summary>
    /// <param name="owner">Registering plugin.</param>
    /// <param name="kind">The kind.</param>
    void RegisterKind(IPluginContext owner, ItemKindDescriptor kind);

    /// <summary>Registered kinds.</summary>
    IReadOnlyList<ItemKindDescriptor> Kinds { get; }

    /// <summary>Sources of a kind.</summary>
    /// <param name="kind">Kind id.</param>
    Task<IReadOnlyList<LibrarySource>> GetSourcesAsync(string kind);

    /// <summary>Adds a folder. Throws ArgumentException when it equals, contains or is inside an existing source of the same kind.</summary>
    /// <param name="kind">Kind id.</param>
    /// <param name="path">Folder path.</param>
    Task<LibrarySource> AddSourceAsync(string kind, string path);

    /// <summary>Removes the source and its items from the database. Never touches files.</summary>
    /// <param name="sourceId">Source id.</param>
    Task RemoveSourceAsync(long sourceId);

    /// <summary>Scans a source.</summary>
    /// <param name="sourceId">Source id.</param>
    /// <param name="progress">Progress sink.</param>
    /// <param name="ct">Cancellation.</param>
    Task<ScanResult> ScanAsync(long sourceId, IProgress<ScanProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Queries items.</summary>
    /// <param name="query">The query.</param>
    /// <param name="ct">Cancellation.</param>
    Task<IReadOnlyList<LibraryItem>> QueryAsync(ItemQuery query, CancellationToken ct = default);

    /// <summary>Gets one item, or null.</summary>
    /// <param name="id">Item id.</param>
    Task<LibraryItem?> GetAsync(long id);

    /// <summary>Adds a manual item; throws if (kind, path) already exists.</summary>
    /// <param name="item">The item.</param>
    Task<LibraryItem> AddAsync(NewItem item);

    /// <summary>Applies a patch.</summary>
    /// <param name="id">Item id.</param>
    /// <param name="patch">The patch.</param>
    Task UpdateAsync(long id, ItemPatch patch);

    /// <summary>Removes items from the database only.</summary>
    /// <param name="ids">Item ids.</param>
    Task RemoveAsync(IReadOnlyCollection<long> ids);

    /// <summary>Replaces the item's tag set.</summary>
    /// <param name="id">Item id.</param>
    /// <param name="tags">New tags.</param>
    Task SetTagsAsync(long id, IReadOnlyCollection<string> tags);

    /// <summary>Tags used by a kind, with counts.</summary>
    /// <param name="kind">Kind id.</param>
    Task<IReadOnlyList<TagCount>> GetTagsAsync(string kind);

    /// <summary>Distinct directory values of non-missing items.</summary>
    /// <param name="kind">Kind id.</param>
    Task<IReadOnlyList<string>> GetDirectoriesAsync(string kind);
}

/// <summary>Kind of library change.</summary>
public enum LibraryChangeType
{
    /// <summary>Items added.</summary>
    Added,
    /// <summary>Items updated.</summary>
    Updated,
    /// <summary>Items removed.</summary>
    Removed,
    /// <summary>Reload everything for the kind.</summary>
    Reset,
}

/// <summary>Kernel-bus event. Reset = reload everything for that kind (ItemIds is empty).</summary>
/// <param name="Kind">Kind id.</param>
/// <param name="Type">Change type.</param>
/// <param name="ItemIds">Affected items.</param>
public sealed record LibraryChanged(string Kind, LibraryChangeType Type, IReadOnlyList<long> ItemIds);

/// <summary>Creates the shared library browser.</summary>
public interface ILibraryBrowserFactory
{
    /// <summary>
    /// Returns a complete, self-contained browser view for one kind (toolbar, folder tree,
    /// virtualized tile grid, details panel, empty state, folder management).
    /// </summary>
    /// <param name="owner">Calling plugin; the browser stops when it deactivates.</param>
    /// <param name="options">Options.</param>
    FrameworkElement CreateBrowser(IPluginContext owner, BrowserOptions options);
}

/// <summary>Browser options.</summary>
public sealed record BrowserOptions
{
    /// <summary>Kind id.</summary>
    public required string Kind { get; init; }
    /// <summary>Double-click / Enter.</summary>
    public required Func<BrowserOpenRequest, Task> OpenItem { get; init; }
    /// <summary>Thumbnail width / height.</summary>
    public double TileAspectRatio { get; init; } = 1.0;
    /// <summary>Thumbnail stretch.</summary>
    public Stretch ThumbnailStretch { get; init; } = Stretch.Uniform;
    /// <summary>Show the folder tree.</summary>
    public bool ShowFolderTree { get; init; } = true;
    /// <summary>Status filter and editor.</summary>
    public bool ShowStatus { get; init; }
    /// <summary>Non-null → toolbar "Add…" button.</summary>
    public Func<Task>? AddItem { get; init; }
    /// <summary>Offered sorts.</summary>
    public IReadOnlyList<ItemSort> Sorts { get; init; } = [ItemSort.Title, ItemSort.AddedAt, ItemSort.LastOpenedAt, ItemSort.Rating];
    /// <summary>Default sort.</summary>
    public ItemSort DefaultSort { get; init; } = ItemSort.Title;
    /// <summary>Second line of a tile.</summary>
    public Func<LibraryItem, string?>? Subtitle { get; init; }
    /// <summary>0..1 → bar on the tile.</summary>
    public Func<LibraryItem, double?>? ProgressFraction { get; init; }
    /// <summary>Extra context-menu entries.</summary>
    public IReadOnlyList<BrowserCommand> Commands { get; init; } = [];
    /// <summary>Kind-specific details block.</summary>
    public Func<LibraryItem, FrameworkElement?>? CreateDetails { get; init; }
}

/// <summary>An open request from the browser.</summary>
/// <param name="Item">The item.</param>
/// <param name="VisibleItems">Items in the grid's current order.</param>
/// <param name="Index">Index of <paramref name="Item"/> in <paramref name="VisibleItems"/>.</param>
public sealed record BrowserOpenRequest(LibraryItem Item, IReadOnlyList<LibraryItem> VisibleItems, int Index);

/// <summary>A custom context-menu command.</summary>
/// <param name="TitleKey">Localization key.</param>
/// <param name="Glyph">Segoe Fluent Icons glyph.</param>
/// <param name="Execute">Runs on the selection.</param>
/// <param name="CanExecute">Whether it applies to the selection.</param>
public sealed record BrowserCommand(
    string TitleKey,
    string? Glyph,
    Func<IReadOnlyList<LibraryItem>, Task> Execute,
    Func<IReadOnlyList<LibraryItem>, bool>? CanExecute = null);
