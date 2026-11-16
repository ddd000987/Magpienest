using Magpienest.Sdk;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Shell;

namespace HelloPlugin;

/// <summary>Adds one page to the navigation rail. Everything it registers is removed when the plugin is disabled.</summary>
[Requires(typeof(IShellService))]
public sealed class HelloPlugin : Plugin
{
    /// <inheritdoc />
    public override void Activate(IPluginContext ctx)
    {
        var shell = ctx.Get<IShellService>();

        shell.AddPage(ctx, new PageDescriptor
        {
            Id = "hello",
            TitleKey = "Hello.Page.Title",
            Glyph = "", // Segoe Fluent Icons: Home
            Order = 500,
            CreateView = () => new HelloView(() => shell.Notify(Loc.Get("Hello.Notification"), NotifySeverity.Success)),
        });

        ctx.Log.Info("Hello plugin activated.");
    }
}
