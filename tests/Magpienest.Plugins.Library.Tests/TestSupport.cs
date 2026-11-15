using System.Diagnostics.CodeAnalysis;
using Magpienest.Plugins.Library;
using Magpienest.Sdk;
using Magpienest.Sdk.Library;
using Microsoft.Data.Sqlite;

namespace Magpienest.Plugins.Library.Tests;

/// <summary>A library over a temp database file; everything is deleted on dispose.</summary>
internal sealed class TestLibrary : IDisposable
{
    private readonly object _lock = new();
    private readonly List<LibraryChanged> _events = [];
    private readonly List<string> _notices = [];

    public TestLibrary()
    {
        Root = Path.Combine(Path.GetTempPath(), "magpienest-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        MediaDirectory = Path.Combine(Root, "library", "media");
        DatabasePath = Path.Combine(Root, "library", "library.db");
        Service = new LibraryService(DatabasePath, MediaDirectory, Record, Notice);
    }

    public string Root { get; }

    public string MediaDirectory { get; }

    public string DatabasePath { get; }

    public LibraryService Service { get; }

    public FakeContext Context { get; } = new();

    public IReadOnlyList<LibraryChanged> Events
    {
        get
        {
            lock (_lock)
            {
                return [.. _events];
            }
        }
    }

    public IReadOnlyList<string> Notices
    {
        get
        {
            lock (_lock)
            {
                return [.. _notices];
            }
        }
    }

    /// <summary>A fresh folder inside the temp root (not inside the library folder).</summary>
    public string NewFolder(string name)
    {
        var path = Path.Combine(Root, "files", name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void RegisterScanner(string kind, IItemScanner scanner) =>
        Service.RegisterKind(Context, new ItemKindDescriptor { Id = kind, DisplayNameKey = "Test.Kind", Scanner = scanner });

    /// <summary>Runs raw SQL on a new connection.</summary>
    public async Task<int> ExecAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await Service.Ready;
        return await Service.Database.WriteAsync(connection =>
        {
            using var command = connection.Command(sql);
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            return command.ExecuteNonQuery();
        });
    }

    public async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await Service.Ready;
        return await Service.Database.ReadAsync(connection =>
        {
            using var command = connection.Command(sql);
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
        });
    }

    public async Task<LibraryItem> AddAsync(string kind, string path, string? title = null) =>
        await Service.AddAsync(new NewItem { Kind = kind, Path = path, Title = title ?? Path.GetFileNameWithoutExtension(path) });

    public void Dispose()
    {
        Context.Dispose();
        Service.Dispose();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(Root, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }

    private void Record(LibraryChanged evt)
    {
        lock (_lock)
        {
            _events.Add(evt);
        }
    }

    private void Notice(string message)
    {
        lock (_lock)
        {
            _notices.Add(message);
        }
    }
}

/// <summary>Scanner that returns whatever the test put in <see cref="Entries"/>.</summary>
internal sealed class FakeScanner : IItemScanner
{
    private int _scans;

    public List<ScannedEntry> Entries { get; } = [];

    /// <summary>Called with the index of the entry that is about to be yielded.</summary>
    public Action<int>? BeforeYield { get; set; }

    /// <summary>Called when a scan starts; may block.</summary>
    public Action? OnStart { get; set; }

    public int ScanCount => Volatile.Read(ref _scans);

    public IEnumerable<ScannedEntry> Scan(string root, CancellationToken ct)
    {
        Interlocked.Increment(ref _scans);
        OnStart?.Invoke();
        var snapshot = Entries.ToList();
        for (var i = 0; i < snapshot.Count; i++)
        {
            BeforeYield?.Invoke(i);
            yield return snapshot[i];
        }
    }

    public static ScannedEntry Entry(string path, long size = 100, int mtimeSeconds = 1000, string? title = null) =>
        new(path, title ?? Path.GetFileNameWithoutExtension(path), size, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000 + mtimeSeconds));
}

/// <summary>Real recursive file scanner, like the kind plugins will have.</summary>
internal sealed class FolderScanner : IItemScanner
{
    public IEnumerable<ScannedEntry> Scan(string root, CancellationToken ct)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
            {
                continue;
            }

            yield return new ScannedEntry(file, Path.GetFileNameWithoutExtension(file), info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
        }
    }
}

/// <summary>Only <see cref="Track(Action)"/> / <see cref="Track(IDisposable)"/> work; disposing runs the effects LIFO.</summary>
internal sealed class FakeContext : IPluginContext, IDisposable
{
    private readonly Stack<Action> _effects = new();

    public PluginManifest Manifest => throw new NotSupportedException();

    public string PluginDirectory => throw new NotSupportedException();

    public string DataDirectory => throw new NotSupportedException();

    public IAppEnvironment Environment => throw new NotSupportedException();

    public ILog Log => throw new NotSupportedException();

    public IPluginConfig Config => throw new NotSupportedException();

    public IUiDispatcher Ui => throw new NotSupportedException();

    public CancellationToken Deactivating => CancellationToken.None;

    public T Get<T>() where T : class => throw new NotSupportedException();

    public bool TryGet<T>([NotNullWhen(true)] out T? service) where T : class => throw new NotSupportedException();

    public void Provide<T>(T implementation) where T : class => throw new NotSupportedException();

    public void On<TEvent>(Action<TEvent> handler) => throw new NotSupportedException();

    public void Emit<TEvent>(TEvent evt) => throw new NotSupportedException();

    public void Track(IDisposable effect) => _effects.Push(effect.Dispose);

    public void Track(Action undo) => _effects.Push(undo);

    public void Dispose()
    {
        while (_effects.Count > 0)
        {
            _effects.Pop()();
        }
    }
}
