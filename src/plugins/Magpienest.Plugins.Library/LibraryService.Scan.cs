using Magpienest.Sdk;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Localization;
using Microsoft.Data.Sqlite;

namespace Magpienest.Plugins.Library;

internal sealed partial class LibraryService
{
    private const int BatchSize = 1000;
    private const int ProgressInterval = 200;

    private readonly object _scanLock = new();
    private readonly Dictionary<long, Task<ScanResult>> _scans = [];

    /// <inheritdoc />
    public Task<ScanResult> ScanAsync(long sourceId, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        Task<ScanResult> shared;
        lock (_scanLock)
        {
            if (!_scans.TryGetValue(sourceId, out shared!))
            {
                var completion = new TaskCompletionSource<ScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                shared = completion.Task;
                _scans[sourceId] = shared;
                _ = Task.Run(() => RunScan(sourceId, progress, ct, completion));

                // The starter observes cancellation through the scan itself, which commits its partial work first.
                return shared;
            }
        }

        // A second request for the same source simply awaits the running scan (it may stop waiting; the scan goes on).
        return shared.WaitAsync(ct);
    }

    private void RunScan(long sourceId, IProgress<ScanProgress>? progress, CancellationToken ct, TaskCompletionSource<ScanResult> completion)
    {
        ScanResult? result = null;
        Exception? failure = null;
        try
        {
            _ready.GetAwaiter().GetResult();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            result = ScanCore(sourceId, progress, linked.Token);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        // Unregister first so that the continuation of an awaiter can start the next scan immediately.
        lock (_scanLock)
        {
            _scans.Remove(sourceId);
        }

        if (failure is OperationCanceledException)
        {
            completion.SetCanceled();
        }
        else if (failure is not null)
        {
            completion.SetException(failure);
        }
        else
        {
            completion.SetResult(result!);
        }
    }

    private ScanResult ScanCore(long sourceId, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var source = _db.Read(connection => LoadSource(connection, sourceId))
                     ?? throw new KeyNotFoundException($"Source {sourceId} does not exist.");
        var kind = source.Kind;
        var scanner = Kinds.FirstOrDefault(k => k.Id == kind)?.Scanner
                      ?? throw new InvalidOperationException($"No scanner is registered for kind '{kind}'.");

        if (!Directory.Exists(source.Path))
        {
            // Probably an unplugged drive: change nothing, in particular do not mark items missing.
            _notify(Loc.Format("Library.Scan.RootUnreachable", source.Path));
            return new ScanResult(0, 0, 0, 0, 0);
        }

        var known = new Dictionary<string, KnownRow>(StringComparer.OrdinalIgnoreCase);
        _db.Read(connection =>
        {
            using var command = connection.Command(
                "SELECT id, path, file_size, file_mtime, missing, title FROM item WHERE source_id = $source").With("$source", sourceId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = new KnownRow(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
                    reader.GetInt64(4) != 0, reader.GetString(5));
                known[row.Path] = row;
            }

            return 0;
        });

        var fresh = new List<ScannedEntry>();
        var freshPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<RowUpdate>();
        var total = 0;
        var updated = 0;
        var cancelled = false;

        try
        {
            foreach (var entry in scanner.Scan(source.Path, ct))
            {
                ct.ThrowIfCancellationRequested();
                total++;
                if (total % ProgressInterval == 0)
                {
                    progress?.Report(new ScanProgress(total, Path.GetDirectoryName(entry.Path)));
                }

                var path = LibraryRules.NormalizePath(entry.Path);
                var mtime = LibraryRules.ToUnixMs(entry.Modified);
                if (known.TryGetValue(path, out var row))
                {
                    row.Seen = true;
                    if (row.Size != entry.Size || row.Mtime != mtime || row.Missing)
                    {
                        var title = LibraryRules.IsAutoTitle(row.Title, row.Path) ? entry.Title : row.Title;
                        pending.Add(new RowUpdate(row.Id, entry.Size, mtime, title));
                        (row.Size, row.Mtime, row.Missing, row.Title) = (entry.Size, mtime, false, title);
                        updated++;
                        if (pending.Count >= BatchSize)
                        {
                            FlushUpdates(pending);
                        }
                    }
                }
                else if (freshPaths.Add(path))
                {
                    fresh.Add(entry with { Path = path });
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
        }

        FlushUpdates(pending);

        // Candidates for relinking: rows this scan did not see, plus everything already flagged missing.
        var gone = cancelled ? [] : known.Values.Where(r => !r.Seen && !r.Missing).ToList();
        var candidates = new List<Candidate>(gone.Select(r => new Candidate(r.Id, r.Path, r.Size, r.Mtime, r.Title)));
        candidates.AddRange(LoadMissingCandidates(kind));
        var index = new Dictionary<(long Size, long Mtime), List<Candidate>>();
        foreach (var candidate in candidates)
        {
            if (!index.TryGetValue((candidate.Size, candidate.Mtime), out var bucket))
            {
                index[(candidate.Size, candidate.Mtime)] = bucket = [];
            }

            bucket.Add(candidate);
        }

        var added = 0;
        var relinked = 0;
        foreach (var batch in fresh.Chunk(BatchSize))
        {
            var (batchAdded, batchRelinked, batchAdopted) = _db.Write(connection => InsertBatch(connection, source, kind, batch, index));
            added += batchAdded;
            relinked += batchRelinked;
            updated += batchAdopted;
        }

        var markedMissing = 0;
        if (!cancelled)
        {
            var relinkedIds = candidates.Where(c => c.Taken).Select(c => c.Id).ToHashSet();
            var toMark = gone.Where(r => !relinkedIds.Contains(r.Id)).Select(r => r.Id).ToList();
            markedMissing = toMark.Count;
            foreach (var batch in toMark.Chunk(BatchSize))
            {
                _db.Write(connection =>
                {
                    using var transaction = connection.BeginTransaction();
                    using var command = connection.Command("UPDATE item SET missing = 1 WHERE id = $id", transaction);
                    var id = command.Parameters.Add("$id", SqliteType.Integer);
                    foreach (var value in batch)
                    {
                        id.Value = value;
                        command.ExecuteNonQuery();
                    }

                    transaction.Commit();
                    return 0;
                });
            }

            _db.Write(connection => connection.Command("UPDATE source SET last_scan_at = $now WHERE id = $id")
                .With("$now", LibraryRules.NowMs()).With("$id", sourceId).Execute());
        }

        if (!cancelled || added + relinked + updated > 0)
        {
            _emit(new LibraryChanged(kind, LibraryChangeType.Reset, []));
        }

        if (cancelled)
        {
            throw new OperationCanceledException(ct);
        }

        return new ScanResult(added, updated, markedMissing, relinked, total);
    }

    private void FlushUpdates(List<RowUpdate> pending)
    {
        if (pending.Count == 0)
        {
            return;
        }

        _db.Write(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.Command(
                "UPDATE item SET file_size = $size, file_mtime = $mtime, title = $title, missing = 0 WHERE id = $id", transaction);
            var id = command.Parameters.Add("$id", SqliteType.Integer);
            var size = command.Parameters.Add("$size", SqliteType.Integer);
            var mtime = command.Parameters.Add("$mtime", SqliteType.Integer);
            var title = command.Parameters.Add("$title", SqliteType.Text);
            foreach (var update in pending)
            {
                id.Value = update.Id;
                size.Value = update.Size;
                mtime.Value = update.Mtime;
                title.Value = update.Title;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            return 0;
        });
        pending.Clear();
    }

    private List<Candidate> LoadMissingCandidates(string kind) => _db.Read(connection =>
    {
        var result = new List<Candidate>();
        using var command = connection.Command(
            "SELECT id, path, file_size, file_mtime, title FROM item WHERE kind = $kind AND missing = 1").With("$kind", kind);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new Candidate(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4)));
        }

        return result;
    });

    private static LibrarySource? LoadSource(SqliteConnection connection, long sourceId)
    {
        using var command = connection.Command(
            "SELECT s.id, s.kind, s.path, s.added_at, s.last_scan_at, 0 FROM source s WHERE s.id = $id").With("$id", sourceId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSource(reader) : null;
    }

    /// <summary>Relinks or inserts one batch of new entries in a single transaction.</summary>
    private static (int Added, int Relinked, int Adopted) InsertBatch(
        SqliteConnection connection, LibrarySource source, string kind, ScannedEntry[] batch,
        Dictionary<(long Size, long Mtime), List<Candidate>> index)
    {
        var added = 0;
        var relinked = 0;
        var adopted = 0;
        var now = LibraryRules.NowMs();
        using var transaction = connection.BeginTransaction();

        using var relink = connection.Command(
            "UPDATE item SET path = $path, dir = $dir, source_id = $source, title = $title, missing = 0 WHERE id = $id", transaction);
        using var insert = connection.Command(
            "INSERT INTO item (kind, source_id, path, dir, title, file_size, file_mtime, added_at) " +
            "VALUES ($kind, $source, $path, $dir, $title, $size, $mtime, $now) ON CONFLICT (kind, path) DO NOTHING", transaction);
        using var adopt = connection.Command(
            "UPDATE item SET source_id = $source, file_size = $size, file_mtime = $mtime, missing = 0 " +
            "WHERE kind = $kind AND path = $path AND source_id IS NULL", transaction);

        foreach (var entry in batch)
        {
            var dir = LibraryRules.DirectoryOf(entry.Path);
            var mtime = LibraryRules.ToUnixMs(entry.Modified);

            if (index.TryGetValue((entry.Size, mtime), out var bucket))
            {
                var fileName = Path.GetFileName(entry.Path);
                var matches = bucket.Where(c => !c.Taken
                    && (string.Equals(Path.GetFileName(c.Path), fileName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(LibraryRules.DirectoryOf(c.Path), dir, StringComparison.OrdinalIgnoreCase))).Take(2).ToList();
                if (matches.Count == 1 && TryRelink(relink, matches[0], entry, dir, source.Id))
                {
                    matches[0].Taken = true;
                    relinked++;
                    continue;
                }
            }

            Bind(insert, ("$kind", kind), ("$source", source.Id), ("$path", entry.Path), ("$dir", dir),
                ("$title", entry.Title), ("$size", entry.Size), ("$mtime", mtime), ("$now", now));
            if (insert.ExecuteNonQuery() > 0)
            {
                added++;
                continue;
            }

            // A manually added item already has this path: let the source adopt it.
            Bind(adopt, ("$source", source.Id), ("$size", entry.Size), ("$mtime", mtime), ("$kind", kind), ("$path", entry.Path));
            adopted += adopt.ExecuteNonQuery();
        }

        transaction.Commit();
        return (added, relinked, adopted);
    }

    private static bool TryRelink(SqliteCommand relink, Candidate candidate, ScannedEntry entry, string dir, long sourceId)
    {
        var title = LibraryRules.IsAutoTitle(candidate.Title, candidate.Path) ? entry.Title : candidate.Title;
        Bind(relink, ("$path", entry.Path), ("$dir", dir), ("$source", sourceId), ("$title", title), ("$id", candidate.Id));
        try
        {
            return relink.ExecuteNonQuery() > 0;
        }
        catch (SqliteException ex) when (ex.IsConstraintViolation())
        {
            // The new path is already taken by another row; fall back to inserting.
            return false;
        }
    }

    private static void Bind(SqliteCommand command, params (string Name, object? Value)[] values)
    {
        foreach (var (name, value) in values)
        {
            if (command.Parameters.Contains(name))
            {
                command.Parameters[name].Value = value ?? DBNull.Value;
            }
            else
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }
        }
    }

    private sealed class KnownRow(long id, string path, long size, long mtime, bool missing, string title)
    {
        public long Id { get; } = id;
        public string Path { get; } = path;
        public long Size { get; set; } = size;
        public long Mtime { get; set; } = mtime;
        public bool Missing { get; set; } = missing;
        public string Title { get; set; } = title;
        public bool Seen { get; set; }
    }

    private sealed record RowUpdate(long Id, long Size, long Mtime, string Title);

    private sealed class Candidate(long id, string path, long size, long mtime, string title)
    {
        public long Id { get; } = id;
        public string Path { get; } = path;
        public long Size { get; } = size;
        public long Mtime { get; } = mtime;
        public string Title { get; } = title;
        public bool Taken { get; set; }
    }
}
