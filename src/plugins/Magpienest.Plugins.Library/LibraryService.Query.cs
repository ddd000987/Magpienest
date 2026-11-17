using Magpienest.Sdk.Library;
using Microsoft.Data.Sqlite;

namespace Magpienest.Plugins.Library;

internal sealed partial class LibraryService
{
    private const int ParallelSortThreshold = 5000;

    /// <inheritdoc />
    public async Task<IReadOnlyList<LibraryItem>> QueryAsync(ItemQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await _ready.ConfigureAwait(false);
        return await _db.ReadAsync<IReadOnlyList<LibraryItem>>(connection => QueryCore(connection, query, ct), ct).ConfigureAwait(false);
    }

    private List<LibraryItem> QueryCore(SqliteConnection connection, ItemQuery query, CancellationToken ct)
    {
        var naturalSort = query.Sort is ItemSort.Title or ItemSort.Path;
        using var command = connection.CreateCommand();
        var folders = new FolderFilter(connection, command, query.Kind, LoadFolderLabels(connection, query.Kind));
        // With rated folders an item's rating may come from its folder: that order is also made here, not in SQLite.
        var ratingSort = query.Sort == ItemSort.Rating && folders.HasRatings;
        var sortHere = naturalSort || ratingSort;
        var where = BuildWhere(command, query, folders);
        var sql = $"SELECT {ItemColumns} FROM item WHERE {where}";
        if (!sortHere)
        {
            sql += " ORDER BY " + SortExpression(query.Sort) + (query.Descending ? " DESC" : " ASC") + ", id ASC";
            if (query.Limit is { } limit)
            {
                sql += " LIMIT $limit";
                command.Parameters.AddWithValue("$limit", Math.Max(0, limit));
            }
        }

        command.CommandText = sql;
        var items = new List<LibraryItem>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if ((items.Count & 1023) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                items.Add(ReadItem(reader, query.Kind));
            }
        }

        if (sortHere)
        {
            // Natural order is not expressible in SQLite: sort here, then apply the limit.
            items = ratingSort
                ? SortByRating(ApplyFolderLabels(items, folders.Folders), query.Descending)
                : SortNaturally(items, query.Sort == ItemSort.Title, query.Descending);
            if (query.Limit is { } limit && limit < items.Count)
            {
                items = items.GetRange(0, Math.Max(0, limit));
            }
        }

        ct.ThrowIfCancellationRequested();
        return items.Count == 0 ? items : ApplyFolderLabels(AttachTags(connection, items), folders.Folders);
    }

    /// <summary>Natural comparison is the expensive part of a big query, so large lists are sorted on all cores.</summary>
    private static List<LibraryItem> SortNaturally(List<LibraryItem> items, bool byTitle, bool descending)
    {
        Func<LibraryItem, string> key = byTitle ? i => i.Title : i => i.Path;
        if (items.Count >= ParallelSortThreshold)
        {
            var parallel = items.AsParallel();
            return (descending
                ? parallel.OrderByDescending(key, LibraryRules.Natural).ThenBy(i => i.Id)
                : parallel.OrderBy(key, LibraryRules.Natural).ThenBy(i => i.Id)).ToList();
        }

        return (descending
            ? items.OrderByDescending(key, LibraryRules.Natural).ThenBy(i => i.Id)
            : items.OrderBy(key, LibraryRules.Natural).ThenBy(i => i.Id)).ToList();
    }

    /// <summary>By the rating an item counts with (its own, else its folder's), then by id as the SQL sorts do.</summary>
    private static List<LibraryItem> SortByRating(List<LibraryItem> items, bool descending)
    {
        static int Effective(LibraryItem item) => item.Rating > 0 ? item.Rating : item.FolderRating;
        return (descending
            ? items.OrderByDescending(Effective).ThenBy(i => i.Id)
            : items.OrderBy(Effective).ThenBy(i => i.Id)).ToList();
    }

    private static string SortExpression(ItemSort sort) => sort switch
    {
        ItemSort.AddedAt => "added_at",
        ItemSort.LastOpenedAt => "last_opened_at",
        ItemSort.Rating => "rating",
        ItemSort.FileModified => "file_mtime",
        ItemSort.FileSize => "file_size",
        ItemSort.TimeSpent => "time_spent_s",
        ItemSort.Progress => "CASE WHEN progress_total = 0 THEN 0.0 ELSE CAST(progress_pos AS REAL) / progress_total END",
        _ => throw new ArgumentOutOfRangeException(nameof(sort), sort, "Not an SQL sort."),
    };

    /// <summary>Builds the WHERE clause and adds its parameters to <paramref name="command"/>.</summary>
    private static string BuildWhere(SqliteCommand command, ItemQuery query, FolderFilter folders)
    {
        var clauses = new List<string> { "kind = $kind" };
        command.Parameters.AddWithValue("$kind", query.Kind);

        if (!query.IncludeMissing)
        {
            clauses.Add("missing = 0");
        }

        if (!string.IsNullOrEmpty(query.Text))
        {
            var text = query.Text;
            var inFolder = folders.UnderAny(folders.Folders
                .Where(f => f.Tags.Any(t => t.Contains(text, StringComparison.OrdinalIgnoreCase)))
                .Select(f => f.Path));
            clauses.Add(
                "(title LIKE $text ESCAPE '\\' OR EXISTS (SELECT 1 FROM item_tag it JOIN tag t ON t.id = it.tag_id " +
                "WHERE it.item_id = item.id AND t.name LIKE $text ESCAPE '\\')" + (inFolder is null ? "" : " OR " + inFolder) + ")");
            command.Parameters.AddWithValue("$text", "%" + LibraryRules.EscapeLike(text) + "%");
        }

        if (!string.IsNullOrEmpty(query.Directory))
        {
            var directory = LibraryRules.NormalizePath(query.Directory);
            if (query.IncludeSubdirectories)
            {
                // Range trick instead of LIKE: '\' + 1 is ']'. A drive root "C:\" gives lo = "C:\", hi = "C:]".
                var stem = directory.TrimEnd(Path.DirectorySeparatorChar);
                clauses.Add("(dir = $dir OR (dir >= $lo AND dir < $hi))");
                command.Parameters.AddWithValue("$dir", directory);
                command.Parameters.AddWithValue("$lo", stem + Path.DirectorySeparatorChar);
                command.Parameters.AddWithValue("$hi", stem + ']');
            }
            else
            {
                clauses.Add("dir = $dir");
                command.Parameters.AddWithValue("$dir", directory);
            }
        }

        if (query.SourceId is { } sourceId)
        {
            clauses.Add("source_id = $source");
            command.Parameters.AddWithValue("$source", sourceId);
        }

        var tagIndex = 0;
        foreach (var tag in LibraryRules.NormalizeTags(query.Tags))
        {
            var name = "$tag" + tagIndex++;
            var inFolder = folders.UnderAny(folders.Folders
                .Where(f => f.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                .Select(f => f.Path));
            clauses.Add(
                "(EXISTS (SELECT 1 FROM item_tag it JOIN tag t ON t.id = it.tag_id " +
                $"WHERE it.item_id = item.id AND t.name = {name})" + (inFolder is null ? "" : " OR " + inFolder) + ")");
            command.Parameters.AddWithValue(name, tag);
        }

        if (query.FavoritesOnly)
        {
            clauses.Add("favorite = 1");
        }

        if (query.MinRating > 0)
        {
            clauses.Add(folders.MinRating(query.MinRating));
            command.Parameters.AddWithValue("$minRating", query.MinRating);
        }

        if (query.Status is { } status)
        {
            clauses.Add("status = $status");
            command.Parameters.AddWithValue("$status", (int)status);
        }

        return string.Join(" AND ", clauses);
    }
}
