using System.Windows.Media;
using System.Windows.Media.Imaging;
using Magpienest.Sdk;
using Magpienest.Sdk.Imaging;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Shell;
using Magpienest.Sdk.Thumbnails;
using Magpienest.Sdk.Viewing;

namespace Magpienest.Plugins.Illustration;

/// <summary>The "Illustrations" page: image files in library folders, browsed and viewed one by one.</summary>
[Requires(typeof(IShellService), typeof(ILibraryService), typeof(ILibraryBrowserFactory),
          typeof(IImagingService), typeof(IThumbnailService), typeof(IImageViewerService))]
public sealed class IllustrationPlugin : Plugin
{
    /// <summary>Kind and page id.</summary>
    public const string Kind = "illustration";

    /// <inheritdoc />
    public override void Activate(IPluginContext ctx)
    {
        var shell = ctx.Get<IShellService>();
        var library = ctx.Get<ILibraryService>();
        var browsers = ctx.Get<ILibraryBrowserFactory>();
        var imaging = ctx.Get<IImagingService>();
        var viewer = ctx.Get<IImageViewerService>();

        library.RegisterKind(ctx, new ItemKindDescriptor
        {
            Id = Kind,
            DisplayNameKey = "Illustration.Kind",
            Scanner = new IllustrationScanner(imaging),
        });
        ctx.Get<IThumbnailService>().RegisterProvider(ctx, Kind, new IllustrationThumbnails(imaging));

        shell.AddPage(ctx, new PageDescriptor
        {
            Id = Kind,
            TitleKey = "Illustration.Page.Title",
            Glyph = "",
            Order = 100,
            CreateView = () => browsers.CreateBrowser(ctx, new BrowserOptions
            {
                Kind = Kind,
                OpenItem = request => OpenAsync(request, viewer, library, shell, ctx.Log),
                TileAspectRatio = 1.0,
                ThumbnailStretch = Stretch.Uniform,
                Sorts = [ItemSort.Title, ItemSort.AddedAt, ItemSort.FileModified, ItemSort.Rating, ItemSort.FileSize],
                DefaultSort = ItemSort.Title,
            }),
        });
    }

    /// <summary>Opens the viewer over the grid's visible, non-missing items so paging follows the grid's sort and filter.</summary>
    private static async Task OpenAsync(BrowserOpenRequest request, IImageViewerService viewer, ILibraryService library,
                                        IShellService shell, ILog log)
    {
        var present = request.VisibleItems.Where(i => !i.IsMissing).ToList();
        var start = present.FindIndex(i => i.Id == request.Item.Id);
        if (start < 0)
        {
            shell.Notify(Loc.Get("Illustration.Missing"), NotifySeverity.Warning);
            return;
        }

        viewer.Open(new ViewerRequest
        {
            Pages = new FilePageSource([.. present.Select(i => i.Path)]),
            StartIndex = start,
            Mode = ViewerMode.Gallery,
            Title = request.Item.Title,
        });

        try
        {
            await library.UpdateAsync(request.Item.Id, new ItemPatch { MarkOpened = true });
        }
        catch (Exception ex)
        {
            log.Warn("Recording the opened illustration failed.", ex);
        }
    }
}

/// <summary>Every supported image file below a folder (hidden and system files skipped).</summary>
internal sealed class IllustrationScanner(IImagingService imaging) : IItemScanner
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    public IEnumerable<ScannedEntry> Scan(string root, CancellationToken ct)
    {
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", Options))
        {
            ct.ThrowIfCancellationRequested();
            if (imaging.IsSupported(file.Extension))
            {
                yield return new ScannedEntry(file.FullName, Path.GetFileNameWithoutExtension(file.Name), file.Length,
                                              new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
            }
        }
    }
}

/// <summary>Decodes the image (or its cover) at thumbnail size.</summary>
internal sealed class IllustrationThumbnails(IImagingService imaging) : IThumbnailProvider
{
    public string GetStamp(LibraryItem item) => ThumbnailDefaults.DefaultStamp(item);

    public async Task<BitmapSource?> CreateAsync(LibraryItem item, int maxPixelSize, CancellationToken ct)
    {
        var path = item.CoverPath ?? item.Path;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                                                64 * 1024, useAsync: true);
        return await imaging.DecodeAsync(stream, new DecodeOptions(maxPixelSize, maxPixelSize), ct);
    }
}
