using Magpienest.Sdk;
using Magpienest.Sdk.Imaging;

namespace Magpienest.Plugins.Imaging;

/// <summary>Provides <see cref="IImagingService"/>: WIC decoding, EXIF orientation and JPEG encoding.</summary>
public sealed class ImagingPlugin : Plugin
{
    /// <inheritdoc />
    public override void Activate(IPluginContext ctx) =>
        ctx.Provide<IImagingService>(new ImagingService(ctx.Log));
}
