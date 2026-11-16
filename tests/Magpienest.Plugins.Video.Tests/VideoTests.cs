using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Magpienest.Sdk;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Media;
using Magpienest.Sdk.Shell;

namespace Magpienest.Plugins.Video.Tests;

public sealed class VideoTests
{
    [Theory]
    [InlineData(0, 0, null)]            // never probed
    [InlineData(60_000, 2_000, null)]   // under 5 %
    [InlineData(60_000, 3_000, 3_000)]  // 5 %
    [InlineData(60_000, 30_000, 30_000)]
    [InlineData(60_000, 57_000, 57_000)] // 95 %
    [InlineData(60_000, 58_000, null)]  // over 95 %
    public void ResumeOnlyBetweenFiveAndNinetyFivePercent(long total, long position, int? expectedMs)
    {
        var item = Item(1) with { ProgressTotal = total, ProgressPosition = position };
        Assert.Equal(expectedMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null, WatchSession.StartAt(item));
    }

    [Fact]
    public void StatusGoesFromNoneToInProgressToCompletedAndUserStatusesStay()
    {
        var duration = TimeSpan.FromMinutes(10);
        var session = new WatchSession(ItemStatus.None);
        var first = session.ToPatch(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, ended: false);
        Assert.True(first.MarkOpened);
        Assert.Equal(ItemStatus.InProgress, first.Status);
        Assert.Null(first.ProgressTotal);

        var middle = session.ToPatch(TimeSpan.FromMinutes(5), duration, TimeSpan.FromSeconds(2.6), ended: false);
        Assert.False(middle.MarkOpened);
        Assert.Null(middle.Status);
        Assert.Equal(300_000, middle.ProgressPosition);
        Assert.Equal(600_000, middle.ProgressTotal);
        Assert.Equal(2, middle.AddTimeSpentSeconds);

        var nearEnd = session.ToPatch(TimeSpan.FromMinutes(9.6), duration, TimeSpan.FromSeconds(0.5), ended: false);
        Assert.Equal(ItemStatus.Completed, nearEnd.Status);
        Assert.Equal(1, nearEnd.AddTimeSpentSeconds);   // 0.6 carried over + 0.5

        var end = new WatchSession(ItemStatus.InProgress).ToPatch(duration, duration, TimeSpan.Zero, ended: true);
        Assert.Equal(ItemStatus.Completed, end.Status);
        Assert.Equal(0, end.ProgressPosition);

        var held = new WatchSession(ItemStatus.OnHold).ToPatch(duration, duration, TimeSpan.Zero, ended: true);
        Assert.Null(held.Status);
    }

    [Fact]
    public void ExtensionsAreParsedLeniently()
    {
        Assert.Equal([".mp4", ".mkv", ".avi"], VideoSettings.ParseExtensions("mp4, .MKV;*.avi  mp4"));
        Assert.Equal([".ts"], VideoSettings.ParseExtensions("ts a/b c:d e.f"));
        Assert.Empty(VideoSettings.ParseExtensions("  "));

        var config = new MemoryConfig();
        var settings = new VideoSettings(config);
        Assert.Contains(".mkv", settings.Extensions);
        settings.SetExtensions([".abc"]);
        Assert.Equal([".abc"], settings.ExtensionList);
        settings.SetExtensions([]);
        Assert.Equal(VideoSettings.DefaultExtensions.Length, settings.Extensions.Count);
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(65_000, "1:05")]
    [InlineData(3_909_000, "1:05:09")]
    [InlineData(90_000_000, "25:00:00")]
    public void DurationsAreShortAndHoursDoNotWrap(long ms, string expected) =>
        Assert.Equal(expected, VideoFormat.Duration(TimeSpan.FromMilliseconds(ms)));

    [Theory]
    [InlineData(1920, 1080, 1397, 790)]   // shrunk to 75 % of the work area
    [InlineData(1080, 1920, 449, 790)]    // portrait keeps its shape
    [InlineData(640, 360, 650, 370)]      // own size
    [InlineData(320, 240, 370, 280)]      // grown to fit the controls
    [InlineData(0, 0, 1290, 730)]         // unknown: 16:9
    public void PlayerWindowStartsAtTheVideoShape(int width, int height, double expectedWidth, double expectedHeight) =>
        Assert.Equal(new Size(expectedWidth, expectedHeight), PlayerWindow.InitialSize(width, height, new Size(1920, 1040)));

    [Fact]
    public void ProgressBarOnlyOnceStarted()
    {
        Assert.Null(VideoItems.ProgressFraction(Item(1) with { ProgressTotal = 100 }));
        Assert.Null(VideoItems.ProgressFraction(Item(1) with { ProgressPosition = 50 }));
        Assert.Equal(0.5, VideoItems.ProgressFraction(Item(1) with { ProgressTotal = 100, ProgressPosition = 50 }));
    }

    [Fact]
    public void ScannerFindsConfiguredExtensionsOnly()
    {
        var dir = Directory.CreateTempSubdirectory("magpienest-videos-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.MKV"), "");
            File.WriteAllText(Path.Combine(dir, "b.txt"), "");
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            File.WriteAllText(Path.Combine(dir, "sub", "c.mp4"), "");
            var found = new VideoScanner(new VideoSettings(new MemoryConfig()))
                .Scan(dir, TestContext.Current.CancellationToken).Select(e => e.Title).Order().ToList();
            Assert.Equal(["a", "c"], found);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LargeShellImagesAreScaledDown()
    {
        var big = BitmapSource.Create(800, 450, 96, 96, PixelFormats.Bgr32, null, new byte[800 * 450 * 4], 800 * 4);
        var fitted = VideoThumbnails.Fit(big, 384);
        Assert.Equal(384, fitted.PixelWidth);
        Assert.Equal(216, fitted.PixelHeight);
        var small = BitmapSource.Create(100, 50, 96, 96, PixelFormats.Bgr32, null, new byte[100 * 50 * 4], 100 * 4);
        Assert.Same(small, VideoThumbnails.Fit(small, 384));
    }

    [Fact]
    public async Task ProberStoresDurationAndSizeOnceAndSkipsKnownOrMissingItems()
    {
        var ct = TestContext.Current.CancellationToken;
        var library = new FakeLibrary();
        var engine = new FakeEngine { Info = new MediaInfo(TimeSpan.FromSeconds(90), 1920, 1080) };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var prober = new DurationProber(engine, library, new NullLog(), cts.Token);
        prober.Enqueue(Item(1));
        prober.Enqueue(Item(1));
        prober.Enqueue(Item(2) with { ProgressTotal = 5, Extra = """{"width":640,"height":360}""" });
        prober.Enqueue(Item(3) with { IsMissing = true });

        await WaitUntilAsync(() => library.Patches.Count >= 1, ct);
        await Task.Delay(100, ct);
        var (id, patch) = Assert.Single(library.Patches);
        Assert.Equal(1, id);
        Assert.Equal(90_000, patch.ProgressTotal);
        Assert.Contains("1920", patch.Extra);
        Assert.Equal(1, engine.Probes);

        // An older entry with a duration but no size is probed again to fill the size in.
        prober.Enqueue(Item(4) with { ProgressTotal = 5, Extra = "{}" });
        await WaitUntilAsync(() => library.Patches.Count >= 2, ct);
        Assert.Equal(4, library.Patches[^1].Id);
        Assert.Equal(new (int, int)?((640, 360)), VideoExtra.SizeOf(Item(2) with { Extra = """{"width":640,"height":360}""" }));
        Assert.Null(VideoExtra.SizeOf(Item(4)));
        await cts.CancelAsync();
    }

    [Fact]
    public void SessionResumesSavesAndMovesOnAtTheEndWhenAsked()
    {
        var library = new FakeLibrary();
        var config = new MemoryConfig();
        var settings = new VideoSettings(config) { ContinueWithNext = true, Volume = 40 };
        var player = new FakePlayer();
        var playlist = new[]
        {
            Item(1) with { ProgressTotal = 100_000, ProgressPosition = 50_000 },
            Item(2),
        };
        var session = new PlayerSession(player, playlist, 0, library, settings, new FakeShell(), new NullLog());
        Assert.Equal(40, player.Volume);

        session.Start(fromStart: false);
        Assert.Equal(("a1.mp4", TimeSpan.FromSeconds(50)), player.Opened[^1]);
        var opened = library.Patches[^1].Patch;
        Assert.True(opened.MarkOpened);
        Assert.Equal(50_000, opened.ProgressPosition);   // not reset to 0 before the player reports a position
        Assert.Equal(ItemStatus.InProgress, opened.Status);

        player.Duration = TimeSpan.FromSeconds(100);
        player.Position = TimeSpan.FromSeconds(100);
        player.RaiseEnd();
        Assert.Contains(library.Patches, p => p.Id == 1 && p.Patch.Status == ItemStatus.Completed && p.Patch.ProgressPosition == 0);
        Assert.Equal(1, session.Index);
        Assert.Equal(("a2.mp4", (TimeSpan?)null), player.Opened[^1]);

        session.Dispose();
        Assert.True(player.Disposed);
    }

    [Fact]
    public void WithoutRememberingEveryVideoStartsAtTheBeginningAndOnlyOpeningIsRecorded()
    {
        var library = new FakeLibrary();
        var settings = new VideoSettings(new MemoryConfig()) { RememberPosition = false };
        var player = new FakePlayer();
        var item = Item(1) with { ProgressTotal = 100_000, ProgressPosition = 50_000 };
        var session = new PlayerSession(player, [item], 0, library, settings, new FakeShell(), new NullLog());

        session.Start(fromStart: false);
        Assert.Equal(("a1.mp4", (TimeSpan?)null), player.Opened[^1]);
        var opened = Assert.Single(library.Patches).Patch;
        Assert.Equal(new ItemPatch { MarkOpened = true }, opened);

        player.Duration = TimeSpan.FromSeconds(100);
        player.Position = TimeSpan.FromSeconds(60);
        player.IsPlaying = true;
        session.Tick();
        player.Position = TimeSpan.FromSeconds(100);
        player.RaiseEnd();
        session.Dispose();
        Assert.Single(library.Patches);

        var videos = new VideoItems(library, null!, null!, settings, new NullLog());
        Assert.Null(videos.Progress(item));
        Assert.All(videos.Commands, c => Assert.False(c.CanExecute!([item])));
        settings.RememberPosition = true;
        Assert.Equal(0.5, videos.Progress(item));
        Assert.Contains(videos.Commands, c => c.CanExecute!([item]));
    }

    private static LibraryItem Item(long id) => new()
    {
        Id = id,
        Kind = VideoPlugin.Kind,
        Path = $"a{id}.mp4",
        Directory = "",
        Title = $"Video {id}",
    };

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, ct);
        }
    }

    private sealed class FakeEngine : IMediaEngine
    {
        public MediaInfo? Info { get; init; }

        public int Probes { get; private set; }

        public string Name => "Fake";

        public IMediaPlayer CreatePlayer() => new FakePlayer();

        public Task<MediaInfo?> ProbeAsync(string path, CancellationToken ct = default)
        {
            Probes++;
            return Task.FromResult(Info);
        }

        public Task<BitmapSource?> CaptureFrameAsync(string path, double positionFraction, int maxPixelSize,
                                                     CancellationToken ct = default) =>
            Task.FromResult<BitmapSource?>(null);
    }

    private sealed class FakePlayer : IMediaPlayer
    {
        public List<(string Path, TimeSpan? Start)> Opened { get; } = [];

        public bool Disposed { get; private set; }

        public event EventHandler? StateChanged { add { } remove { } }

        public event EventHandler? PositionChanged { add { } remove { } }

        public event EventHandler? EndReached;

        public event EventHandler<string>? Failed { add { } remove { } }

        public bool IsPlaying { get; set; }

        public bool HasMedia => Opened.Count > 0;

        public TimeSpan Position { get; set; }

        public TimeSpan Duration { get; set; }

        public int Volume { get; set; }

        public bool IsMuted { get; set; }

        public double Rate { get; set; } = 1;

        public IReadOnlyList<MediaTrack> AudioTracks => [];

        public IReadOnlyList<MediaTrack> SubtitleTracks => [];

        public int? CurrentAudioTrack { get; set; }

        public int? CurrentSubtitleTrack { get; set; }

        public FrameworkElement CreateView(FrameworkElement? overlay) => overlay ?? new FrameworkElement();

        public void Open(string path, TimeSpan? startAt = null)
        {
            Opened.Add((path, startAt));
            Position = TimeSpan.Zero;
            Duration = TimeSpan.Zero;
        }

        public void Play() => IsPlaying = true;

        public void Pause() => IsPlaying = false;

        public void Stop() => IsPlaying = false;

        public bool AddSubtitleFile(string path) => true;

        public void RaiseEnd() => EndReached?.Invoke(this, EventArgs.Empty);

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeLibrary : ILibraryService
    {
        private readonly Lock _lock = new();
        private readonly List<(long Id, ItemPatch Patch)> _patches = [];

        public List<(long Id, ItemPatch Patch)> Patches
        {
            get
            {
                lock (_lock)
                {
                    return [.. _patches];
                }
            }
        }

        public IReadOnlyList<ItemKindDescriptor> Kinds => [];

        public Task UpdateAsync(long id, ItemPatch patch)
        {
            lock (_lock)
            {
                _patches.Add((id, patch));
            }

            return Task.CompletedTask;
        }

        public void RegisterKind(IPluginContext owner, ItemKindDescriptor kind) => throw new NotSupportedException();
        public Task<IReadOnlyList<LibrarySource>> GetSourcesAsync(string kind) => throw new NotSupportedException();
        public Task<LibrarySource> AddSourceAsync(string kind, string path) => throw new NotSupportedException();
        public Task RemoveSourceAsync(long sourceId) => throw new NotSupportedException();
        public Task<ScanResult> ScanAsync(long sourceId, IProgress<ScanProgress>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LibraryItem>> QueryAsync(ItemQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryItem?> GetAsync(long id) => throw new NotSupportedException();
        public Task<LibraryItem> AddAsync(NewItem item) => throw new NotSupportedException();
        public Task RemoveAsync(IReadOnlyCollection<long> ids) => throw new NotSupportedException();
        public Task SetTagsAsync(long id, IReadOnlyCollection<string> tags) => throw new NotSupportedException();
        public Task<IReadOnlyList<TagCount>> GetTagsAsync(string kind) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetDirectoriesAsync(string kind) => throw new NotSupportedException();
    }

    private sealed class FakeShell : IShellService
    {
        public Window MainWindow => throw new NotSupportedException();
        public IReadOnlyList<PageDescriptor> Pages => [];
        public string? CurrentPageId => null;
        public bool IsFullScreen { get; set; }
        public AppTheme Theme { get; set; }
        public List<string> Notes { get; } = [];
        public void AddPage(IPluginContext owner, PageDescriptor page) { }
        public bool Navigate(string pageId, object? parameter = null) => false;
        public void ShowOverlay(IPluginContext owner, FrameworkElement view) { }
        public void CloseOverlay(FrameworkElement view) { }
        public void AddResources(IPluginContext owner, ResourceDictionary dictionary) { }
        public void Notify(string message, NotifySeverity severity = NotifySeverity.Info) => Notes.Add(message);
        public IBackgroundTask BeginTask(IPluginContext owner, string title) => throw new NotSupportedException();
        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
        public Task<bool> ConfirmAsync(string title, string message, string? confirmText = null, string? cancelText = null) => Task.FromResult(false);
        public Task<bool> ShowDialogAsync(DialogDescriptor dialog) => Task.FromResult(false);
    }

    private sealed class MemoryConfig : IPluginConfig
    {
        private readonly Dictionary<string, object?> _values = [];

        public T Get<T>(string key, T defaultValue) => _values.TryGetValue(key, out var v) && v is T t ? t : defaultValue;

        public void Set<T>(string key, T value) => _values[key] = value;
    }

    private sealed class NullLog : ILog
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message, Exception? ex = null) { }
        public void Error(string message, Exception? ex = null) { }
    }
}
