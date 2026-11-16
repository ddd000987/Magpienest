using Microsoft.Data.Sqlite;

namespace Magpienest.Plugins.Library;

/// <summary>
/// The single entry point to <c>library.db</c>: pragmas on every connection,
/// one gate for writers, readers on their own connections, migrations by <c>PRAGMA user_version</c>.
/// </summary>
internal sealed class LibraryDb : IDisposable
{
    /// <summary>Released scripts are never edited; append a new entry instead.</summary>
    internal static readonly IReadOnlyList<(int Version, string Sql)> Migrations =
    [
        (1, SchemaV1),
    ];

    private const string SchemaV1 = """
        CREATE TABLE source (
          id            INTEGER PRIMARY KEY,
          kind          TEXT    NOT NULL,
          path          TEXT    NOT NULL COLLATE NOCASE,
          added_at      INTEGER NOT NULL,
          last_scan_at  INTEGER,
          UNIQUE (kind, path)
        );

        CREATE TABLE item (
          id              INTEGER PRIMARY KEY,
          kind            TEXT    NOT NULL,
          source_id       INTEGER REFERENCES source(id) ON DELETE CASCADE,
          path            TEXT    NOT NULL COLLATE NOCASE,
          dir             TEXT    NOT NULL COLLATE NOCASE,
          title           TEXT    NOT NULL,
          file_size       INTEGER NOT NULL DEFAULT 0,
          file_mtime      INTEGER NOT NULL DEFAULT 0,
          added_at        INTEGER NOT NULL,
          last_opened_at  INTEGER,
          open_count      INTEGER NOT NULL DEFAULT 0,
          rating          INTEGER NOT NULL DEFAULT 0,
          favorite        INTEGER NOT NULL DEFAULT 0,
          status          INTEGER NOT NULL DEFAULT 0,
          progress_pos    INTEGER NOT NULL DEFAULT 0,
          progress_total  INTEGER NOT NULL DEFAULT 0,
          time_spent_s    INTEGER NOT NULL DEFAULT 0,
          missing         INTEGER NOT NULL DEFAULT 0,
          notes           TEXT    NOT NULL DEFAULT '',
          cover           TEXT,
          extra           TEXT    NOT NULL DEFAULT '{}',
          UNIQUE (kind, path)
        );
        CREATE INDEX ix_item_kind_dir    ON item (kind, dir);
        CREATE INDEX ix_item_kind_added  ON item (kind, added_at);
        CREATE INDEX ix_item_source      ON item (source_id);

        CREATE TABLE tag (
          id    INTEGER PRIMARY KEY,
          name  TEXT NOT NULL UNIQUE COLLATE NOCASE
        );

        CREATE TABLE item_tag (
          item_id  INTEGER NOT NULL REFERENCES item(id) ON DELETE CASCADE,
          tag_id   INTEGER NOT NULL REFERENCES tag(id)  ON DELETE CASCADE,
          PRIMARY KEY (item_id, tag_id)
        ) WITHOUT ROWID;
        CREATE INDEX ix_item_tag_tag ON item_tag (tag_id);
        """;

    private const string ConnectionPragmas =
        "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";

    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private volatile bool _disposed;

    public LibraryDb(string path)
    {
        DatabasePath = path;
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
    }

    /// <summary>Database file.</summary>
    public string DatabasePath { get; }

    /// <summary>Opens a connection with the required pragmas. The caller disposes it.</summary>
    public SqliteConnection OpenConnection()
    {
        ThrowIfDisposed();
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var pragmas = connection.CreateCommand();
            pragmas.CommandText = ConnectionPragmas;
            pragmas.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Applies every pending migration; returns how many scripts ran.</summary>
    public int Migrate() => Write(connection =>
    {
        var current = Convert.ToInt32(connection.Command("PRAGMA user_version").Scalar());
        var applied = 0;
        foreach (var (version, sql) in Migrations.OrderBy(m => m.Version))
        {
            if (version <= current)
            {
                continue;
            }

            using var transaction = connection.BeginTransaction();
            connection.Command(sql, transaction).Execute();
            connection.Command($"PRAGMA user_version = {version}", transaction).Execute();
            transaction.Commit();
            applied++;
        }

        return applied;
    });

    /// <summary>The stored schema version.</summary>
    public int GetVersion() => Read(connection => Convert.ToInt32(connection.Command("PRAGMA user_version").Scalar()));

    /// <summary>Runs <paramref name="work"/> on its own connection (no gate). Blocks the calling thread.</summary>
    public T Read<T>(Func<SqliteConnection, T> work)
    {
        using var connection = OpenConnection();
        return work(connection);
    }

    /// <summary>Runs <paramref name="work"/> while holding the single write gate. Blocks the calling thread.</summary>
    public T Write<T>(Func<SqliteConnection, T> work)
    {
        _writeGate.Wait();
        try
        {
            using var connection = OpenConnection();
            return work(connection);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Background-thread variant of <see cref="Read{T}"/>.</summary>
    public Task<T> ReadAsync<T>(Func<SqliteConnection, T> work, CancellationToken ct = default) =>
        Task.Run(() => Read(work), ct);

    /// <summary>Background-thread variant of <see cref="Write{T}"/>.</summary>
    public Task<T> WriteAsync<T>(Func<SqliteConnection, T> work, CancellationToken ct = default) =>
        Task.Run(() => Write(work), ct);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SqliteConnection.ClearAllPools();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

/// <summary>Small helpers that keep SQL call sites short. Values are always passed as parameters.</summary>
internal static class SqlExtensions
{
    public static SqliteCommand Command(this SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    public static SqliteCommand With(this SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public static int Execute(this SqliteCommand command)
    {
        using (command)
        {
            return command.ExecuteNonQuery();
        }
    }

    public static object? Scalar(this SqliteCommand command)
    {
        using (command)
        {
            var value = command.ExecuteScalar();
            return value is DBNull ? null : value;
        }
    }

    public static bool IsConstraintViolation(this SqliteException exception) => exception.SqliteErrorCode == 19;
}
