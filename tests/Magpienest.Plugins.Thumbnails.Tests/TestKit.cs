using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Magpienest.Plugins.Thumbnails;
using Magpienest.Sdk;
using Magpienest.Sdk.Imaging;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Thumbnails;
using Microsoft.Data.Sqlite;

namespace Magpienest.Plugins.Thumbnails.Tests;

/// <summary>Imaging service built on plain WPF codecs (the real Imaging plugin lives in another project).</summary>
internal sealed class FakeImaging : IImagingService
{
    public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>();

    public bool IsSupported(string pathOrExtension) => false;

    public Task<BitmapSource> DecodeAsync(Stream seekable, DecodeOptions options, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var decoder = BitmapDecoder.Create(seekable, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            return (BitmapSource)frame;
        }, ct);
    }

    public Task<ImageInfo?> ReadInfoAsync(Stream seekable, CancellationToken ct = default) => Task.FromResult<ImageInfo?>(null);

    public byte[] EncodeJpeg(BitmapSource source, int quality)
    {
        var flat = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(flat));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public void RegisterDecoder(IPluginContext owner, IImageDecoder decoder)
    {
    }
}

internal sealed class FakeLog : ILog
{
    public ConcurrentQueue<string> DebugMessages { get; } = new();

    public ConcurrentQueue<string> Problems { get; } = new();

    public void Debug(string message) => DebugMessages.Enqueue(message);

    public void Info(string message)
    {
    }

    public void Warn(string message, Exception? ex = null) => Problems.Enqueue("WARN " + message + " " + ex);

    public void Error(string message, Exception? ex = null) => Problems.Enqueue("ERROR " + message + " " + ex);
}

/// <summary>Records <see cref="IPluginContext.Track(System.Action)"/> calls; <see cref="Deactivate"/> runs them LIFO.</summary>
internal sealed class FakeContext : IPluginContext
{
    private readonly Stack<Action> _undo = new();

    public void Deactivate()
    {
        while (_undo.Count > 0)
        {
            _undo.Pop()();
        }
    }

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

    public void Track(IDisposable effect) => _undo.Push(effect.Dispose);

    public void Track(Action undo) => _undo.Push(undo);
}

internal sealed class CountingProvider : IThumbnailProvider
{
    private readonly object _lock = new();
    private readonly List<string> _order = [];

    public string Stamp { get; set; } = "s1";

    public int Width { get; set; } = 100;

    public int Height { get; set; } = 80;

    public PixelFormat Format { get; set; } = PixelFormats.Bgr32;

    /// <summary>Overrides the default "make a bitmap" behaviour.</summary>
    public Func<LibraryItem, CancellationToken, Task<BitmapSource?>>? Behaviour { get; set; }

    public int Calls
    {
        get
        {
            lock (_lock)
            {
                return _order.Count;
            }
        }
    }

    /// <summary>Paths in the order the provider was entered.</summary>
    public IReadOnlyList<string> Order
    {
        get
        {
            lock (_lock)
            {
                return _order.ToArray();
            }
        }
    }

    public int CallsFor(string path)
    {
        lock (_lock)
        {
            return _order.Count(p => p == path);
        }
    }

    public string GetStamp(LibraryItem item) => Stamp;

    public Task<BitmapSource?> CreateAsync(LibraryItem item, int maxPixelSize, CancellationToken ct)
    {
        lock (_lock)
        {
            _order.Add(item.Path);
        }

        return Behaviour is { } behaviour ? behaviour(item, ct) : Task.FromResult<BitmapSource?>(MakeBitmap(Width, Height, Format));
    }

    public static BitmapSource MakeBitmap(int width, int height, PixelFormat format)
    {
        var bytesPerPixel = format.BitsPerPixel / 8;
        var stride = width * bytesPerPixel;
        var pixels = new byte[stride * height];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i * 7);
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, format, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }
}

/// <summary>Temp folder plus services created in it; everything is disposed and deleted afterwards.</summary>
public abstract class ServiceTestBase : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "magpienest-tests", Guid.NewGuid().ToString("N"));
    private readonly List<ThumbnailService> _services = [];

    protected ServiceTestBase() => Directory.CreateDirectory(_root);

    internal FakeLog Log { get; } = new();

    internal string DbPath => Path.Combine(_root, "cache", "thumbnails.db");

    internal ThumbnailService CreateService(
        long memoryCapBytes = ThumbnailService.DefaultMemoryCapBytes,
        int? workerCount = null,
        TimeSpan? flushInterval = null,
        int flushBatch = 50)
    {
        var service = new ThumbnailService(DbPath, new FakeImaging(), Log, memoryCapBytes, workerCount, flushInterval, flushBatch);
        _services.Add(service);
        return service;
    }

    internal static FakeContext Register(ThumbnailService service, CountingProvider provider, string kind = "test")
    {
        var context = new FakeContext();
        service.RegisterProvider(context, kind, provider);
        return context;
    }

    internal static LibraryItem Item(string name, string kind = "test") => new()
    {
        Kind = kind,
        Path = @"C:\media\" + name,
        Directory = @"C:\media",
        Title = name,
    };

    internal int RowCount()
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM thumb";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    protected static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition was not reached in time.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    public void Dispose()
    {
        foreach (var service in _services)
        {
            service.Dispose();
        }

        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }
}
