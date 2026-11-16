using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Magpienest.Sdk.Library;

namespace Magpienest.Plugins.WebUi;

/// <summary><c>GET /api/items</c> after parsing: the library query, the page wanted and the key of the cached result.</summary>
/// <param name="Query">The library query (without a limit: the whole result is cached).</param>
/// <param name="Offset">First item of the page.</param>
/// <param name="Limit">Items in the page.</param>
/// <param name="CacheKey">Equal for requests that differ only in the page.</param>
internal sealed record ItemsRequest(ItemQuery Query, int Offset, int Limit, string CacheKey);

/// <summary>A byte range of a file: <paramref name="Start"/> and <paramref name="Length"/> in bytes.</summary>
/// <param name="Start">First byte.</param>
/// <param name="Length">Number of bytes.</param>
internal readonly record struct ByteRange(long Start, long Length);

/// <summary>A change the web page asks for (<c>PATCH /api/items/{id}</c> or the progress beacon).</summary>
internal sealed record WebPatch
{
    public bool? Favorite { get; init; }
    public int? Rating { get; init; }
    public ItemStatus? Status { get; init; }
    public long? ProgressPosition { get; init; }
    public long? ProgressTotal { get; init; }
    public long? AddTimeSpentSeconds { get; init; }
    public bool MarkOpened { get; init; }

    /// <summary>The last page was shown, or the video played to its end.</summary>
    public bool ReachedEnd { get; init; }

    /// <summary>Keys merged into the item's <c>extra</c> JSON.</summary>
    public JsonObject? Extra { get; init; }

    /// <summary>Whether this reports reading or watching progress.</summary>
    public bool HasProgress => ProgressPosition is not null || ProgressTotal is not null || AddTimeSpentSeconds is not null ||
                               MarkOpened || ReachedEnd;
}

/// <summary>Parses and checks what the web page sends to the library endpoints. Pure functions, no I/O.</summary>
internal static class LibraryRequests
{
    /// <summary>Largest page of <c>GET /api/items</c>.</summary>
    public const int MaxLimit = 500;

    /// <summary>Page size when none is given.</summary>
    public const int DefaultLimit = 200;

    /// <summary>Most seconds one report may add to the time spent (one day).</summary>
    public const long MaxAddedSeconds = 24 * 60 * 60;

    private static readonly HashSet<string> PatchKeys = new(StringComparer.Ordinal)
    {
        "favorite", "rating", "status", "progressPosition", "progressTotal", "addTimeSpentSeconds", "markOpened", "reachedEnd", "extra",
    };

    private static readonly HashSet<string> ProgressKeys = new(StringComparer.Ordinal)
    {
        "progressPosition", "progressTotal", "addTimeSpentSeconds", "markOpened", "reachedEnd",
    };

    /// <summary>Builds the query of <c>GET /api/items</c>; an unknown or invalid value gives an error message.</summary>
    /// <param name="query">Decoded query-string parameters.</param>
    /// <param name="kinds">Registered kind ids.</param>
    /// <param name="error">Why the request was refused.</param>
    public static ItemsRequest? ParseItems(IReadOnlyDictionary<string, string> query, IReadOnlyCollection<string> kinds, out string? error)
    {
        error = null;
        var kind = Value(query, "kind");
        if (kind is null || !kinds.Contains(kind))
        {
            error = "Unknown kind";
            return null;
        }

        if (!TryBool(query, "favorites", out var favorites) || !TryBool(query, "desc", out var descending) ||
            !TryInt(query, "minRating", 0, 5, 0, out var minRating) ||
            !TryInt(query, "offset", 0, int.MaxValue, 0, out var offset) ||
            !TryInt(query, "limit", 1, MaxLimit, DefaultLimit, out var limit) ||
            !TryEnum(query, "status", out ItemStatus? status) || !TryEnum(query, "sort", out ItemSort? sort))
        {
            error = "Invalid query value";
            return null;
        }

        var text = Value(query, "text");
        var directory = Value(query, "dir");
        string[] tags = [.. (Value(query, "tags") ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                                                         .Distinct(StringComparer.OrdinalIgnoreCase)];
        var items = new ItemQuery
        {
            Kind = kind,
            Text = text,
            Directory = directory,
            Tags = tags,
            FavoritesOnly = favorites,
            MinRating = minRating,
            Status = status,
            Sort = sort ?? ItemSort.Title,
            Descending = descending,
        };

        var key = string.Join('\u001f', kind, text, string.Join('\u001e', tags), directory, favorites, minRating, status, items.Sort,
                              descending);
        return new ItemsRequest(items, offset, limit, key);
    }

    /// <summary>
    /// Reads a <c>Range</c> header against a file of <paramref name="length"/> bytes. Returns false when it cannot be
    /// satisfied (answer 416); true with null when the whole file is sent (no header, or several ranges).
    /// </summary>
    /// <param name="header">The header value.</param>
    /// <param name="length">File length.</param>
    /// <param name="range">The range to send, or null for the whole file.</param>
    public static bool TryParseRange(string? header, long length, out ByteRange? range)
    {
        range = null;
        if (string.IsNullOrWhiteSpace(header))
        {
            return true;
        }

        const string unit = "bytes=";
        if (!header.StartsWith(unit, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var spec = header[unit.Length..].Trim();
        if (spec.Contains(','))
        {
            return true; // Several ranges: the whole file is a valid answer (RFC 9110 §14.2).
        }

        var dash = spec.IndexOf('-');
        if (dash < 0 || length <= 0)
        {
            return false;
        }

        var first = spec[..dash].Trim();
        var last = spec[(dash + 1)..].Trim();
        if (first.Length == 0)
        {
            // Suffix: the last N bytes.
            if (!TryLong(last, out var suffix) || suffix <= 0)
            {
                return false;
            }

            var count = Math.Min(suffix, length);
            range = new ByteRange(length - count, count);
            return true;
        }

        if (!TryLong(first, out var start) || start >= length)
        {
            return false;
        }

        var end = length - 1;
        if (last.Length > 0)
        {
            if (!TryLong(last, out end) || end < start)
            {
                return false;
            }

            end = Math.Min(end, length - 1);
        }

        range = new ByteRange(start, end - start + 1);
        return true;
    }

    /// <summary>Reads a patch body; <paramref name="progressOnly"/> limits it to the progress fields (the beacon).</summary>
    /// <param name="body">The JSON body.</param>
    /// <param name="progressOnly">Whether only progress fields are allowed.</param>
    /// <param name="error">Why it was refused.</param>
    public static WebPatch? ParsePatch(ReadOnlyMemory<byte> body, bool progressOnly, out string? error)
    {
        error = "Invalid body";
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(body.Span);
        }
        catch (JsonException)
        {
            return null;
        }

        if (node is not JsonObject root)
        {
            return null;
        }

        var allowed = progressOnly ? ProgressKeys : PatchKeys;
        var patch = new WebPatch();
        foreach (var (key, value) in root)
        {
            if (!allowed.Contains(key))
            {
                error = "Unknown field: " + key;
                return null;
            }

            if (value is null)
            {
                continue;
            }

            try
            {
                patch = key switch
                {
                    "favorite" => patch with { Favorite = value.GetValue<bool>() },
                    "rating" => patch with { Rating = Checked(value.GetValue<int>(), 0, 5) },
                    "status" => patch with { Status = ParseStatus(value.GetValue<string>()) },
                    "progressPosition" => patch with { ProgressPosition = Checked(value.GetValue<long>(), 0, long.MaxValue) },
                    "progressTotal" => patch with { ProgressTotal = Checked(value.GetValue<long>(), 0, long.MaxValue) },
                    "addTimeSpentSeconds" => patch with { AddTimeSpentSeconds = Checked(value.GetValue<long>(), 0, MaxAddedSeconds) },
                    "markOpened" => patch with { MarkOpened = value.GetValue<bool>() },
                    "reachedEnd" => patch with { ReachedEnd = value.GetValue<bool>() },
                    _ => patch with { Extra = value as JsonObject ?? throw new FormatException() },
                };
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException or ArgumentException)
            {
                error = "Invalid value: " + key;
                return null;
            }
        }

        error = null;
        return patch;
    }

    /// <summary>
    /// The library patch for a web patch: the automatic status (StatusRules) applies to progress unless the page set a
    /// status itself, and <c>extra</c> is merged key by key into the stored JSON.
    /// </summary>
    /// <param name="patch">What the page sent.</param>
    /// <param name="item">The stored item.</param>
    public static ItemPatch ToItemPatch(WebPatch patch, LibraryItem item)
    {
        var status = patch.Status;
        if (status is null && patch.HasProgress)
        {
            var reachedEnd = StatusRules.ReachedEnd(item.Kind, patch.ReachedEnd, patch.ProgressPosition,
                                                    patch.ProgressTotal ?? item.ProgressTotal);
            status = StatusRules.AfterProgress(item.Kind, item.Status, reachedEnd);
        }

        return new ItemPatch
        {
            IsFavorite = patch.Favorite,
            Rating = patch.Rating,
            Status = status,
            ProgressPosition = patch.ProgressPosition,
            ProgressTotal = patch.ProgressTotal,
            AddTimeSpentSeconds = patch.AddTimeSpentSeconds is > 0 ? patch.AddTimeSpentSeconds : null,
            MarkOpened = patch.MarkOpened,
            Extra = patch.Extra is { } extra ? MergeExtra(item.Extra, extra) : null,
        };
    }

    /// <summary>Copies every key of <paramref name="changes"/> into the stored JSON; keys it does not name are kept.</summary>
    /// <param name="stored">The stored <c>extra</c> JSON.</param>
    /// <param name="changes">The keys to set.</param>
    public static string MergeExtra(string stored, JsonObject changes)
    {
        JsonObject target;
        try
        {
            target = JsonNode.Parse(string.IsNullOrWhiteSpace(stored) ? "{}" : stored) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            target = [];
        }

        foreach (var (key, value) in changes)
        {
            target[key] = value?.DeepClone();
        }

        return target.ToJsonString();
    }

    /// <summary>The API name of a status.</summary>
    /// <param name="status">The status.</param>
    public static string StatusName(ItemStatus status) => JsonNamingPolicy.CamelCase.ConvertName(status.ToString());

    private static ItemStatus ParseStatus(string value)
    {
        foreach (var status in Enum.GetValues<ItemStatus>())
        {
            if (StatusName(status).Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return status;
            }
        }

        throw new FormatException();
    }

    private static T Checked<T>(T value, T min, T max) where T : IComparable<T> =>
        value.CompareTo(min) < 0 || value.CompareTo(max) > 0 ? throw new FormatException() : value;

    private static string? Value(IReadOnlyDictionary<string, string> query, string key) =>
        query.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static bool TryBool(IReadOnlyDictionary<string, string> query, string key, out bool result)
    {
        result = false;
        switch (Value(query, key)?.ToLowerInvariant())
        {
            case null or "false" or "0":
                return true;
            case "true" or "1":
                result = true;
                return true;
            default:
                return false;
        }
    }

    private static bool TryInt(IReadOnlyDictionary<string, string> query, string key, int min, int max, int fallback, out int result)
    {
        result = fallback;
        var text = Value(query, key);
        return text is null ||
               (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result >= min && result <= max);
    }

    private static bool TryEnum<T>(IReadOnlyDictionary<string, string> query, string key, out T? result) where T : struct, Enum
    {
        result = null;
        var text = Value(query, key);
        if (text is null)
        {
            return true;
        }

        foreach (var value in Enum.GetValues<T>())
        {
            if (JsonNamingPolicy.CamelCase.ConvertName(value.ToString()).Equals(text, StringComparison.OrdinalIgnoreCase))
            {
                result = value;
                return true;
            }
        }

        return false;
    }

    private static bool TryLong(string text, out long value) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
