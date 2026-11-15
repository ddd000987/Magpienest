using Magpienest.Sdk;
using Magpienest.Sdk.Library;
using Microsoft.Data.Sqlite;

namespace Magpienest.Plugins.Library;

/// <summary>
/// <see cref="ILibraryService"/> over <see cref="LibraryDb"/>. Independent of the kernel: events and
/// notifications leave through the two callbacks, so it can be tested with a temp database.
/// </summary>
internal sealed partial class LibraryService : ILibraryService, IDisposable
{
    private const string ItemColumns =
        "id, kind, source_id, path, dir, title, file_size, file_mtime, added_at, last_opened_at, open_count, rating, " +
        "favorite, status, progress_pos, progress_total, time_spent_s, missing, notes, cover, extra";

    private const int ChunkSize = 500;

    private readonly LibraryDb _db;
    private readonly string _mediaDirectory;
    private readonly Action<LibraryChanged> _emit;
    private readonly Action<string> _notify;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _ready;
    private readonly object _kindsLock = new();
    private ItemKindDescriptor[] _kinds = [];

    /// <summary>Creates the service and starts opening and migrating the database on a background thread.</summary>
    /// <param name="databasePath">Path of <c>library.db</c>.</param>
    /// <param name="mediaDirectory">Path of <c>library/media</c>.</param>
    /// <param name="emit">Receives every <see cref="LibraryChanged"/>; may be called from any thread.</param>
    /// <param name="notify">Receives already-localized user notifications; may be called from any thread.</param>
    public LibraryService(string databasePath, string mediaDirectory, Action<LibraryChanged> emit, Action<string> notify)
    {
        _mediaDirectory = LibraryRules.NormalizePath(mediaDirectory);
        _emit = emit;
        _notify = notify;
        _db = new LibraryDb(databasePath);
        _ready = Task.Run(() => _db.Migrate());
    }

    /// <summary>Completes when the database is open and migrated; faults when that failed.</summary>
    public Task Ready => _ready;

    /// <summary>The database, for tests that need to inspect or bulk-load it.</summary>
    internal LibraryDb Database => _db;

    /// <inheritdoc />
    public IReadOnlyList<ItemKindDescriptor> Kinds
    {
        get
        {
            lock (_kindsLock)
            {
                return _kinds;
            }
        }
    }

    /// <inheritdoc />
    public void RegisterKind(IPluginContext owner, ItemKindDescriptor kind)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(kind);
        lock (_kindsLock)
        {
            if (_kinds.Any(k => string.Equals(k.Id, kind.Id, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"Item kind '{kind.Id}' is already registered.");
            }

            _kinds = [.. _kinds, kind];
        }

        owner.Track(() =>
        {
            lock (_kindsLock)
            {
                _kinds = _kinds.Where(k => !ReferenceEquals(k, kind)).ToArray();
            }
        });
    }

    /// <summary>Cancels running scans and closes the database.</summary>
    public void Dispose()
    {
        _lifetime.Cancel();
        _db.Dispose();
        _lifetime.Dispose();
    }

    // ----- sources ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<IReadOnlyList<LibrarySource>> GetSourcesAsync(string kind)
    {
        await _ready.ConfigureAwait(false);
        return await _db.ReadAsync<IReadOnlyList<LibrarySource>>(connection =>
        {
            var result = new List<LibrarySource>();
            using var command = connection.Command(
                "SELECT s.id, s.kind, s.path, s.added_at, s.last_scan_at, " +
                "(SELECT COUNT(*) FROM item i WHERE i.source_id = s.id) " +
                "FROM source s WHERE s.kind = $kind ORDER BY s.path, s.id").With("$kind", kind);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(ReadSource(reader));
            }

            return result;
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<LibrarySource> AddSourceAsync(string kind, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = LibraryRules.NormalizePath(path);
        await _ready.ConfigureAwait(false);
        return await _db.WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using (var existing = connection.Command("SELECT path FROM source WHERE kind = $kind", transaction).With("$kind", kind))
            using (var reader = existing.ExecuteReader())
            {
                while (reader.Read())
                {
                    var other = reader.GetString(0);
                    if (LibraryRules.IsSameOrInside(normalized, other) || LibraryRules.IsSameOrInside(other, normalized))
                    {
                        throw new ArgumentException($"The folder overlaps the existing source '{other}'.", nameof(path));
                    }
                }
            }

            var now = LibraryRules.NowMs();
            var id = Convert.ToInt64(connection.Command(
                    "INSERT INTO source (kind, path, added_at) VALUES ($kind, $path, $now) RETURNING id", transaction)
                .With("$kind", kind).With("$path", normalized).With("$now", now).Scalar());
            transaction.Commit();
            return new LibrarySource(id, kind, normalized, LibraryRules.FromUnixMs(now), null, 0);
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveSourceAsync(long sourceId)
    {
        await _ready.ConfigureAwait(false);
        var removed = await _db.WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var kind = connection.Command("SELECT kind FROM source WHERE id = $id", transaction).With("$id", sourceId).Scalar() as string;
            if (kind is null)
            {
                return (Kind: (string?)null, Ids: new List<long>());
            }

            var ids = new List<long>();
            using (var select = connection.Command("SELECT id FROM item WHERE source_id = $id", transaction).With("$id", sourceId))
            using (var reader = select.ExecuteReader())
            {
                while (reader.Read())
                {
                    ids.Add(reader.GetInt64(0));
                }
            }

            // Cascades to the source's items and their tag links (foreign_keys = ON).
            connection.Command("DELETE FROM source WHERE id = $id", transaction).With("$id", sourceId).Execute();
            DeleteOrphanTags(connection, transaction);
            transaction.Commit();
            return (Kind: kind, Ids: ids);
        }).ConfigureAwait(false);

        if (removed.Kind is null)
        {
            return;
        }

        foreach (var id in removed.Ids)
        {
            DeleteMediaFolder(id);
        }

        _emit(new LibraryChanged(removed.Kind, LibraryChangeType.Reset, []));
    }

    // ----- items -----------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<LibraryItem?> GetAsync(long id)
    {
        await _ready.ConfigureAwait(false);
        return await _db.ReadAsync<LibraryItem?>(connection =>
        {
            var item = SelectItem(connection, id);
            return item is null ? null : AttachTags(connection, [item])[0];
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<LibraryItem> AddAsync(NewItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(item.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(item.Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(item.Title);
        var path = LibraryRules.NormalizePath(item.Path);
        await _ready.ConfigureAwait(false);
        var created = await _db.WriteAsync(connection =>
        {
            try
            {
                var id = Convert.ToInt64(connection.Command(
                        "INSERT INTO item (kind, source_id, path, dir, title, added_at, extra) " +
                        "VALUES ($kind, NULL, $path, $dir, $title, $now, $extra) RETURNING id")
                    .With("$kind", item.Kind).With("$path", path).With("$dir", LibraryRules.DirectoryOf(path))
                    .With("$title", item.Title).With("$now", LibraryRules.NowMs()).With("$extra", string.IsNullOrWhiteSpace(item.Extra) ? "{}" : item.Extra)
                    .Scalar());
                return SelectItem(connection, id)!;
            }
            catch (SqliteException ex) when (ex.IsConstraintViolation())
            {
                throw new InvalidOperationException($"An item of kind '{item.Kind}' with the path '{path}' already exists.", ex);
            }
        }).ConfigureAwait(false);

        _emit(new LibraryChanged(created.Kind, LibraryChangeType.Added, [created.Id]));
        return created;
    }

    /// <inheritdoc />
    public async Task UpdateAsync(long id, ItemPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (patch.Rating is < 0 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(patch), "Rating must be between 0 and 5.");
        }

        if (patch.Title is not null && string.IsNullOrWhiteSpace(patch.Title))
        {
            throw new ArgumentException("Title must not be empty.", nameof(patch));
        }

        await _ready.ConfigureAwait(false);
        var kind = await _db.WriteAsync(connection => UpdateCore(connection, id, patch)).ConfigureAwait(false);
        if (kind is not null)
        {
            _emit(new LibraryChanged(kind, LibraryChangeType.Updated, [id]));
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(IReadOnlyCollection<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return;
        }

        await _ready.ConfigureAwait(false);
        var removed = await _db.WriteAsync(connection =>
        {
            var result = new List<(long Id, string Kind)>();
            using var transaction = connection.BeginTransaction();
            foreach (var chunk in ids.Distinct().Chunk(ChunkSize))
            {
                using (var select = connection.Command("SELECT id, kind FROM item", transaction))
                {
                    select.CommandText += " WHERE id IN " + AddInParameters(select, chunk, "$i");
                    using var reader = select.ExecuteReader();
                    while (reader.Read())
                    {
                        result.Add((reader.GetInt64(0), reader.GetString(1)));
                    }
                }

                using var delete = connection.Command("DELETE FROM item", transaction);
                delete.CommandText += " WHERE id IN " + AddInParameters(delete, chunk, "$i");
                delete.Execute();
            }

            DeleteOrphanTags(connection, transaction);
            transaction.Commit();
            return result;
        }).ConfigureAwait(false);

        foreach (var (id, _) in removed)
        {
            DeleteMediaFolder(id);
        }

        foreach (var group in removed.GroupBy(r => r.Kind))
        {
            _emit(new LibraryChanged(group.Key, LibraryChangeType.Removed, group.Select(r => r.Id).ToList()));
        }
    }

    // ----- tags ------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task SetTagsAsync(long id, IReadOnlyCollection<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var normalized = LibraryRules.NormalizeTags(tags);
        await _ready.ConfigureAwait(false);
        var kind = await _db.WriteAsync(connection => SetTagsCore(connection, id, normalized)).ConfigureAwait(false);
        if (kind is not null)
        {
            _emit(new LibraryChanged(kind, LibraryChangeType.Updated, [id]));
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TagCount>> GetTagsAsync(string kind)
    {
        await _ready.ConfigureAwait(false);
        return await _db.ReadAsync<IReadOnlyList<TagCount>>(connection =>
        {
            var result = new List<TagCount>();
            using var command = connection.Command(
                "SELECT t.name, COUNT(*) FROM tag t JOIN item_tag it ON it.tag_id = t.id JOIN item i ON i.id = it.item_id " +
                "WHERE i.kind = $kind GROUP BY t.id ORDER BY t.name").With("$kind", kind);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new TagCount(reader.GetString(0), reader.GetInt32(1)));
            }

            return result;
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetDirectoriesAsync(string kind)
    {
        await _ready.ConfigureAwait(false);
        return await _db.ReadAsync<IReadOnlyList<string>>(connection =>
        {
            var result = new List<string>();
            using var command = connection.Command("SELECT DISTINCT dir FROM item WHERE kind = $kind AND missing = 0").With("$kind", kind);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(reader.GetString(0));
            }

            result.Sort(LibraryRules.Natural);
            return result;
        }).ConfigureAwait(false);
    }

    // ----- database helpers ------------------------------------------------------------------------------------

    private static LibrarySource ReadSource(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        LibraryRules.FromUnixMs(reader.GetInt64(3)),
        reader.IsDBNull(4) ? null : LibraryRules.FromUnixMs(reader.GetInt64(4)),
        reader.GetInt32(5));

    /// <param name="reader">Positioned on a row selected with <see cref="ItemColumns"/>.</param>
    /// <param name="kind">The kind when the caller already knows it (saves one string per row).</param>
    private LibraryItem ReadItem(SqliteDataReader reader, string? kind = null) => new()
    {
        Id = reader.GetInt64(0),
        Kind = kind ?? reader.GetString(1),
        SourceId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
        Path = reader.GetString(3),
        Directory = reader.GetString(4),
        Title = reader.GetString(5),
        FileSize = reader.GetInt64(6),
        FileModified = LibraryRules.FromUnixMs(reader.GetInt64(7)),
        AddedAt = LibraryRules.FromUnixMs(reader.GetInt64(8)),
        LastOpenedAt = reader.IsDBNull(9) ? null : LibraryRules.FromUnixMs(reader.GetInt64(9)),
        OpenCount = reader.GetInt32(10),
        Rating = reader.GetInt32(11),
        IsFavorite = reader.GetInt64(12) != 0,
        Status = (ItemStatus)reader.GetInt32(13),
        ProgressPosition = reader.GetInt64(14),
        ProgressTotal = reader.GetInt64(15),
        TimeSpentSeconds = reader.GetInt64(16),
        IsMissing = reader.GetInt64(17) != 0,
        Notes = reader.GetString(18),
        CoverPath = reader.IsDBNull(19) ? null : Path.Combine(_mediaDirectory, reader.GetString(19)),
        Extra = reader.GetString(20),
    };

    private LibraryItem? SelectItem(SqliteConnection connection, long id)
    {
        using var command = connection.Command($"SELECT {ItemColumns} FROM item WHERE id = $id").With("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadItem(reader) : null;
    }

    /// <summary>Loads the tags of <paramref name="items"/> with chunked <c>IN</c> queries (no N+1).</summary>
    private static List<LibraryItem> AttachTags(SqliteConnection connection, IReadOnlyList<LibraryItem> items)
    {
        var tagsById = new Dictionary<long, List<string>>();
        if (items.Count > 0)
        {
            // One statement with a fixed number of parameters, re-bound per chunk (the last chunk repeats its final id),
            // so SQLite compiles it once however many items there are.
            using var command = connection.CreateCommand();
            var slots = new SqliteParameter[Math.Min(items.Count, ChunkSize)];
            var names = new string[slots.Length];
            for (var i = 0; i < slots.Length; i++)
            {
                names[i] = "$i" + i;
                slots[i] = command.Parameters.Add(names[i], SqliteType.Integer);
            }

            command.CommandText = "SELECT it.item_id, t.name FROM item_tag it JOIN tag t ON t.id = it.tag_id " +
                                  $"WHERE it.item_id IN ({string.Join(',', names)}) ORDER BY t.name";
            foreach (var chunk in items.Select(i => i.Id).Chunk(slots.Length))
            {
                for (var i = 0; i < slots.Length; i++)
                {
                    slots[i].Value = chunk[Math.Min(i, chunk.Length - 1)];
                }

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var itemId = reader.GetInt64(0);
                    if (!tagsById.TryGetValue(itemId, out var list))
                    {
                        tagsById[itemId] = list = [];
                    }

                    list.Add(reader.GetString(1));
                }
            }
        }

        var result = new List<LibraryItem>(items.Count);
        foreach (var item in items)
        {
            result.Add(tagsById.TryGetValue(item.Id, out var tags) ? item with { Tags = tags } : item);
        }

        return result;
    }

    /// <summary>Appends <c>$prefix0, $prefix1…</c> parameters to the command and returns <c>($prefix0,$prefix1…)</c>.</summary>
    private static string AddInParameters(SqliteCommand command, IReadOnlyList<long> values, string prefix)
    {
        var names = new string[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            names[i] = prefix + i;
            command.Parameters.AddWithValue(names[i], values[i]);
        }

        return "(" + string.Join(',', names) + ")";
    }

    private static void DeleteOrphanTags(SqliteConnection connection, SqliteTransaction transaction) =>
        connection.Command("DELETE FROM tag WHERE NOT EXISTS (SELECT 1 FROM item_tag WHERE item_tag.tag_id = tag.id)", transaction).Execute();

    private static string? SetTagsCore(SqliteConnection connection, long id, List<string> tags)
    {
        using var transaction = connection.BeginTransaction();
        var kind = connection.Command("SELECT kind FROM item WHERE id = $id", transaction).With("$id", id).Scalar() as string;
        if (kind is null)
        {
            return null;
        }

        var oldTagIds = new List<long>();
        using (var select = connection.Command("SELECT tag_id FROM item_tag WHERE item_id = $id", transaction).With("$id", id))
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                oldTagIds.Add(reader.GetInt64(0));
            }
        }

        connection.Command("DELETE FROM item_tag WHERE item_id = $id", transaction).With("$id", id).Execute();
        foreach (var tag in tags)
        {
            connection.Command("INSERT INTO tag (name) VALUES ($name) ON CONFLICT (name) DO NOTHING", transaction).With("$name", tag).Execute();
            var tagId = Convert.ToInt64(connection.Command("SELECT id FROM tag WHERE name = $name", transaction).With("$name", tag).Scalar());
            connection.Command("INSERT OR IGNORE INTO item_tag (item_id, tag_id) VALUES ($item, $tag)", transaction)
                .With("$item", id).With("$tag", tagId).Execute();
        }

        foreach (var chunk in oldTagIds.Chunk(ChunkSize))
        {
            var cleanup = connection.Command("DELETE FROM tag", transaction);
            cleanup.CommandText += " WHERE id IN " + AddInParameters(cleanup, chunk, "$t") +
                                   " AND NOT EXISTS (SELECT 1 FROM item_tag WHERE item_tag.tag_id = tag.id)";
            cleanup.Execute();
        }

        transaction.Commit();
        return kind;
    }

    /// <summary>Applies the patch; returns the item's kind, or null when nothing was changed or the item does not exist.</summary>
    private string? UpdateCore(SqliteConnection connection, long id, ItemPatch patch)
    {
        string kind;
        long? sourceId;
        string currentPath;
        string? cover;
        using (var select = connection.Command("SELECT kind, source_id, path, cover FROM item WHERE id = $id").With("$id", id))
        using (var reader = select.ExecuteReader())
        {
            if (!reader.Read())
            {
                return null;
            }

            kind = reader.GetString(0);
            sourceId = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            currentPath = reader.GetString(2);
            cover = reader.IsDBNull(3) ? null : reader.GetString(3);
        }

        if (patch.Path is not null && sourceId is not null)
        {
            throw new InvalidOperationException("The path of an item that belongs to a source cannot be changed.");
        }

        var sets = new List<string>();
        using var update = connection.CreateCommand();
        void Set(string column, string parameter, object? value)
        {
            sets.Add($"{column} = {parameter}");
            update.Parameters.AddWithValue(parameter, value ?? DBNull.Value);
        }

        if (patch.Title is not null) Set("title", "$title", patch.Title);
        if (patch.Path is not null)
        {
            var path = LibraryRules.NormalizePath(patch.Path);
            Set("path", "$path", path);
            Set("dir", "$dir", LibraryRules.DirectoryOf(path));
        }

        if (patch.Rating is { } rating) Set("rating", "$rating", rating);
        if (patch.IsFavorite is { } favorite) Set("favorite", "$favorite", favorite ? 1 : 0);
        if (patch.Status is { } status) Set("status", "$status", (int)status);
        if (patch.ProgressPosition is { } position) Set("progress_pos", "$pos", position);
        if (patch.ProgressTotal is { } total) Set("progress_total", "$total", total);
        if (patch.AddTimeSpentSeconds is { } seconds)
        {
            sets.Add("time_spent_s = time_spent_s + $seconds");
            update.Parameters.AddWithValue("$seconds", seconds);
        }

        if (patch.MarkOpened)
        {
            Set("last_opened_at", "$opened", LibraryRules.NowMs());
            sets.Add("open_count = open_count + 1");
        }

        if (patch.Notes is not null) Set("notes", "$notes", patch.Notes);
        if (patch.Extra is not null) Set("extra", "$extra", string.IsNullOrWhiteSpace(patch.Extra) ? "{}" : patch.Extra);

        if (patch.SetCoverFromFile is not null)
        {
            var relative = CopyCover(id, patch.SetCoverFromFile);
            if (cover is not null && !string.Equals(cover, relative, StringComparison.OrdinalIgnoreCase))
            {
                DeleteMediaFile(cover);
            }

            Set("cover", "$cover", relative);
        }
        else if (patch.ClearCover && cover is not null)
        {
            DeleteMediaFile(cover);
            Set("cover", "$cover", null);
        }

        if (sets.Count == 0)
        {
            return null;
        }

        update.CommandText = $"UPDATE item SET {string.Join(", ", sets)} WHERE id = $id";
        update.Parameters.AddWithValue("$id", id);
        try
        {
            update.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.IsConstraintViolation())
        {
            throw new InvalidOperationException($"Another item already uses the path '{patch.Path ?? currentPath}'.", ex);
        }

        return kind;
    }

    // ----- media folder (the only files the library ever deletes) -----------------------------------------------

    private string MediaFolder(long id) => Path.Combine(_mediaDirectory, id.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Copies <paramref name="source"/> to <c>media/&lt;id&gt;/cover&lt;ext&gt;</c>; returns the path relative to the media folder.</summary>
    private string CopyCover(long id, string source)
    {
        var folder = MediaFolder(id);
        var fileName = "cover" + Path.GetExtension(source);
        var destination = Path.Combine(folder, fileName);
        Directory.CreateDirectory(folder);
        if (!string.Equals(Path.GetFullPath(source), destination, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(source, destination, overwrite: true);
        }

        return Path.Combine(id.ToString(System.Globalization.CultureInfo.InvariantCulture), fileName);
    }

    /// <summary>Deletes one file below the media folder (and its folder when it becomes empty). Anything outside is ignored.</summary>
    private void DeleteMediaFile(string relativePath)
    {
        try
        {
            var full = Path.GetFullPath(Path.Combine(_mediaDirectory, relativePath));
            if (!LibraryRules.IsSameOrInside(full, _mediaDirectory) || string.Equals(full, _mediaDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            File.Delete(full);
            var parent = Path.GetDirectoryName(full);
            if (parent is not null
                && !string.Equals(parent, _mediaDirectory, StringComparison.OrdinalIgnoreCase)
                && LibraryRules.IsSameOrInside(parent, _mediaDirectory)
                && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cover that cannot be deleted is not worth failing the operation for.
        }
    }

    private void DeleteMediaFolder(long id)
    {
        try
        {
            var folder = MediaFolder(id);
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftover cover files are harmless.
        }
    }
}
