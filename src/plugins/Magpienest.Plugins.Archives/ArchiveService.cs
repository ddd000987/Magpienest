using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Magpienest.Sdk;
using Magpienest.Sdk.Archives;

namespace Magpienest.Plugins.Archives;

/// <summary>Opens archives; solid ones are extracted once into the cache and shared between open handles.</summary>
internal sealed class ArchiveService : IArchiveService
{
    /// <summary>Entries above this size are refused (zip-bomb protection).</summary>
    public const long MaxEntrySize = 512L * 1024 * 1024;

    /// <summary>Archives with more entries are refused.</summary>
    public const int MaxEntries = 50_000;

    private readonly string _cacheRoot;
    private readonly IArchiveBackendFactory _factory;
    private readonly ILog _log;
    private readonly CancellationToken _stopping;
    private readonly object _lock = new();
    private readonly Dictionary<string, SolidExtraction> _extractions = new(StringComparer.OrdinalIgnoreCase);

    public ArchiveService(string cacheRoot, IArchiveBackendFactory factory, ILog log, CancellationToken stopping)
    {
        _cacheRoot = cacheRoot;
        _factory = factory;
        _log = log;
        _stopping = stopping;
    }

    public IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".zip", ".cbz", ".rar", ".cbr", ".7z", ".cb7" };

    public bool IsSupported(string path) => SupportedExtensions.Contains(Path.GetExtension(path));

    public Task<IArchive> OpenAsync(string path, CancellationToken ct = default) => Task.Run(() => Open(path, ct), ct);

    /// <summary>Whether an extraction folder is in use (cache hygiene skips it).</summary>
    public bool IsInUse(string folderName)
    {
        lock (_lock)
        {
            return _extractions.ContainsKey(folderName);
        }
    }

    private IArchive Open(string path, CancellationToken ct)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("The archive does not exist.", path);
        }

        var backend = _factory.Open(path);
        List<ArchiveEntry> entries;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (backend.IsEncrypted || backend.Entries.Any(e => e.IsEncrypted))
            {
                throw new ArchiveEncryptedException("The archive is password-protected.");
            }

            if (backend.Entries.Count > MaxEntries)
            {
                throw new IOException($"The archive has more than {MaxEntries} entries.");
            }

            entries = [.. backend.Entries.Select((e, i) => new ArchiveEntry(i, EntryNames.NormaliseKey(e.Key), e.Size))];
            if (!backend.IsSolid)
            {
                return new RandomAccessArchive(path, entries, backend);
            }
        }
        catch
        {
            backend.Dispose();
            throw;
        }

        var extraction = Acquire(file, entries, backend);
        return new SolidArchive(path, entries, extraction, Release);
    }

    /// <summary>Joins a running or completed extraction of the same file, or starts one (taking <paramref name="backend"/>).</summary>
    private SolidExtraction Acquire(FileInfo file, IReadOnlyList<ArchiveEntry> entries, IArchiveBackend backend)
    {
        var key = Hash(file);
        while (true)
        {
            SolidExtraction? stale = null;
            lock (_lock)
            {
                if (_extractions.TryGetValue(key, out var existing))
                {
                    if (!existing.IsCancelled)
                    {
                        existing.RefCount++;
                        backend.Dispose();
                        return existing;
                    }

                    stale = existing;
                }
                else
                {
                    var folder = Path.Combine(_cacheRoot, key);
                    var created = SolidExtraction.Start(key, folder, entries, backend, _log, _stopping);
                    created.RefCount = 1;
                    _extractions[key] = created;
                    return created;
                }
            }

            // A cancelled pass still writing into the folder: wait until it stops, then start over.
            try
            {
                stale!.Pass.Wait();
            }
            catch (AggregateException)
            {
                // Its failure was reported to its own readers.
            }

            lock (_lock)
            {
                if (_extractions.TryGetValue(key, out var current) && ReferenceEquals(current, stale))
                {
                    _extractions.Remove(key);
                }
            }
        }
    }

    private void Release(SolidExtraction extraction)
    {
        lock (_lock)
        {
            if (--extraction.RefCount > 0)
            {
                return;
            }

            if (extraction.Pass.IsCompleted)
            {
                _extractions.Remove(extraction.Key);
                return;
            }

            extraction.Cancel();
        }

        // Forget it once the cancelled pass has stopped (unless a newer handle replaced it meanwhile).
        extraction.Pass.ContinueWith(_ =>
        {
            lock (_lock)
            {
                if (_extractions.TryGetValue(extraction.Key, out var current) && ReferenceEquals(current, extraction)
                    && extraction.RefCount == 0)
                {
                    _extractions.Remove(extraction.Key);
                }
            }
        }, TaskScheduler.Default);
    }

    /// <summary>SHA-1 of path, length and modification time: a changed file gets a new folder.</summary>
    internal static string Hash(FileInfo file)
    {
        var text = string.Create(CultureInfo.InvariantCulture,
            $"{file.FullName.ToUpperInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");
        return Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(text)));
    }
}

/// <summary>Non-solid archive: each entry is decompressed on demand, one at a time.</summary>
internal sealed class RandomAccessArchive(string path, IReadOnlyList<ArchiveEntry> entries, IArchiveBackend backend) : IArchive
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public string Path { get; } = path;

    public IReadOnlyList<ArchiveEntry> Entries { get; } = entries;

    public async Task<Stream> OpenEntryAsync(ArchiveEntry entry, CancellationToken ct = default)
    {
        EntryChecks.Validate(Entries, entry);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() =>
            {
                var buffer = new MemoryStream(entry.Size is > 0 and <= int.MaxValue ? (int)entry.Size : 0);
                backend.CopyEntry(entry.Index, buffer, ct);
                buffer.Position = 0;
                return (Stream)buffer;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_disposed)
            {
                _disposed = true;
                backend.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>Solid archive: entries are read from the shared extraction folder as the background pass reaches them.</summary>
internal sealed class SolidArchive(string path, IReadOnlyList<ArchiveEntry> entries, SolidExtraction extraction,
                                   Action<SolidExtraction> release) : IArchive
{
    private int _disposed;

    public string Path { get; } = path;

    public IReadOnlyList<ArchiveEntry> Entries { get; } = entries;

    public async Task<Stream> OpenEntryAsync(ArchiveEntry entry, CancellationToken ct = default)
    {
        EntryChecks.Validate(Entries, entry);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await extraction.WhenReady(entry.Index, ct).ConfigureAwait(false);
        var file = extraction.FilePath(entry.Index);
        if (!File.Exists(file))
        {
            throw new IOException($"The entry could not be extracted: {entry.Key}");
        }

        return new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, useAsync: true);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            release(extraction);
        }

        return ValueTask.CompletedTask;
    }
}

internal static class EntryChecks
{
    public static void Validate(IReadOnlyList<ArchiveEntry> entries, ArchiveEntry entry)
    {
        if (entry.Index < 0 || entry.Index >= entries.Count || entries[entry.Index] != entry)
        {
            throw new ArgumentException("The entry does not belong to this archive.", nameof(entry));
        }

        if (entry.Size > ArchiveService.MaxEntrySize)
        {
            throw new IOException($"The entry is too large to open: {entry.Key}");
        }
    }
}

/// <summary>One background pass that writes every entry of a solid archive to &lt;cache&gt;/archives/&lt;hash&gt;/&lt;index&gt;.</summary>
internal sealed class SolidExtraction
{
    public const string DoneMarker = ".done";

    private readonly TaskCompletionSource[] _ready;
    private readonly CancellationTokenSource _cts;

    private SolidExtraction(string key, string folder, int count, CancellationToken stopping)
    {
        Key = key;
        Folder = folder;
        _ready = new TaskCompletionSource[count];
        for (var i = 0; i < count; i++)
        {
            _ready[i] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
    }

    public string Key { get; }

    public string Folder { get; }

    /// <summary>Guarded by the service lock.</summary>
    public int RefCount { get; set; }

    public bool IsCancelled => _cts.IsCancellationRequested;

    public Task Pass { get; private set; } = Task.CompletedTask;

    public static SolidExtraction Start(string key, string folder, IReadOnlyList<ArchiveEntry> entries, IArchiveBackend backend,
                                        ILog log, CancellationToken stopping)
    {
        var extraction = new SolidExtraction(key, folder, entries.Count, stopping);
        if (File.Exists(Path.Combine(folder, DoneMarker)))
        {
            backend.Dispose();
            TryTouch(folder);
            foreach (var ready in extraction._ready)
            {
                ready.TrySetResult();
            }

            return extraction;
        }

        extraction.Pass = Task.Run(() => extraction.Run(entries, backend, log));
        return extraction;
    }

    public string FilePath(int index) => Path.Combine(Folder, index.ToString(CultureInfo.InvariantCulture));

    public Task WhenReady(int index, CancellationToken ct) => _ready[index].Task.WaitAsync(ct);

    public void Cancel() => _cts.Cancel();

    private void Run(IReadOnlyList<ArchiveEntry> entries, IArchiveBackend backend, ILog log)
    {
        var ct = _cts.Token;
        try
        {
            if (Directory.Exists(Folder))
            {
                Directory.Delete(Folder, recursive: true); // an earlier pass was interrupted
            }

            Directory.CreateDirectory(Folder);
            backend.ExtractAll((index, write) =>
            {
                ct.ThrowIfCancellationRequested();
                if (entries[index].Size > ArchiveService.MaxEntrySize)
                {
                    _ready[index].TrySetException(new IOException($"The entry is too large to open: {entries[index].Key}"));
                    return;
                }

                var target = FilePath(index);
                var part = target + ".part";
                using (var stream = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                {
                    write(stream);
                }

                File.Move(part, target, overwrite: true);
                _ready[index].TrySetResult();
            }, ct);

            foreach (var ready in _ready)
            {
                ready.TrySetResult(); // entries the pass never met: OpenEntryAsync reports the missing file
            }

            File.WriteAllText(Path.Combine(Folder, DoneMarker), "");
        }
        catch (OperationCanceledException)
        {
            foreach (var ready in _ready)
            {
                ready.TrySetCanceled(ct);
            }
        }
        catch (Exception ex)
        {
            log.Warn($"Extracting {Folder} failed.", ex);
            foreach (var ready in _ready)
            {
                ready.TrySetException(ex);
            }
        }
        finally
        {
            backend.Dispose();
        }
    }

    private static void TryTouch(string folder)
    {
        try
        {
            Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Deletes extraction folders unused for a week, then the oldest until the cache is small enough.</summary>
internal static class CacheHygiene
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    public const long MaxBytes = 2L * 1024 * 1024 * 1024;

    public static void Run(string root, Func<string, bool> isInUse, DateTime utcNow, long maxBytes, ILog log)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var folders = new List<(DirectoryInfo Dir, DateTime Used, long Bytes)>();
        foreach (var dir in new DirectoryInfo(root).EnumerateDirectories())
        {
            if (isInUse(dir.Name))
            {
                continue;
            }

            if (utcNow - dir.LastWriteTimeUtc > MaxAge)
            {
                TryDelete(dir, log);
                continue;
            }

            long bytes = 0;
            try
            {
                bytes = dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch (IOException)
            {
            }

            folders.Add((dir, dir.LastWriteTimeUtc, bytes));
        }

        var total = folders.Sum(f => f.Bytes);
        foreach (var folder in folders.OrderBy(f => f.Used))
        {
            if (total < maxBytes)
            {
                break;
            }

            if (TryDelete(folder.Dir, log))
            {
                total -= folder.Bytes;
            }
        }
    }

    private static bool TryDelete(DirectoryInfo dir, ILog log)
    {
        try
        {
            dir.Delete(recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"Could not delete the cache folder {dir.FullName}.", ex);
            return false;
        }
    }
}
