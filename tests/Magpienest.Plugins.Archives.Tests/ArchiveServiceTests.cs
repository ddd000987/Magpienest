using System.IO.Compression;
using System.Text;
using Magpienest.Plugins.Archives;
using Magpienest.Sdk;
using Magpienest.Sdk.Archives;

namespace Magpienest.Plugins.Archives.Tests;

public sealed class ArchiveServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mn-arch-" + Guid.NewGuid().ToString("N"));
    private readonly string _cache;

    public ArchiveServiceTests()
    {
        _cache = Path.Combine(_dir, "cache");
        Directory.CreateDirectory(_cache);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ArchiveService RealService() => new(_cache, new SharpCompressBackendFactory(), new NullLog(), CancellationToken.None);

    private string MakeZip(params (string Name, string? Content, CompressionLevel Level)[] entries)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content, level) in entries)
        {
            var entry = zip.CreateEntry(name, level);
            if (content is not null)
            {
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        return path;
    }

    private static async Task<string> ReadAsync(IArchive archive, ArchiveEntry entry)
    {
        await using var stream = await archive.OpenEntryAsync(entry, TestContext.Current.CancellationToken);
        Assert.True(stream.CanSeek);
        Assert.Equal(0, stream.Position);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void SupportedExtensions()
    {
        var service = RealService();
        Assert.True(service.IsSupported(@"C:\a\b.CBZ"));
        Assert.True(service.IsSupported(".7z"));
        Assert.True(service.IsSupported("x.cbr"));
        Assert.False(service.IsSupported("x.jpg"));
        Assert.Equal(6, service.SupportedExtensions.Count);
    }

    [Fact]
    public async Task ZipStoredDeflatedNestedAndNonAscii()
    {
        var path = MakeZip(
            ("01.txt", "stored", CompressionLevel.NoCompression),
            ("ch1/sub/02.txt", "deflated deflated deflated", CompressionLevel.Optimal),
            ("empty/", null, CompressionLevel.NoCompression),
            ("名前/画像 03.txt", "日本語", CompressionLevel.Optimal));

        await using var archive = await RealService().OpenAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(path, archive.Path);
        Assert.Equal(["01.txt", "ch1/sub/02.txt", "名前/画像 03.txt"], archive.Entries.Select(e => e.Key));
        Assert.Equal([0, 1, 2], archive.Entries.Select(e => e.Index));
        Assert.Equal(6, archive.Entries[0].Size);
        Assert.Equal("stored", await ReadAsync(archive, archive.Entries[0]));
        Assert.Equal("deflated deflated deflated", await ReadAsync(archive, archive.Entries[1]));
        Assert.Equal("日本語", await ReadAsync(archive, archive.Entries[2]));
    }

    [Fact]
    public async Task ConcurrentReadsReturnTheRightContent()
    {
        var entries = Enumerable.Range(0, 20)
            .Select(i => ($"p{i:00}.txt", (string?)new string((char)('a' + i), 1000 + i), CompressionLevel.Optimal))
            .ToArray();
        var path = MakeZip(entries);

        await using var archive = await RealService().OpenAsync(path, TestContext.Current.CancellationToken);
        var reads = Enumerable.Range(0, 100).Select(n => Task.Run(async () =>
        {
            var entry = archive.Entries[n % 20];
            var text = await ReadAsync(archive, entry);
            return (entry.Index, text);
        })).ToArray();

        foreach (var (index, text) in await Task.WhenAll(reads))
        {
            Assert.Equal(entries[index].Item2, text);
        }
    }

    [Fact]
    public async Task EncryptedZipThrows()
    {
        var path = MakeZip(("a.txt", "secret", CompressionLevel.NoCompression));
        SetEncryptedFlag(path);

        await Assert.ThrowsAsync<ArchiveEncryptedException>(
            () => RealService().OpenAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CorruptFileThrowsIOException()
    {
        var path = Path.Combine(_dir, "broken.zip");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5, 6, 7, 8], TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<IOException>(() => RealService().OpenAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ForeignEntryIsRejected()
    {
        var path = MakeZip(("a.txt", "a", CompressionLevel.NoCompression));
        await using var archive = await RealService().OpenAsync(path, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(
            () => archive.OpenEntryAsync(new ArchiveEntry(0, "other.txt", 1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void LegacyNamesFallBackToTheCodePage()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var shiftJis = Encoding.GetEncoding(932);
        var legacy = shiftJis.GetBytes("名前.jpg");
        var utf8 = Encoding.UTF8.GetBytes("名前.jpg");

        Assert.Equal("名前.jpg", EntryNames.Decode(legacy, 0, legacy.Length, shiftJis));
        Assert.Equal("名前.jpg", EntryNames.Decode(utf8, 0, utf8.Length, shiftJis));
        Assert.Equal("a/b/c.jpg", EntryNames.NormaliseKey(@"\a\b\c.jpg"));
    }

    [Fact]
    public async Task TooManyEntriesIsRefused()
    {
        var file = DummyFile();
        var fake = new FakeBackend(false, Enumerable.Range(0, ArchiveService.MaxEntries + 1).Select(i => ($"{i}.jpg", "x")));
        var service = new ArchiveService(_cache, new FakeFactory(() => fake), new NullLog(), CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() => service.OpenAsync(file, TestContext.Current.CancellationToken));
        Assert.True(fake.Disposed);
    }

    [Fact]
    public async Task SolidArchiveIsExtractedOnceAndShared()
    {
        var file = DummyFile();
        var backends = new List<FakeBackend>();
        var gate = new SemaphoreSlim(0);
        var service = new ArchiveService(_cache, new FakeFactory(() =>
        {
            var backend = new FakeBackend(true, [("1.jpg", "one"), ("dir\\2.jpg", "two"), ("3.jpg", "three")]) { Gate = gate };
            backends.Add(backend);
            return backend;
        }), new NullLog(), CancellationToken.None);
        var ct = TestContext.Current.CancellationToken;

        var first = await service.OpenAsync(file, ct);
        var second = await service.OpenAsync(file, ct);
        Assert.Equal(["1.jpg", "dir/2.jpg", "3.jpg"], first.Entries.Select(e => e.Key));

        // Entry 1 is not available until the pass has written it.
        var pending = ReadAsync(second, second.Entries[1]);
        await Task.Delay(50, ct);
        Assert.False(pending.IsCompleted);
        gate.Release(3);
        Assert.Equal("two", await pending);
        Assert.Equal("one", await ReadAsync(first, first.Entries[0]));
        Assert.Equal("three", await ReadAsync(first, first.Entries[2]));

        Assert.Equal(1, backends.Sum(b => b.ExtractCalls)); // one pass shared by both handles
        Assert.All(backends, b => Assert.True(b.Disposed || b.ExtractCalls == 1));
        await first.DisposeAsync();
        await second.DisposeAsync();
        await WaitFor(() => backends.All(b => b.Disposed));

        var folder = Path.Combine(_cache, ArchiveService.Hash(new FileInfo(file)));
        Assert.True(File.Exists(Path.Combine(folder, SolidExtraction.DoneMarker)));

        // Reopening reuses the completed folder: no new pass.
        await using var third = await service.OpenAsync(file, ct);
        Assert.Equal("three", await ReadAsync(third, third.Entries[2]));
        Assert.Equal(1, backends.Sum(b => b.ExtractCalls));
    }

    [Fact]
    public async Task SolidOversizedEntryThrowsButOthersWork()
    {
        var file = DummyFile();
        var fake = new FakeBackend(true, [("1.jpg", "one"), ("2.jpg", "two")]) { SizeOverride = (1, ArchiveService.MaxEntrySize + 1) };
        var service = new ArchiveService(_cache, new FakeFactory(() => fake), new NullLog(), CancellationToken.None);
        var ct = TestContext.Current.CancellationToken;

        await using var archive = await service.OpenAsync(file, ct);
        Assert.Equal("one", await ReadAsync(archive, archive.Entries[0]));
        await Assert.ThrowsAsync<IOException>(() => archive.OpenEntryAsync(archive.Entries[1], ct));
    }

    [Fact]
    public async Task ClosingEveryHandleCancelsTheSolidPass()
    {
        var file = DummyFile();
        var gate = new SemaphoreSlim(0);
        var fake = new FakeBackend(true, [("1.jpg", "one"), ("2.jpg", "two")]) { Gate = gate };
        var service = new ArchiveService(_cache, new FakeFactory(() => fake), new NullLog(), CancellationToken.None);
        var ct = TestContext.Current.CancellationToken;

        var archive = await service.OpenAsync(file, ct);
        var pending = archive.OpenEntryAsync(archive.Entries[1], ct);
        await archive.DisposeAsync();
        gate.Release(2);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await WaitFor(() => fake.Disposed);
        var folder = Path.Combine(_cache, ArchiveService.Hash(new FileInfo(file)));
        Assert.False(File.Exists(Path.Combine(folder, SolidExtraction.DoneMarker)));
    }

    [Fact]
    public void HygieneDeletesOldThenOldestOverBudget()
    {
        var now = DateTime.UtcNow;
        var old = MakeCacheFolder("old", now.AddDays(-8), 10);
        var a = MakeCacheFolder("a", now.AddHours(-3), 100);
        var b = MakeCacheFolder("b", now.AddHours(-2), 100);
        var c = MakeCacheFolder("c", now.AddHours(-1), 100);
        var busy = MakeCacheFolder("busy", now.AddDays(-30), 100);

        CacheHygiene.Run(_cache, name => name == "busy", now, maxBytes: 250, new NullLog());

        Assert.False(Directory.Exists(old));
        Assert.False(Directory.Exists(a)); // oldest goes until the total is below the budget
        Assert.True(Directory.Exists(b));
        Assert.True(Directory.Exists(c));
        Assert.True(Directory.Exists(busy));
    }

    private string MakeCacheFolder(string name, DateTime used, int bytes)
    {
        var dir = Path.Combine(_cache, name);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "0"), new byte[bytes]);
        Directory.SetLastWriteTimeUtc(dir, used);
        return dir;
    }

    private string DummyFile()
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".7z");
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    /// <summary>Sets general-purpose bit 0 (encrypted) in every local and central header of a stored zip.</summary>
    private static void SetEncryptedFlag(string path)
    {
        var bytes = File.ReadAllBytes(path);
        for (var i = 0; i + 4 <= bytes.Length; i++)
        {
            var signature = BitConverter.ToUInt32(bytes, i);
            if (signature == 0x04034b50)
            {
                bytes[i + 6] |= 1;
            }
            else if (signature == 0x02014b50)
            {
                bytes[i + 8] |= 1;
            }
        }

        File.WriteAllBytes(path, bytes);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class FakeFactory(Func<IArchiveBackend> create) : IArchiveBackendFactory
    {
        public IArchiveBackend Open(string path) => create();
    }

    private sealed class FakeBackend : IArchiveBackend
    {
        private readonly List<(string Key, byte[] Data)> _items;

        public FakeBackend(bool solid, IEnumerable<(string Key, string Content)> items)
        {
            IsSolid = solid;
            _items = [.. items.Select(i => (i.Key, Encoding.UTF8.GetBytes(i.Content)))];
        }

        public SemaphoreSlim? Gate { get; init; }

        public (int Index, long Size)? SizeOverride { get; init; }

        public int ExtractCalls { get; private set; }

        public bool Disposed { get; private set; }

        public bool IsSolid { get; }

        public bool IsEncrypted => false;

        public IReadOnlyList<BackendEntry> Entries =>
            [.. _items.Select((item, i) => new BackendEntry(item.Key,
                SizeOverride is { } o && o.Index == i ? o.Size : item.Data.Length, false))];

        public void CopyEntry(int index, Stream destination, CancellationToken ct) => destination.Write(_items[index].Data);

        public void ExtractAll(Action<int, Action<Stream>> onEntry, CancellationToken ct)
        {
            ExtractCalls++;
            for (var i = 0; i < _items.Count; i++)
            {
                Gate?.Wait(ct);
                var data = _items[i].Data;
                onEntry(i, s => s.Write(data));
            }
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class NullLog : ILog
    {
        public void Debug(string message) { }

        public void Info(string message) { }

        public void Warn(string message, Exception? ex = null) { }

        public void Error(string message, Exception? ex = null) { }
    }
}
