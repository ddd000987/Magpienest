using System.IO;
using Magpienest.Sdk;

namespace Magpienest.Plugins.DevTools;

/// <summary>
/// <c>--install-plugin &lt;zip&gt;</c> (repeatable, phase 8 T8.3): installs each package with
/// <see cref="IPluginManager.InstallAsync"/> without a dialog, in the order given, then exits with 0 when every
/// install succeeded and 1 otherwise. Used by the publish script to test the store packages. It refuses the
/// user's own data folder, so it only runs with an explicit <c>--data-dir</c> (or <c>MAGPIENEST_DATA_DIR</c>).
/// </summary>
internal static class InstallPluginRun
{
    /// <summary>The packages named by every <c>--install-plugin</c>, in order; empty when there is none.</summary>
    public static IReadOnlyList<string> Packages(IReadOnlyList<string> args)
    {
        var packages = new List<string>();
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], "--install-plugin", StringComparison.OrdinalIgnoreCase))
            {
                packages.Add(Path.GetFullPath(args[i + 1]));
            }
        }

        return packages;
    }

    /// <summary>
    /// True when the data folder is one of the folders Magpienest picks by itself (the user's own:
    /// %APPDATA%\Magpienest, or "data" next to the exe in a portable copy).
    /// </summary>
    public static bool IsDefaultDataDirectory(string dataDirectory, string appDirectory)
    {
        var data = Normalize(dataDirectory);
        return string.Equals(data, Normalize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Magpienest")), StringComparison.OrdinalIgnoreCase)
               || string.Equals(data, Normalize(Path.Combine(appDirectory, "data")), StringComparison.OrdinalIgnoreCase);
    }

    public static async Task RunAsync(IPluginContext ctx, IReadOnlyList<string> packages)
    {
        var exitCode = 0;
        try
        {
            if (IsDefaultDataDirectory(ctx.Environment.DataDirectory, ctx.Environment.AppDirectory))
            {
                ctx.Log.Error("--install-plugin needs --data-dir with a separate data folder; nothing was installed.");
                exitCode = 1;
            }
            else
            {
                var manager = ctx.Get<IPluginManager>();
                foreach (var package in packages)
                {
                    var result = await manager.InstallAsync(package);
                    if (result.Success)
                    {
                        ctx.Log.Info($"--install-plugin '{package}': installed '{result.PluginId}'{(result.RestartRequired ? " (staged)" : string.Empty)}.");
                    }
                    else
                    {
                        ctx.Log.Error($"--install-plugin '{package}' failed ({result.ErrorCode}): {result.Error}");
                        exitCode = 1;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ctx.Log.Error("--install-plugin failed.", ex);
            exitCode = 1;
        }

        ctx.Log.Info($"Install run finished with exit code {exitCode}.");
        ctx.Environment.RequestShutdown(exitCode);
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
