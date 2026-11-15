using System.Windows.Media.Imaging;
using Magpienest.Plugins.Comic;
using Magpienest.Sdk;
using Magpienest.Sdk.Archives;
using Magpienest.Sdk.Imaging;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Viewing;

namespace Magpienest.Plugins.Comic.Tests;

public sealed class BookPagesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mn-comic-" + Guid.NewGuid().ToString("N"));

    public BookPagesTests() => Directory.CreateDirectory(_dir);

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

    private string Touch(string relative)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1]);
        return path;
    }

    [Fact]
    public async Task FolderPagesAreDirectImagesInNaturalOrder()
    {
        foreach (var name in new[] { "10.jpg", "2.jpg", "1.PNG", "notes.txt", ".hidden.jpg", "sub/0.jpg" })
        {
            Touch(Path.Combine("book", name));
        }

        await using var pages = await new BookPages(new FakeImaging(), null).CreateAsync(Path.Combine(_dir, "book"),
            TestContext.Current.CancellationToken);
        Assert.Equal(["1.PNG", "2.jpg", "10.jpg"], Enumerable.Range(0, pages.Count).Select(pages.GetName));
        Assert.NotNull(pages.GetFilePath(0));
    }

    [Fact]
    public async Task ArchivePagesSortSegmentBySegmentAndSkipJunk()
    {
        var archive = Touch("book.cbz");
        var service = new FakeArchives(
            "ch10/1.jpg", "ch2/10.jpg", "ch2/2.jpg", "__MACOSX/ch2/._2.jpg", "ch2/._3.jpg", ".DS_Store", "readme.txt",
            "cover.jpg", "ch1/1.jpg");

        await using var pages = await new BookPages(new FakeImaging(), service).CreateAsync(archive,
            TestContext.Current.CancellationToken);
        Assert.Equal(["1.jpg", "2.jpg", "10.jpg", "1.jpg", "cover.jpg"], Enumerable.Range(0, pages.Count).Select(pages.GetName));
        Assert.Null(pages.GetFilePath(0));
        await using var stream = await pages.OpenAsync(1, TestContext.Current.CancellationToken);
        Assert.Equal("ch2/2.jpg", new StreamReader(stream).ReadToEnd());
        await pages.DisposeAsync();
        Assert.True(service.LastOpened!.Disposed);
    }

    [Fact]
    public void KeyOrderIsNaturalPerSegment()
    {
        var keys = new[] { "ch10/1.jpg", "ch2/1.jpg", "Ch1/2.jpg", "ch1/10.jpg" };
        Assert.Equal(["Ch1/2.jpg", "ch1/10.jpg", "ch2/1.jpg", "ch10/1.jpg"], keys.Order(BookPages.KeyOrder));
    }

    [Fact]
    public async Task ArchiveWithoutTheServiceThrows()
    {
        var archive = Touch("book.zip");
        await Assert.ThrowsAsync<ArchiveSupportMissingException>(
            () => new BookPages(new FakeImaging(), null).CreateAsync(archive, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ScannerFindsVolumesFoldersWithSubfoldersAndArchives()
    {
        Touch("Series/Vol 1/1.jpg");
        Touch("Series/Vol 1/2.jpg");
        Touch("Series/Vol 2/1.jpg");
        Touch("Mixed/cover.jpg");
        Touch("Mixed/Extra/1.jpg");
        Touch("Mixed/book.cbz");
        Touch("Mixed/notes.txt");
        Touch(".hidden/1.jpg");
        Touch("$RECYCLE/1.jpg");
        Touch("root.jpg");

        var withArchives = new ComicScanner(new FakeImaging(), new FakeArchives())
            .Scan(_dir, TestContext.Current.CancellationToken).ToList();
        var books = withArchives.Select(e => Path.GetRelativePath(_dir, e.Path)).ToList();
        Assert.Equal([".", Path.Combine("Mixed"), Path.Combine("Mixed", "book.cbz"), Path.Combine("Mixed", "Extra"),
                      Path.Combine("Series", "Vol 1"), Path.Combine("Series", "Vol 2")], books);

        var vol1 = withArchives.Single(e => e.Path.EndsWith("Vol 1"));
        Assert.Equal("Vol 1", vol1.Title);
        Assert.Equal(2, vol1.Size);
        Assert.Equal("book", withArchives.Single(e => e.Path.EndsWith(".cbz")).Title);

        var withoutArchives = new ComicScanner(new FakeImaging(), null).Scan(_dir, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(withoutArchives, e => e.Path.EndsWith(".cbz"));
    }

    internal sealed class FakeImaging : IImagingService
    {
        private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".png" };

        public IReadOnlySet<string> SupportedExtensions => Extensions;

        public bool IsSupported(string pathOrExtension) => Extensions.Contains(Path.GetExtension(pathOrExtension))
                                                            || Extensions.Contains(pathOrExtension);

        public Task<BitmapSource> DecodeAsync(Stream seekable, DecodeOptions options, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ImageInfo?> ReadInfoAsync(Stream seekable, CancellationToken ct = default) => throw new NotSupportedException();

        public byte[] EncodeJpeg(BitmapSource source, int quality) => [];

        public void RegisterDecoder(IPluginContext owner, IImageDecoder decoder) { }
    }

    private sealed class FakeArchives(params string[] keys) : IArchiveService
    {
        public FakeArchive? LastOpened { get; private set; }

        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".zip", ".cbz" };

        public bool IsSupported(string path) => SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

        public Task<IArchive> OpenAsync(string path, CancellationToken ct = default)
        {
            LastOpened = new FakeArchive(path, [.. keys.Select((k, i) => new ArchiveEntry(i, k, 1))]);
            return Task.FromResult<IArchive>(LastOpened);
        }
    }

    private sealed class FakeArchive(string path, IReadOnlyList<ArchiveEntry> entries) : IArchive
    {
        public bool Disposed { get; private set; }

        public string Path => path;

        public IReadOnlyList<ArchiveEntry> Entries => entries;

        public Task<Stream> OpenEntryAsync(ArchiveEntry entry, CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(entry.Key)));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class ReadingSessionTests
{
    private static LibraryItem Item(ItemStatus status, long position = 0) => new()
    {
        Kind = "comic", Path = @"C:\b", Directory = @"C:\", Title = "b", Status = status, ProgressPosition = position,
    };

    [Fact]
    public void StartPageResumesUnlessFinished()
    {
        Assert.Equal(12, ReadingSession.StartPage(Item(ItemStatus.InProgress, 12), 100));
        Assert.Equal(0, ReadingSession.StartPage(Item(ItemStatus.Completed, 99), 100));
        Assert.Equal(9, ReadingSession.StartPage(Item(ItemStatus.InProgress, 50), 10)); // book got shorter
    }

    [Fact]
    public void StatusMovesForwardAndTimeAccumulates()
    {
        var session = new ReadingSession(ItemStatus.None);

        var first = session.ToPatch(new ViewerProgress(0, 10, false, TimeSpan.FromSeconds(0.6)));
        Assert.True(first.MarkOpened);
        Assert.Equal(ItemStatus.InProgress, first.Status);
        Assert.Null(first.AddTimeSpentSeconds);
        Assert.Equal(0, first.ProgressPosition);
        Assert.Equal(10, first.ProgressTotal);

        var second = session.ToPatch(new ViewerProgress(4, 10, false, TimeSpan.FromSeconds(0.6)));
        Assert.False(second.MarkOpened);
        Assert.Null(second.Status);
        Assert.Equal(1, second.AddTimeSpentSeconds); // 1.2 s → 1 s, 0.2 s kept

        var end = session.ToPatch(new ViewerProgress(8, 10, true, TimeSpan.FromSeconds(2.8)));
        Assert.Equal(ItemStatus.Completed, end.Status);
        Assert.Equal(3, end.AddTimeSpentSeconds);

        var back = session.ToPatch(new ViewerProgress(2, 10, false, TimeSpan.Zero));
        Assert.Null(back.Status); // never moves back from Completed
    }

    [Theory]
    [InlineData(ItemStatus.OnHold)]
    [InlineData(ItemStatus.Dropped)]
    [InlineData(ItemStatus.Planned)]
    public void UserStatusesAreNeverChanged(ItemStatus status)
    {
        var session = new ReadingSession(status);
        Assert.Null(session.ToPatch(new ViewerProgress(0, 10, false, TimeSpan.Zero)).Status);
        Assert.Null(session.ToPatch(new ViewerProgress(9, 10, true, TimeSpan.Zero)).Status);
    }

    [Fact]
    public void ExtraRoundTripsAndFallsBack()
    {
        Assert.Null(new ComicExtra().ToBookSettings());
        Assert.Equal(new BookSettings(ReadingDirection.LeftToRight, SpreadMode.Double, true),
                     new ComicExtra { Direction = "ltr" }.ToBookSettings());

        var settings = new BookSettings(ReadingDirection.RightToLeft, SpreadMode.Single, false, PageFlow.Strip);
        var json = LibraryItemExtensions.SerializeExtra(ComicExtra.From(settings));
        Assert.Contains("\"direction\":\"rtl\"", json);
        Assert.Contains("\"flow\":\"strip\"", json);
        var item = Item(ItemStatus.None) with { Extra = json };
        Assert.Equal(settings, item.GetExtra<ComicExtra>().ToBookSettings());

        // A flow override alone keeps the other fields at the built-in defaults.
        Assert.Equal(new BookSettings(ReadingDirection.RightToLeft, SpreadMode.Double, true, PageFlow.Strip),
                     new ComicExtra { Flow = "strip" }.ToBookSettings());
    }
}
