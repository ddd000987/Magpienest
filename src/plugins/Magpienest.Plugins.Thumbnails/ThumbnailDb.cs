using System.IO;
using Magpienest.Sdk;
using Microsoft.Data.Sqlite;

namespace Magpienest.Plugins.Thumbnails;

/// <summary>
/// The thumbnail cache file. All methods are synchronous and must be called off the UI thread.
/// Writes are buffered and flushed in one transaction every <c>flushInterval</c> or when <c>flushBatch</c> thumbnails
/// are waiting; every write (flush, delete, clear) is serialised by one lock; reads open their own connection.
/// If the file cannot be opened it is deleted and recreated once; if that fails too the cache is disabled
/// (every read misses, writes are dropped) so thumbnails still work, just without persistence.
/// </summary>
internal sealed class ThumbnailDb : IDisposable
{
    private const string Pragmas =
        "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";

    private const string CreateTable =
        "CREATE TABLE IF NOT EXISTS thumb (key TEXT PRIMARY KEY, stamp TEXT NOT NULL, data BLOB NOT NULL, created INTEGER NOT NULL) WITHOUT ROWID;";

    private readonly record struct Pending(string Stamp, byte[] Data);

    private readonly string _path;
    private readonly string _connectionString;
    private readonly ILog _log;
    private readonly TimeSpan _flushInterval;
    private readonly int _flushBatch;
    private readonly object _writeLock = new();
    private readonly object _initLock = new();
    private readonly object _pendingLock = new();
    private readonly Timer _timer;
    private Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private Dictionary<string, Pending>? _flushing;
    private bool _timerArmed;
    private bool _ready;
    private bool _failed;
    private volatile bool _disposed;

    public ThumbnailDb(string path, ILog log, TimeSpan flushInterval, int flushBatch)
    {
        _path = path;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        _log = log;
        _flushInterval = flushInterval;
        _flushBatch = flushBatch;
        _timer = new Timer(_ => OnTimer(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Number of thumbnails waiting to be written.</summary>
    public int PendingCount
    {
        get
        {
            lock (_pendingLock)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>Returns the JPEG stored for the key when its stamp matches; null on a miss or any error.</summary>
    public byte[]? TryRead(string key, string stamp)
    {
        lock (_pendingLock)
        {
            if (_pending.TryGetValue(key, out var p) && p.Stamp == stamp)
            {
                return p.Data;
            }

            if (_flushing is not null && _flushing.TryGetValue(key, out p) && p.Stamp == stamp)
            {
                return p.Data;
            }
        }

        if (_disposed || !EnsureReady())
        {
            return null;
        }

        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT data FROM thumb WHERE key = $key AND stamp = $stamp";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$stamp", stamp);
            return command.ExecuteScalar() as byte[];
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
        {
            _log.Warn("Reading the thumbnail cache failed.", ex);
            return null;
        }
    }

    /// <summary>Queues a thumbnail for the next batch write.</summary>
    public void Enqueue(string key, string stamp, byte[] data)
    {
        if (_disposed || _failed)
        {
            return;
        }

        bool flushNow;
        lock (_pendingLock)
        {
            _pending[key] = new Pending(stamp, data);
            flushNow = _pending.Count >= _flushBatch;
            if (!flushNow && !_timerArmed)
            {
                _timerArmed = true;
                _timer.Change(_flushInterval, Timeout.InfiniteTimeSpan);
            }
        }

        if (flushNow)
        {
            ThreadPool.QueueUserWorkItem(_ => Flush());
        }
    }

    /// <summary>Writes every queued thumbnail in one transaction.</summary>
    public void Flush()
    {
        lock (_writeLock)
        {
            Dictionary<string, Pending> batch;
            lock (_pendingLock)
            {
                _timerArmed = false;
                if (_pending.Count == 0)
                {
                    return;
                }

                batch = _pending;
                _pending = new Dictionary<string, Pending>(StringComparer.Ordinal);
                _flushing = batch; // still visible to TryRead until committed
            }

            try
            {
                if (EnsureReady())
                {
                    using var connection = Open();
                    using var transaction = connection.BeginTransaction();
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "INSERT OR REPLACE INTO thumb (key, stamp, data, created) VALUES ($key, $stamp, $data, $created)";
                    var key = command.Parameters.Add("$key", SqliteType.Text);
                    var stamp = command.Parameters.Add("$stamp", SqliteType.Text);
                    var data = command.Parameters.Add("$data", SqliteType.Blob);
                    var created = command.Parameters.Add("$created", SqliteType.Integer);
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    foreach (var (k, p) in batch)
                    {
                        key.Value = k;
                        stamp.Value = p.Stamp;
                        data.Value = p.Data;
                        created.Value = now;
                        command.ExecuteNonQuery();
                    }

                    transaction.Commit();
                }
            }
            catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
            {
                _log.Warn($"Writing {batch.Count} thumbnails to the cache failed; they are dropped.", ex);
            }
            finally
            {
                lock (_pendingLock)
                {
                    _flushing = null;
                }
            }
        }
    }

    /// <summary>Removes one entry (queued or stored).</summary>
    public void Delete(string key)
    {
        lock (_writeLock)
        {
            lock (_pendingLock)
            {
                _pending.Remove(key);
            }

            if (_disposed || !EnsureReady())
            {
                return;
            }

            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM thumb WHERE key = $key";
                command.Parameters.AddWithValue("$key", key);
                command.ExecuteNonQuery();
            }
            catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
            {
                _log.Warn("Removing a thumbnail from the cache failed.", ex);
            }
        }
    }

    /// <summary>Drops queued writes, drops and recreates the table, then VACUUMs.</summary>
    public void Clear()
    {
        lock (_writeLock)
        {
            lock (_pendingLock)
            {
                _pending.Clear();
            }

            if (_disposed || !EnsureReady())
            {
                return;
            }

            try
            {
                using var connection = Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "DROP TABLE IF EXISTS thumb; " + CreateTable;
                    command.ExecuteNonQuery();
                }

                using (var vacuum = connection.CreateCommand())
                {
                    vacuum.CommandText = "VACUUM";
                    vacuum.ExecuteNonQuery();
                }
            }
            catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
            {
                _log.Warn("Clearing the thumbnail cache failed.", ex);
            }
        }
    }

    /// <summary>Flushes what is queued and releases every connection to the file.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Flush();
        _disposed = true;
        _timer.Dispose();
        SqliteConnection.ClearAllPools();
    }

    private void OnTimer()
    {
        try
        {
            if (!_disposed)
            {
                Flush();
            }
        }
        catch (Exception ex)
        {
            _log.Warn("Flushing the thumbnail cache failed.", ex);
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = Pragmas;
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private bool EnsureReady()
    {
        if (_ready)
        {
            return true;
        }

        if (_failed)
        {
            return false;
        }

        lock (_initLock)
        {
            if (_ready)
            {
                return true;
            }

            if (_failed)
            {
                return false;
            }

            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                    using (var connection = Open())
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = CreateTable;
                        command.ExecuteNonQuery();
                    }

                    _ready = true;
                    return true;
                }
                catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
                {
                    _log.Warn(attempt == 0
                        ? "The thumbnail cache could not be opened; recreating it."
                        : "The thumbnail cache could not be recreated; running without a disk cache.", ex);
                    if (attempt == 0)
                    {
                        SqliteConnection.ClearAllPools();
                        DeleteFiles();
                    }
                }
            }

            _failed = true;
            return false;
        }
    }

    private void DeleteFiles()
    {
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Debug($"Could not delete '{file}': {ex.Message}");
            }
        }
    }
}
