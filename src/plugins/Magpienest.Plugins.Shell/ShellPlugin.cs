using Magpienest.Sdk;
using Magpienest.Sdk.Shell;

namespace Magpienest.Plugins.Shell;

/// <summary>Provides <see cref="IShellService"/>: the main window and everything around it.</summary>
public sealed class ShellPlugin : Plugin
{
    /// <inheritdoc />
    public override void Activate(IPluginContext ctx)
    {
        var shell = new ShellService(ctx);
        ctx.Track(shell.Close);
        ctx.Provide<IShellService>(shell);
        ctx.On<AppStarted>(_ => shell.Start());
        ctx.On<AppActivationRequested>(_ => shell.BringToFront());
        if (ctx.Environment.IsStarted)
        {
            shell.Start();
        }
    }
}
