using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GenHTTP.Api.Protocol;
using Magpienest.Sdk.Imaging;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Thumbnails;
using Magpienest.Sdk.Viewing;

namespace Magpienest.Plugins.WebUi;

/// <summary>
/// The library endpoints of the web API. Files are found only by item id and page index; nothing here
/// accepts a path, and the only writes are library metadata. Runs on the server's threads; the services it calls are
/// thread-safe by contract.
/// </summary>
internal sealed class LibraryApi : IAsyncDisposable
{
    /// <summary>How long the full result of one <c>GET /api/items</c> query is reused for its other pages.</summary>
    public static readonly TimeSpan QueryCacheLifetime = TimeSpan.FromSeconds(30);

    /// <summary>Long edge of images converted to JPEG for the browser.</summary>
    public const int MaxConvertedSize = 4096;

    /// <summary>JPEG quality of converted images.</summary>
    public const int ConvertedQuality = 90;

    private const int MaxCachedQueries = 64;

    private static readonly Dictionary<string, string> NativeImages = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".avif"] = "image/avif",
    };

    private static readonly Dictionary<string, string> VideoTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/mp4",
        [".webm"] = "video/webm",
        [".ogv"] = "video/ogg",
        [".mkv"] = "video/x-matroska",
        [".mov"] = "video/quicktime",
        [".avi"] = "video/x-msvideo",
        [".wmv"] = "video/x-ms-wmv",
        [".ts"] = "video/mp2t",
        [".m2ts"] = "video/mp2t",
        [".flv"] = "video/x-flv",
    };

    private readonly ILibraryService _library;
    private readonly IThumbnailService _thumbnails;
    private readonly IImagingService _imaging;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, CachedQuery> _queries = new(StringComparer.Ordinal);
    private readonly BookCache _books;

    public LibraryApi(ILibraryService library, IThumbnailService thumbnails, IImagingService imaging, TimeProvider clock)
    {
        _library = library;
        _thumbnails = thumbnails;
        _imaging = imaging;
        _clock = clock;
        _books = new BookCache(clock);
    }

    /// <summary>Open books (tests).</summary>
    internal BookCache Books => _books;

    /// <summary>Forgets cached query results; called when the library changed.</summary>
    public void Invalidate() => _queries.Clear();

    public ValueTask DisposeAsync() => _books.DisposeAsync();

    /// <summary>Answers a library request, or returns null when the path is not one of these endpoints.</summary>
    /// <param name="request">The request.</param>
    /// <param name="path">Its path without the query.</param>
    public async Task<IResponse?> HandleAsync(IRequest request, string path)
    {
        var method = request.Header.Method;
        var reading = method.Equals(RequestMethod.Get) || method.Equals(RequestMethod.Head);
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries); // "api", "items", "12", …
        if (parts.Length < 2)
        {
            return null;
        }

        long id = 0;
        var hasId = parts.Length >= 3 && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out id);
        switch (parts[1])
        {
            case "items" when parts.Length == 2 && reading:
                return await ItemsAsync(request);
            case "items" when parts.Length == 3 && hasId && reading:
                return await ItemAsync(request, id);
            case "items" when parts.Length == 3 && hasId && method.Equals(RequestMethod.Patch):
                return await PatchAsync(request, id, progressOnly: false);
            case "items" when parts.Length == 4 && hasId && parts[3] == "progress" && method.Equals(RequestMethod.Post):
                return await PatchAsync(request, id, progressOnly: true);
            case "tags" when parts.Length == 2 && reading:
                return await PerKindAsync(request, async kind => (await _library.GetTagsAsync(kind)).Select(t => new { t.Name, t.Count }));
            case "dirs" when parts.Length == 2 && reading:
                return await PerKindAsync(request, async kind => await _library.GetDirectoriesAsync(kind));
            case "thumb" when parts.Length == 3 && hasId && reading:
                return await ThumbAsync(request, id);
            case "file" when parts.Length == 3 && hasId && reading:
                return await FileAsync(request, id);
            case "books" when parts.Length == 3 && hasId && reading:
                return await BookAsync(request, id);
            case "books" when parts.Length == 5 && hasId && parts[3] == "pages" && reading:
                return int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    ? await PageAsync(request, id, index)
                    : NotFound(request);
            case "books" when parts.Length == 4 && hasId && parts[3] == "frame" && reading:
                return await FrameAsync(request, id);
            default:
                return null;
        }
    }

    private async Task<IResponse> ItemsAsync(IRequest request)
    {
        var parsed = LibraryRequests.ParseItems(QueryOf(request), KindIds(), out var error);
        if (parsed is null)
        {
            return WebUiHandler.Error(request, ResponseStatus.BadRequest, error!);
        }

        var all = await QueryAsync(parsed);
        var page = all.Skip(parsed.Offset).Take(parsed.Limit).Select(ToDto).ToArray();
        return WebUiHandler.Json(request, ResponseStatus.Ok, new { total = all.Count, items = page });
    }

    private async Task<IReadOnlyList<LibraryItem>> QueryAsync(ItemsRequest parsed)
    {
        var now = _clock.GetUtcNow();
        if (_queries.TryGetValue(parsed.CacheKey, out var cached) && now - cached.At < QueryCacheLifetime)
        {
            try
            {
                return await cached.Items;
            }
            catch (Exception)
            {
                _queries.TryRemove(new KeyValuePair<string, CachedQuery>(parsed.CacheKey, cached));
                throw;
            }
        }

        if (_queries.Count >= MaxCachedQueries)
        {
            _queries.Clear();
        }

        var fresh = new CachedQuery(now, _library.QueryAsync(parsed.Query));
        _queries[parsed.CacheKey] = fresh;
        try
        {
            return await fresh.Items;
        }
        catch (Exception)
        {
            _queries.TryRemove(new KeyValuePair<string, CachedQuery>(parsed.CacheKey, fresh));
            throw;
        }
    }

    private async Task<IResponse> ItemAsync(IRequest request, long id)
    {
        if (await _library.GetAsync(id) is not { } item)
        {
            return NotFound(request);
        }

        var json = JsonSerializer.SerializeToNode(ToDto(item), WebJson.Options)!.AsObject();
        json["tags"] = new JsonArray([.. item.Tags.Select(t => (JsonNode)JsonValue.Create(t))]);
        json["notes"] = item.Notes;
        json["extra"] = ParseObject(item.Extra);
        return WebUiHandler.Json(request, ResponseStatus.Ok, json);
    }

    private async Task<IResponse> PatchAsync(IRequest request, long id, bool progressOnly)
    {
        var body = await WebUiHandler.ReadBodyAsync(request);
        if (body is null)
        {
            return WebUiHandler.Error(request, ResponseStatus.BadRequest, "Invalid body");
        }

        var patch = LibraryRequests.ParsePatch(body.Value, progressOnly, out var error);
        if (patch is null)
        {
            return WebUiHandler.Error(request, ResponseStatus.BadRequest, error!);
        }

        if (await _library.GetAsync(id) is not { } item)
        {
            return NotFound(request);
        }

        await _library.UpdateAsync(id, LibraryRequests.ToItemPatch(patch, item));
        var updated = await _library.GetAsync(id) ?? item;
        return WebUiHandler.Json(request, ResponseStatus.Ok, ToDto(updated));
    }

    private async Task<IResponse> PerKindAsync<T>(IRequest request, Func<string, Task<T>> read)
    {
        var kind = QueryOf(request).GetValueOrDefault("kind");
        if (kind is null || !KindIds().Contains(kind))
        {
            return WebUiHandler.Error(request, ResponseStatus.BadRequest, "Unknown kind");
        }

        return WebUiHandler.Json(request, ResponseStatus.Ok, (await read(kind))!);
    }

    private async Task<IResponse> ThumbAsync(IRequest request, long id)
    {
        if (await _library.GetAsync(id) is not { } item)
        {
            return NotFound(request);
        }

        var tag = "\"" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ThumbnailDefaults.DefaultStamp(item))))[..20] + "\"";
        const string caching = "private, max-age=86400";
        if (Header(request, "If-None-Match") == tag)
        {
            return WebUiHandler.Secured(request.Respond().Status(ResponseStatus.NotModified).Header("ETag", tag), caching)
                .Content(new BytesContent([])).Build();
        }

        if (await _thumbnails.GetJpegAsync(item) is not { } jpeg)
        {
            return NotFound(request);
        }

        return WebUiHandler.Secured(request.Respond().Status(ResponseStatus.Ok).Header("Content-Type", "image/jpeg").Header("ETag", tag),
                                    caching)
            .Content(new BytesContent(jpeg)).Build();
    }

    private async Task<IResponse> FileAsync(IRequest request, long id)
    {
        if (await _library.GetAsync(id) is not { } item || !File.Exists(item.Path))
        {
            return NotFound(request);
        }

        var extension = Path.GetExtension(item.Path);
        if (item.Kind == "video")
        {
            return Ranged(request, item.Path, VideoTypes.GetValueOrDefault(extension, "application/octet-stream"));
        }

        if (item.Kind != "illustration")
        {
            return NotFound(request); // Comics, games and other kinds are never sent as files.
        }

        if (NativeImages.TryGetValue(extension, out var type))
        {
            return Ranged(request, item.Path, type);
        }

        if (!CanConvert(extension))
        {
            return NotFound(request);
        }

        await using var stream = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                                                81920, useAsync: true);
        return await ConvertedAsync(request, stream);
    }

    private async Task<IResponse> BookAsync(IRequest request, long id)
    {
        var (item, lease) = await OpenBookAsync(id);
        if (lease is null)
        {
            return NotFound(request);
        }

        using (lease)
        {
            var pages = lease.Pages;
            var settings = BookSettingsOf(item);
            var position = pages.Count == 0 || item.Status == ItemStatus.Completed
                ? 0
                : (int)Math.Clamp(item.ProgressPosition, 0, pages.Count - 1);
            return WebUiHandler.Json(request, ResponseStatus.Ok, new
            {
                count = pages.Count,
                names = Enumerable.Range(0, pages.Count).Select(pages.GetName).ToArray(),
                direction = settings.Direction == ReadingDirection.RightToLeft ? "rtl" : "ltr",
                spread = settings.Spread == SpreadMode.Single ? "single" : "double",
                coverAlone = settings.CoverAlone,
                flow = settings.Flow == PageFlow.Strip ? "strip" : "paged",
                position,
            });
        }
    }

    private async Task<IResponse> PageAsync(IRequest request, long id, int index)
    {
        var (_, lease) = await OpenBookAsync(id);
        if (lease is null)
        {
            return NotFound(request);
        }

        using (lease)
        {
            if (index >= lease.Pages.Count)
            {
                return NotFound(request);
            }

            var extension = Path.GetExtension(lease.Pages.GetName(index));
            var native = NativeImages.TryGetValue(extension, out var type);
            if (!native && !CanConvert(extension))
            {
                return NotFound(request);
            }

            await using var stream = await lease.Pages.OpenAsync(index);
            if (!native)
            {
                return await ConvertedAsync(request, stream);
            }

            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            return WebUiHandler.Secured(request.Respond().Status(ResponseStatus.Ok).Header("Content-Type", type!), PageCaching)
                .Content(new BytesContent(copy.ToArray())).Build();
        }
    }

    private async Task<IResponse> FrameAsync(IRequest request, long id)
    {
        var (item, lease) = await OpenBookAsync(id);
        if (lease is null)
        {
            return NotFound(request);
        }

        using (lease)
        {
            var query = QueryOf(request);
            var settings = BookSettingsOf(item);
            var count = lease.Pages.Count;
            var forward = query.GetValueOrDefault("dir") switch
            {
                null or "" or "forward" => (bool?)true,
                "backward" => false,
                _ => null,
            };
            var spread = query.GetValueOrDefault("spread") switch
            {
                null or "" => settings.Spread,
                "single" => SpreadMode.Single,
                "double" => (SpreadMode?)SpreadMode.Double,
                _ => null,
            };
            var coverAlone = query.GetValueOrDefault("coverAlone") switch
            {
                null or "" => settings.CoverAlone,
                "true" => true,
                "false" => (bool?)false,
                _ => null,
            };
            if (!int.TryParse(query.GetValueOrDefault("at"), NumberStyles.None, CultureInfo.InvariantCulture, out var at) ||
                at >= count || forward is null || spread is null || coverAlone is null)
            {
                return WebUiHandler.Error(request, ResponseStatus.BadRequest, "Invalid query value");
            }

            // FrameLayout looks at the page itself and its neighbours only; read their shapes first.
            for (var i = Math.Max(0, at - 1); i <= Math.Min(count - 1, at + 1); i++)
            {
                await IsWideAsync(lease, i);
            }

            bool IsWide(int index) => lease.Wide.TryGetValue(index, out var wide) && wide;
            var frame = forward.Value
                ? FrameLayout.Forward(at, count, spread.Value, coverAlone.Value, IsWide)
                : FrameLayout.Backward(at, count, spread.Value, coverAlone.Value, IsWide);
            return WebUiHandler.Json(request, ResponseStatus.Ok, new { first = frame.First, last = frame.Last });
        }
    }

    private async Task IsWideAsync(BookCache.BookLease lease, int index)
    {
        if (lease.Wide.ContainsKey(index))
        {
            return;
        }

        bool wide;
        try
        {
            await using var stream = await lease.Pages.OpenAsync(index);
            wide = await _imaging.ReadInfoAsync(stream) is { } info && info.PixelWidth > info.PixelHeight;
        }
        catch (Exception ex) when (ex is IOException or ImageDecodeException or InvalidDataException or UnauthorizedAccessException)
        {
            wide = false; // Unknown counts as not wide, as on the desktop.
        }

        lease.Wide[index] = wide;
    }

    private async Task<(LibraryItem Item, BookCache.BookLease? Lease)> OpenBookAsync(long id)
    {
        if (await _library.GetAsync(id) is not { } item ||
            _library.Kinds.FirstOrDefault(k => k.Id == item.Kind)?.OpenPages is not { } open)
        {
            return (null!, null);
        }

        try
        {
            return (item, await _books.OpenAsync(item, open, CancellationToken.None));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return (item, null); // The book is gone or unreadable.
        }
    }

    private const string PageCaching = "private, max-age=600";

    private async Task<IResponse> ConvertedAsync(IRequest request, Stream stream)
    {
        byte[] jpeg;
        try
        {
            var seekable = stream;
            if (!stream.CanSeek)
            {
                seekable = new MemoryStream();
                await stream.CopyToAsync(seekable);
                seekable.Position = 0;
            }

            var bitmap = await _imaging.DecodeAsync(seekable, new DecodeOptions(MaxConvertedSize, MaxConvertedSize));
            jpeg = await Task.Run(() => _imaging.EncodeJpeg(bitmap, ConvertedQuality));
        }
        catch (ImageDecodeException)
        {
            return NotFound(request);
        }

        return WebUiHandler.Secured(request.Respond().Status(ResponseStatus.Ok).Header("Content-Type", "image/jpeg"), PageCaching)
            .Content(new BytesContent(jpeg)).Build();
    }

    private static IResponse Ranged(IRequest request, string path, string type)
    {
        var length = new FileInfo(path).Length;
        if (!LibraryRequests.TryParseRange(Header(request, "Range"), length, out var range))
        {
            return WebUiHandler.Secured(request.Respond().Status(ResponseStatus.RequestedRangeNotSatisfiable)
                    .Header("Content-Range", $"bytes */{length}")
                    .Header("Content-Type", "application/json; charset=utf-8"))
                .Content(new BytesContent("{\"error\":\"Range not satisfiable\"}"u8.ToArray())).Build();
        }

        var builder = request.Respond().Header("Content-Type", type).Header("Accept-Ranges", "bytes");
        if (range is { } r)
        {
            builder = builder.Status(ResponseStatus.PartialContent)
                .Header("Content-Range", $"bytes {r.Start}-{r.Start + r.Length - 1}/{length}");
            return WebUiHandler.Secured(builder).Content(new FileContent(path, r.Start, r.Length)).Build();
        }

        return WebUiHandler.Secured(builder.Status(ResponseStatus.Ok)).Content(new FileContent(path, 0, length)).Build();
    }

    private bool CanConvert(string extension) =>
        !extension.Equals(".svg", StringComparison.OrdinalIgnoreCase) && extension.Length > 1 && _imaging.IsSupported(extension);

    private HashSet<string> KindIds() => [.. _library.Kinds.Select(k => k.Id)];

    private static BookSettings BookSettingsOf(LibraryItem item)
    {
        var extra = item.GetExtra<BookExtra>();
        return new BookSettings(
            extra.Direction == "ltr" ? ReadingDirection.LeftToRight : ReadingDirection.RightToLeft,
            extra.Spread == "single" ? SpreadMode.Single : SpreadMode.Double,
            extra.CoverAlone ?? true,
            extra.Flow == "strip" ? PageFlow.Strip : PageFlow.Paged);
    }

    /// <summary>The API form of an item (no full paths).</summary>
    /// <param name="item">The item.</param>
    internal static ItemDto ToDto(LibraryItem item) => new(
        item.Id, item.Kind, item.Title, item.Directory, item.FileSize, item.FileModified, item.AddedAt, item.LastOpenedAt,
        item.Rating, item.IsFavorite, LibraryRequests.StatusName(item.Status), item.ProgressPosition, item.ProgressTotal,
        item.TimeSpentSeconds, item.IsMissing);

    private static JsonObject ParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IResponse NotFound(IRequest request) => WebUiHandler.Error(request, ResponseStatus.NotFound, "Not found");

    private static string? Header(IRequest request, string name) => request.Header.Headers.GetEntry(name)?.ToString();

    /// <summary>The decoded query-string parameters (the first value of a repeated key).</summary>
    internal static Dictionary<string, string> QueryOf(IRequest request)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var query = request.Header.Query;
        for (var i = 0; i < query.Count; i++)
        {
            var entry = query.GetStringEntry(i);
            result.TryAdd(Decode(entry.Key.ToString()), Decode(entry.Value.ToString()));
        }

        return result;
    }

    /// <summary>Undoes the form encoding of a query-string part (<c>+</c> is a space, <c>%XX</c> a UTF-8 byte).</summary>
    private static string Decode(string raw) => Uri.UnescapeDataString(raw.Replace('+', ' '));

    private sealed record CachedQuery(DateTimeOffset At, Task<IReadOnlyList<LibraryItem>> Items);

    /// <summary>Reading settings in a book's <c>extra</c> JSON.</summary>
    private sealed class BookExtra
    {
        public string? Direction { get; set; }
        public string? Spread { get; set; }
        public bool? CoverAlone { get; set; }
        public string? Flow { get; set; }
    }
}

/// <summary>An item as the web API shows it.</summary>
internal sealed record ItemDto(long Id, string Kind, string Title, string Directory, long FileSize, DateTimeOffset FileModified,
                               DateTimeOffset AddedAt, DateTimeOffset? LastOpenedAt, int Rating, bool Favorite, string Status,
                               long ProgressPosition, long ProgressTotal, long TimeSpentSeconds, bool Missing);
