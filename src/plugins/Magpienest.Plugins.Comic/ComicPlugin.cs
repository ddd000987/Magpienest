using System.Windows.Media;
using System.Windows.Media.Imaging;
using Magpienest.Sdk;
using Magpienest.Sdk.Archives;
using Magpienest.Sdk.Imaging;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Settings;
using Magpienest.Sdk.Shell;
using Magpienest.Sdk.Thumbnails;
using Magpienest.Sdk.Viewing;

namespace Magpienest.Plugins.Comic;

/// <summary>The "Comics" page: books (image folders and archives) read in the viewer's book mode.</summary>
[Requires(typeof(IShellService), typeof(ISettingsService), typeof(ILibraryService),
          typeof(ILibraryBrowserFactory), typeof(IImagingService), typeof(IThumbnailService),
          typeof(IImageViewerService))]
[Optional(typeof(IArchiveService))]
public sealed class ComicPlugin : Plugin
{
    /// <summary>Kind and page id.</summary>
    public const string Kind = "comic";

    /// <inheritdoc />
    public override void Activate(IPluginContext ctx)
    {
        var shell = ctx.Get<IShellService>();
        var library = ctx.Get<ILibraryService>();
        var browsers = ctx.Get<ILibraryBrowserFactory>();
        var imaging = ctx.Get<IImagingService>();
        var viewer = ctx.Get<IImageViewerService>();
        ctx.TryGet<IArchiveService>(out var archives);

        var pages = new BookPages(imaging, archives);
        var books = new ComicBooks(pages, library, shell, viewer, ctx.Log);

        library.RegisterKind(ctx, new ItemKindDescriptor
        {
            Id = Kind,
            DisplayNameKey = "Comic.Kind",
            Scanner = new ComicScanner(imaging, archives),
            OpenPages = (item, ct) => pages.CreateAsync(item.Path, ct),
        });
        ctx.Get<IThumbnailService>().RegisterProvider(ctx, Kind, new ComicThumbnails(pages, imaging, library, ctx.Log));

        shell.AddPage(ctx, new PageDescriptor
        {
            Id = Kind,
            TitleKey = "Comic.Page.Title",
            Glyph = "",
            Order = 110,
            CreateView = () => browsers.CreateBrowser(ctx, new BrowserOptions
            {
                Kind = Kind,
                OpenItem = books.OpenAsync,
                TileAspectRatio = 0.7,
                ThumbnailStretch = Stretch.UniformToFill,
                ShowStatus = true,
                Sorts = [ItemSort.Title, ItemSort.AddedAt, ItemSort.LastOpenedAt, ItemSort.Rating, ItemSort.Progress],
                DefaultSort = ItemSort.Title,
                Subtitle = ComicBooks.Subtitle,
                ProgressFraction = ComicBooks.ProgressFraction,
                Commands = books.Commands,
                CreateDetails = item => ComicDetails.Create(item, library, ctx.Log),
            }),
        });
    }
}

/// <summary>Opening books, tile texts and context-menu commands.</summary>
internal sealed class ComicBooks(BookPages pages, ILibraryService library, IShellService shell, IImageViewerService viewer, ILog log)
{
    public IReadOnlyList<BrowserCommand> Commands =>
    [
        new BrowserCommand("Comic.Command.MarkRead", "",
            items => UpdateAllAsync(items, _ => new ItemPatch { Status = ItemStatus.Completed }),
            items => items.Any(i => i.Status != ItemStatus.Completed)),
        new BrowserCommand("Comic.Command.MarkUnread", "",
            items => UpdateAllAsync(items, _ => new ItemPatch { Status = ItemStatus.None, ProgressPosition = 0 }),
            items => items.Any(i => i.Status != ItemStatus.None || i.ProgressPosition > 0)),
        new BrowserCommand("Comic.Command.SetCover", "", SetCoverAsync, items => items.Count == 1),
    ];

    /// <summary>"12 / 180" once the book was started.</summary>
    public static string? Subtitle(LibraryItem item) => IsStarted(item)
        ? Loc.Format("Comic.Subtitle", item.ProgressPosition + 1, item.ProgressTotal)
        : null;

    public static double? ProgressFraction(LibraryItem item) => IsStarted(item)
        ? Math.Clamp((item.ProgressPosition + 1) / (double)item.ProgressTotal, 0, 1)
        : null;

    private static bool IsStarted(LibraryItem item) =>
        item.ProgressTotal > 0 && (item.OpenCount > 0 || item.ProgressPosition > 0 || item.Status != ItemStatus.None);

    public async Task OpenAsync(BrowserOpenRequest request)
    {
        var item = request.Item;
        if (item.IsMissing)
        {
            shell.Notify(Loc.Format("Comic.Missing", item.Title), NotifySeverity.Warning);
            return;
        }

        IPageSource source;
        try
        {
            source = await pages.CreateAsync(item.Path);
        }
        catch (ArchiveEncryptedException)
        {
            shell.Notify(Loc.Format("Comic.Error.Encrypted", item.Title), NotifySeverity.Error);
            return;
        }
        catch (ArchiveSupportMissingException)
        {
            shell.Notify(Loc.Format("Comic.Error.NoArchives", item.Title), NotifySeverity.Warning);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"Opening the book {item.Path} failed.", ex);
            shell.Notify(Loc.Format("Comic.Error.Open", item.Title), NotifySeverity.Error);
            return;
        }

        if (source.Count == 0)
        {
            await source.DisposeAsync();
            shell.Notify(Loc.Format("Comic.Error.Empty", item.Title), NotifySeverity.Warning);
            return;
        }

        var session = new ReadingSession(item.Status);
        viewer.Open(new ViewerRequest
        {
            Pages = source,
            StartIndex = ReadingSession.StartPage(item, source.Count),
            Mode = ViewerMode.Book,
            Title = item.Title,
            Book = item.GetExtra<ComicExtra>().ToBookSettings(),
            OnBookSettingsChanged = settings => _ = SaveAsync(item.Id,
                new ItemPatch { Extra = LibraryItemExtensions.SerializeExtra(ComicExtra.From(settings)) }),
            OnProgress = progress => _ = SaveAsync(item.Id, session.ToPatch(progress)),
        });
    }

    private async Task SaveAsync(long id, ItemPatch patch)
    {
        try
        {
            await library.UpdateAsync(id, patch);
        }
        catch (Exception ex)
        {
            log.Warn("Saving comic progress failed.", ex);
        }
    }

    private async Task UpdateAllAsync(IReadOnlyList<LibraryItem> items, Func<LibraryItem, ItemPatch> patch)
    {
        foreach (var item in items)
        {
            await SaveAsync(item.Id, patch(item));
        }
    }

    private async Task SetCoverAsync(IReadOnlyList<LibraryItem> items)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.Get("Comic.Cover.Title"),
            Filter = Loc.Get("Comic.Cover.Filter") + "|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(shell.MainWindow) == true)
        {
            await SaveAsync(items[0].Id, new ItemPatch { SetCoverFromFile = dialog.FileName });
        }
    }
}

/// <summary>Custom cover, else the book's first page. Also records the page count the first time.</summary>
internal sealed class ComicThumbnails(BookPages pages, IImagingService imaging, ILibraryService library, ILog log) : IThumbnailProvider
{
    public string GetStamp(LibraryItem item) => ThumbnailDefaults.DefaultStamp(item);

    public async Task<BitmapSource?> CreateAsync(LibraryItem item, int maxPixelSize, CancellationToken ct)
    {
        var size = new DecodeOptions(maxPixelSize, maxPixelSize);
        if (item.CoverPath is not null)
        {
            await using var cover = new FileStream(item.CoverPath, FileMode.Open, FileAccess.Read,
                                                   FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
            return await imaging.DecodeAsync(cover, size, ct);
        }

        await using var source = await pages.CreateAsync(item.Path, ct);
        if (item.ProgressTotal == 0 && source.Count > 0)
        {
            try
            {
                await library.UpdateAsync(item.Id, new ItemPatch { ProgressTotal = source.Count });
            }
            catch (Exception ex)
            {
                log.Warn("Recording the page count failed.", ex);
            }
        }

        if (source.Count == 0)
        {
            return null;
        }

        await using var first = await source.OpenAsync(0, ct);
        return await imaging.DecodeAsync(first, size, ct);
    }
}
