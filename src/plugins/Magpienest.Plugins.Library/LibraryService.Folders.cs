using Magpienest.Sdk.Library;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Magpienest.Plugins.Library;

internal sealed partial class LibraryService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<FolderLabels>> GetFolderLabelsAsync(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        await _ready.ConfigureAwait(false);
        return await _db.ReadAsync<IReadOnlyList<FolderLabels>>(connection => LoadFolderLabels(connection, kind)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetFolderLabelsAsync(string kind, string path, int rating, IReadOnlyCollection<string> tags)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(tags);
        if (rating is < 0 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(rating), "Rating must be between 0 and 5.");
        }

        var folder = LibraryRules.NormalizePath(path);
        var normalized = LibraryRules.NormalizeTags(tags);
        await _ready.ConfigureAwait(false);
        await _db.WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            if (rating == 0 && normalized.Count == 0)
            {
                connection.Command("DELETE FROM folder WHERE kind = $kind AND path = $path", transaction)
                    .With("$kind", kind).With("$path", folder).Execute();
            }
            else
            {
                // The stored path keeps the casing it had when the folder was first labelled.
                var id = Convert.ToInt64(connection.Command(
                        "INSERT INTO folder (kind, path, rating) VALUES ($kind, $path, $rating) " +
                        "ON CONFLICT (kind, path) DO UPDATE SET rating = excluded.rating RETURNING id", transaction)
                    .With("$kind", kind).With("$path", folder).With("$rating", rating).Scalar());
                connection.Command("DELETE FROM folder_tag WHERE folder_id = $id", transaction).With("$id", id).Execute();
                foreach (var tag in normalized)
                {
                    connection.Command("INSERT INTO tag (name) VALUES ($name) ON CONFLICT (name) DO NOTHING", transaction).With("$name", tag).Execute();
                    var tagId = Convert.ToInt64(connection.Command("SELECT id FROM tag WHERE name = $name", transaction).With("$name", tag).Scalar());
                    connection.Command("INSERT OR IGNORE INTO folder_tag (folder_id, tag_id) VALUES ($folder, $tag)", transaction)
                        .With("$folder", id).With("$tag", tagId).Execute();
                }
            }

            DeleteOrphanTags(connection, transaction);
            transaction.Commit();
            return true;
        }).ConfigureAwait(false);

        // Which items match a tag or rating filter can change anywhere below the folder.
        _emit(new LibraryChanged(kind, LibraryChangeType.Reset, []));
    }

    /// <summary>The labelled folders of a kind, tags in name order.</summary>
    private static List<FolderLabels> LoadFolderLabels(SqliteConnection connection, string kind)
    {
        var folders = new List<(long Id, string Path, int Rating)>();
        using (var command = connection.Command("SELECT id, path, rating FROM folder WHERE kind = $kind").With("$kind", kind))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                folders.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2)));
            }
        }

        if (folders.Count == 0)
        {
            return [];
        }

        var tags = new Dictionary<long, List<string>>();
        using (var command = connection.Command(
                   "SELECT ft.folder_id, t.name FROM folder_tag ft JOIN tag t ON t.id = ft.tag_id JOIN folder f ON f.id = ft.folder_id " +
                   "WHERE f.kind = $kind ORDER BY t.name").With("$kind", kind))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                if (!tags.TryGetValue(id, out var list))
                {
                    tags[id] = list = [];
                }

                list.Add(reader.GetString(1));
            }
        }

        return [.. folders
            .Select(f => new FolderLabels(f.Path, f.Rating, tags.TryGetValue(f.Id, out var list) ? list : []))
            .OrderBy(f => f.Path, LibraryRules.Natural)];
    }

    /// <summary>
    /// Replaces the counts of tags that folders carry: every item below such a folder counts once, plus the items
    /// elsewhere that carry the tag themselves. Counted per directory, so it costs one pass over the folders, not the items.
    /// </summary>
    private static void MergeFolderTagCounts(SqliteConnection connection, string kind, IReadOnlyList<FolderLabels> folders, List<TagCount> result)
    {
        var byPath = new Dictionary<string, FolderLabels>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            byPath[folder.Path] = folder;
        }

        var inherited = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> TagsOf(string directory)
        {
            if (!inherited.TryGetValue(directory, out var tags))
            {
                inherited[directory] = tags = Inherit(directory, byPath).Tags;
            }

            return tags;
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in folders.SelectMany(f => f.Tags))
        {
            counts.TryAdd(tag, 0);
        }

        using (var command = connection.Command("SELECT dir, COUNT(*) FROM item WHERE kind = $kind GROUP BY dir").With("$kind", kind))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var count = reader.GetInt32(1);
                foreach (var tag in TagsOf(reader.GetString(0)))
                {
                    counts[tag] += count;
                }
            }
        }

        using (var command = connection.Command(
                   "SELECT t.name, i.dir FROM item_tag it JOIN tag t ON t.id = it.tag_id JOIN item i ON i.id = it.item_id " +
                   "WHERE i.kind = $kind AND EXISTS (SELECT 1 FROM folder_tag ft WHERE ft.tag_id = t.id)").With("$kind", kind))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var tag = reader.GetString(0);
                if (counts.ContainsKey(tag) && !TagsOf(reader.GetString(1)).Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    counts[tag]++;
                }
            }
        }

        result.RemoveAll(t => counts.ContainsKey(t.Name));
        result.AddRange(counts.Where(c => c.Value > 0).Select(c => new TagCount(c.Key, c.Value)));
        result.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
    }

    /// <summary>Fills <see cref="LibraryItem.FolderTags"/> and <see cref="LibraryItem.FolderRating"/> from the labelled folders above each item.</summary>
    private static List<LibraryItem> ApplyFolderLabels(List<LibraryItem> items, IReadOnlyList<FolderLabels> folders)
    {
        if (folders.Count == 0 || items.Count == 0)
        {
            return items;
        }

        var byPath = new Dictionary<string, FolderLabels>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            byPath[folder.Path] = folder;
        }

        var byDirectory = new Dictionary<string, (IReadOnlyList<string> Tags, int Rating)>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!byDirectory.TryGetValue(item.Directory, out var inherited))
            {
                byDirectory[item.Directory] = inherited = Inherit(item.Directory, byPath);
            }

            if (inherited.Tags.Count > 0 || inherited.Rating > 0)
            {
                items[i] = item with { FolderTags = inherited.Tags, FolderRating = inherited.Rating };
            }
        }

        return items;
    }

    /// <summary>Walks up from <paramref name="directory"/>: every labelled folder adds its tags, the nearest rated one gives the rating.</summary>
    private static (IReadOnlyList<string> Tags, int Rating) Inherit(string directory, Dictionary<string, FolderLabels> byPath)
    {
        List<string>? tags = null;
        var rating = 0;
        for (var current = directory; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (!byPath.TryGetValue(current, out var folder))
            {
                continue;
            }

            if (rating == 0)
            {
                rating = folder.Rating;
            }

            foreach (var tag in folder.Tags)
            {
                tags ??= [];
                if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    tags.Add(tag);
                }
            }
        }

        tags?.Sort(StringComparer.OrdinalIgnoreCase);
        return (tags ?? (IReadOnlyList<string>)[], rating);
    }

    /// <summary>
    /// Writes the SQL that brings folder labels into a query: "this item is in folder X or below it", and the
    /// rating an item counts with. Folder parameters are shared, so a folder used twice is bound once.
    /// </summary>
    private sealed class FolderFilter(SqliteConnection connection, SqliteCommand command, string kind, IReadOnlyList<FolderLabels> folders)
    {
        private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<FolderLabels> Folders => folders;

        /// <summary>Whether some folder has a rating, so an item's rating is not always its own.</summary>
        public bool HasRatings => folders.Any(f => f.Rating > 0);

        /// <summary>True when the item row's <c>dir</c> is <paramref name="path"/> or lies below it.</summary>
        public string Under(string path)
        {
            if (!_names.TryGetValue(path, out var name))
            {
                name = "$f" + _names.Count;
                _names[path] = name;
                // Range trick as in the folder filter: '\' + 1 is ']'.
                var stem = path.TrimEnd(Path.DirectorySeparatorChar);
                command.Parameters.AddWithValue(name, path);
                command.Parameters.AddWithValue(name + "lo", stem + Path.DirectorySeparatorChar);
                command.Parameters.AddWithValue(name + "hi", stem + ']');
            }

            return $"(dir = {name} OR (dir >= {name}lo AND dir < {name}hi))";
        }

        /// <summary>True when any of <paramref name="paths"/> holds the item; null when there are none.</summary>
        public string? UnderAny(IEnumerable<string> paths)
        {
            var clauses = paths.Select(Under).ToList();
            return clauses.Count == 0 ? null : string.Join(" OR ", clauses);
        }

        /// <summary>
        /// The item's own rating, or while it is 0 the rating of the nearest rated folder above it, is at least
        /// <c>$minRating</c>. The folder side is worked out per directory here, so SQLite only looks the directory up.
        /// </summary>
        public string MinRating(int min)
        {
            if (!HasRatings)
            {
                return "rating >= $minRating";
            }

            var byPath = new Dictionary<string, FolderLabels>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in folders)
            {
                byPath[folder.Path] = folder;
            }

            var qualifying = new List<string>();
            using (var directories = connection.Command("SELECT DISTINCT dir FROM item WHERE kind = $kind").With("$kind", kind))
            using (var reader = directories.ExecuteReader())
            {
                while (reader.Read())
                {
                    var directory = reader.GetString(0);
                    if (Inherit(directory, byPath).Rating >= min)
                    {
                        qualifying.Add(directory);
                    }
                }
            }

            command.Parameters.AddWithValue("$ratedDirs", JsonSerializer.Serialize(qualifying));
            return "(rating >= $minRating OR (rating = 0 AND dir IN (SELECT value FROM json_each($ratedDirs))))";
        }
    }
}
