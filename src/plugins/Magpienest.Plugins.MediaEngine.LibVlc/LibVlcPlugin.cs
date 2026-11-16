using System.IO;
using Magpienest.Sdk;
using Magpienest.Sdk.Media;

namespace Magpienest.Plugins.MediaEngine.LibVlc;

/// <summary>Provides <see cref="IMediaEngine"/> on LibVLC (native libraries in <c>libvlc\win-x64</c>).</summary>
public sealed class LibVlcPlugin : Plugin
{
    /// <inheritdoc />
    public override void Activate(IPluginContext ctx)
    {
        var native = Path.Combine(ctx.PluginDirectory, "libvlc", "win-x64");
        var engine = new LibVlcEngine(native, ctx.Log);
        ctx.Provide<IMediaEngine>(engine);
        ctx.Track(engine);
    }
}
