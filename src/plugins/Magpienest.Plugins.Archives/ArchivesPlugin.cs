using Magpienest.Sdk;
using Magpienest.Sdk.Archives;

namespace Magpienest.Plugins.Archives;

/// <summary>Provides <see cref="IArchiveService"/> for zip, rar and 7z files.</summary>
public sealed class ArchivesPlugin : Plugin
{
    /// <inheritdoc />
    public override void Activate(IPluginContext ctx)
    {
        var root = Path.Combine(ctx.Environment.CacheDirectory, "archives");
        Directory.CreateDirectory(root);
        var service = new ArchiveService(root, new SharpCompressBackendFactory(), ctx.Log, ctx.Deactivating);
        ctx.Provide<IArchiveService>(service);

        var log = ctx.Log;
        _ = Task.Run(() =>
        {
            try
            {
                CacheHygiene.Run(root, service.IsInUse, DateTime.UtcNow, CacheHygiene.MaxBytes, log);
            }
            catch (Exception ex)
            {
                log.Warn("Cleaning the archive cache failed.", ex);
            }
        }, ctx.Deactivating);
    }
}
