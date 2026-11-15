using System.IO;
using Magpienest.Sdk;
using Magpienest.Sdk.Imaging;
using Magpienest.Sdk.Thumbnails;

namespace Magpienest.Plugins.Thumbnails;

/// <summary>Provides <see cref="IThumbnailService"/>: a memory and disk thumbnail cache plus shell icons.</summary>
[Requires(typeof(IImagingService))]
public sealed class ThumbnailsPlugin : Plugin
{
    /// <inheritdoc />
    public override void Activate(IPluginContext ctx)
    {
        var imaging = ctx.Get<IImagingService>();

        // The file is created lazily on first use, off the UI thread.
        var dbPath = Path.Combine(ctx.Environment.CacheDirectory, "thumbnails.db");
        var service = new ThumbnailService(dbPath, imaging, ctx.Log);

        // Tracked before Provide, so on deactivation the service is withdrawn first and then disposed
        // (flushes pending writes, stops workers and the shell thread, closes the database).
        ctx.Track(service);
        ctx.Provide<IThumbnailService>(service);
    }
}
