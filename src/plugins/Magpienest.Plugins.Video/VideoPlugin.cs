using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Magpienest.Sdk;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Media;
using Magpienest.Sdk.Settings;
using Magpienest.Sdk.Shell;
using Magpienest.Sdk.Thumbnails;

namespace Magpienest.Plugins.Video;

/// <summary>The "Videos" page: video files in library folders, with thumbnails, durations and a player.</summary>
[Requires(typeof(IShellService), typeof(ISettingsService), typeof(ILibraryService),
          typeof(ILibraryBrowserFactory), typeof(IThumbnailService), typeof(IMediaEngine))]
public sealed class VideoPlugin : Plugin
{
    /// <summary>Kind and page id.</summary>
    public const string Kind = "video";

    /// <inheritdoc />
    public override void Activate(IPluginContext ctx)
    {
        var shell = ctx.Get<IShellService>();
        var library = ctx.Get<ILibraryService>();
        var browsers = ctx.Get<ILibraryBrowserFactory>();
        var thumbnails = ctx.Get<IThumbnailService>();
        var engine = ctx.Get<IMediaEngine>();
        var settings = new VideoSettings(ctx.Config);
        var prober = new DurationProber(engine, library, ctx.Log, ctx.Deactivating);
        var playback = new VideoPlayback(ctx, engine, library, shell, settings);
        var videos = new VideoItems(library, playback, prober, settings, ctx.Log);

        library.RegisterKind(ctx, new ItemKindDescriptor
        {
            Id = Kind,
            DisplayNameKey = "Video.Kind",
            Scanner = new VideoScanner(settings),
        });
        thumbnails.RegisterProvider(ctx, Kind, new VideoThumbnails(thumbnails, engine));

        // After a scan, probe the durations of the new videos in the background.
        ctx.On<LibraryChanged>(e =>
        {
            if (e.Kind == Kind && e.Type is LibraryChangeType.Added or LibraryChangeType.Reset)
            {
                _ = prober.EnqueueUnprobedAsync();
            }
        });

        shell.AddPage(ctx, new PageDescriptor
        {
            Id = Kind,
            TitleKey = "Video.Page.Title",
            Glyph = "",
            Order = 120,
            CreateView = () => browsers.CreateBrowser(ctx, new BrowserOptions
            {
                Kind = Kind,
                OpenItem = playback.OpenAsync,
                TileAspectRatio = 16.0 / 9,
                ThumbnailStretch = Stretch.UniformToFill,
                ShowStatus = true,
                Sorts = [ItemSort.Title, ItemSort.AddedAt, ItemSort.LastOpenedAt, ItemSort.FileModified,
                         ItemSort.Rating, ItemSort.FileSize],
                DefaultSort = ItemSort.Title,
                Subtitle = videos.Subtitle,
                ProgressFraction = videos.Progress,
                Commands = videos.Commands,
            }),
        });

        ctx.Get<ISettingsService>().AddSection(ctx, new SettingsSection
        {
            Id = Kind,
            TitleKey = "Video.Settings.Title",
            Glyph = "",
            Order = 220,
            CreateView = () => new VideoSettingsView { DataContext = new VideoSettingsViewModel(settings) },
        });
    }
}

/// <summary>Tile texts and context-menu commands.</summary>
internal sealed class VideoItems(ILibraryService library, VideoPlayback playback, DurationProber prober,
                                 VideoSettings settings, ILog log)
{
    /// <summary>The tile's progress bar; none while "remember where I stopped" is off.</summary>
    public double? Progress(LibraryItem item) => settings.RememberPosition ? ProgressFraction(item) : null;

    // These commands belong to "remember where I stopped"; while it is off they are not shown.
    public IReadOnlyList<BrowserCommand> Commands =>
    [
        new BrowserCommand("Video.Command.PlayFromStart", "",
            items => playback.OpenAsync(items[0], fromStart: true),
            items => settings.RememberPosition && items.Count == 1 && !items[0].IsMissing),
        new BrowserCommand("Video.Command.MarkWatched", "",
            items => UpdateAllAsync(items, new ItemPatch { Status = ItemStatus.Completed, ProgressPosition = 0 }),
            items => settings.RememberPosition && items.Any(i => i.Status != ItemStatus.Completed)),
        new BrowserCommand("Video.Command.MarkUnwatched", "",
            items => UpdateAllAsync(items, new ItemPatch { Status = ItemStatus.None, ProgressPosition = 0 }),
            items => settings.RememberPosition && items.Any(i => i.Status != ItemStatus.None || i.ProgressPosition > 0)),
    ];

    /// <summary>The duration; a tile without one is queued for probing.</summary>
    public string? Subtitle(LibraryItem item)
    {
        if (item.ProgressTotal > 0)
        {
            return VideoFormat.Duration(TimeSpan.FromMilliseconds(item.ProgressTotal));
        }

        prober.Enqueue(item);
        return null;
    }

    /// <summary>Position / duration once the video was started.</summary>
    public static double? ProgressFraction(LibraryItem item) => item.ProgressTotal > 0 && item.ProgressPosition > 0
        ? Math.Clamp(item.ProgressPosition / (double)item.ProgressTotal, 0, 1)
        : null;

    private async Task UpdateAllAsync(IReadOnlyList<LibraryItem> items, ItemPatch patch)
    {
        foreach (var item in items)
        {
            try
            {
                await library.UpdateAsync(item.Id, patch);
            }
            catch (Exception ex)
            {
                log.Warn("Updating a video failed.", ex);
            }
        }
    }
}

/// <summary>Time formats shown to the user.</summary>
internal static class VideoFormat
{
    /// <summary>"1:05:09", "5:09"; hours are not wrapped at 24.</summary>
    public static string Duration(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        var hours = (int)value.TotalHours;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{value.Minutes:00}:{value.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{value.Minutes}:{value.Seconds:00}");
    }
}

/// <summary>The kind-specific extra JSON.</summary>
internal sealed class VideoExtra
{
    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>The stored video size, or null when it was never read.</summary>
    public static (int Width, int Height)? SizeOf(LibraryItem item)
    {
        var extra = item.GetExtra<VideoExtra>();
        return extra.Width > 0 && extra.Height > 0 ? (extra.Width, extra.Height) : null;
    }
}

/// <summary>Every file with a configured video extension below a folder (hidden and system files skipped).</summary>
internal sealed class VideoScanner(VideoSettings settings) : IItemScanner
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    public IEnumerable<ScannedEntry> Scan(string root, CancellationToken ct)
    {
        var extensions = settings.Extensions;
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", Options))
        {
            ct.ThrowIfCancellationRequested();
            if (extensions.Contains(file.Extension))
            {
                yield return new ScannedEntry(file.FullName, Path.GetFileNameWithoutExtension(file.Name), file.Length,
                                              new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
            }
        }
    }
}

/// <summary>Custom cover, else the Windows thumbnail, else a frame at 10 % from the media engine.</summary>
internal sealed class VideoThumbnails(IThumbnailService thumbnails, IMediaEngine engine) : IThumbnailProvider
{
    public const double FramePosition = 0.10;

    public string GetStamp(LibraryItem item) => ThumbnailDefaults.DefaultStamp(item);

    public async Task<BitmapSource?> CreateAsync(LibraryItem item, int maxPixelSize, CancellationToken ct)
    {
        if (item.CoverPath is not null &&
            await thumbnails.GetShellImageAsync(item.CoverPath, maxPixelSize, iconOnly: false, ct) is { } cover)
        {
            return Fit(cover, maxPixelSize);
        }

        if (await thumbnails.GetShellImageAsync(item.Path, maxPixelSize, iconOnly: false, ct) is { } shell)
        {
            return Fit(shell, maxPixelSize);
        }

        return await engine.CaptureFrameAsync(item.Path, FramePosition, maxPixelSize, ct);
    }

    /// <summary>The shell may return a larger image than asked for; scale it down.</summary>
    internal static BitmapSource Fit(BitmapSource source, int maxPixelSize)
    {
        var longSide = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longSide <= maxPixelSize)
        {
            return source;
        }

        var scale = maxPixelSize / (double)longSide;
        var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }
}

/// <summary>Probes durations one at a time in the background and stores them (ProgressTotal, ms).</summary>
internal sealed class DurationProber
{
    private readonly IMediaEngine _engine;
    private readonly ILibraryService _library;
    private readonly ILog _log;
    private readonly CancellationToken _ct;
    private readonly Channel<LibraryItem> _queue = Channel.CreateUnbounded<LibraryItem>(new() { SingleReader = true });
    private readonly ConcurrentDictionary<long, byte> _seen = new();
    private int _sweeping;

    public DurationProber(IMediaEngine engine, ILibraryService library, ILog log, CancellationToken ct)
    {
        _engine = engine;
        _library = library;
        _log = log;
        _ct = ct;
        Completion = Task.Run(RunAsync, ct);
    }

    /// <summary>The worker; ends when the plugin deactivates.</summary>
    internal Task Completion { get; }

    /// <summary>Queues an item without a duration or size; each item is tried at most once per session.</summary>
    public void Enqueue(LibraryItem item)
    {
        if ((item.ProgressTotal <= 0 || VideoExtra.SizeOf(item) is null) && !item.IsMissing && _seen.TryAdd(item.Id, 0))
        {
            _queue.Writer.TryWrite(item);
        }
    }

    /// <summary>Queues every video without a duration (after a scan).</summary>
    public async Task EnqueueUnprobedAsync()
    {
        if (Interlocked.Exchange(ref _sweeping, 1) == 1)
        {
            return;
        }

        try
        {
            var items = await _library.QueryAsync(new ItemQuery { Kind = VideoPlugin.Kind }, _ct);
            foreach (var item in items)
            {
                Enqueue(item);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Warn("Listing videos to probe failed.", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _sweeping, 0);
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(_ct))
            {
                try
                {
                    var info = await _engine.ProbeAsync(item.Path, _ct);
                    if (info is { Duration.Ticks: > 0 })
                    {
                        await _library.UpdateAsync(item.Id, new ItemPatch
                        {
                            ProgressTotal = (long)info.Duration.TotalMilliseconds,
                            Extra = LibraryItemExtensions.SerializeExtra(new VideoExtra
                            {
                                Width = info.VideoWidth,
                                Height = info.VideoHeight,
                            }),
                        });
                    }
                }
                catch (OperationCanceledException) when (_ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log.Debug($"Probing {item.Path} failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
