using System.Reflection;
using Magpienest.Sdk;

namespace Magpienest.Core.Tests;

/// <summary>Loads real built plugins from artifacts/bin/&lt;configuration&gt;/plugins.</summary>
public sealed class DiskPluginTests
{
    [Fact]
    public void AboutPluginLoadsFromDisk()
    {
        using var h = new Harness();
        var source = new FilteredSource(new DirectoryPluginSource(BuiltPluginsDirectory(), isBuiltIn: true), "magpienest.about");
        var kernel = new Kernel(new KernelOptions
        {
            AppDirectory = h.DataDirectory,
            DataDirectory = h.DataDirectory,
            Dispatcher = h.Dispatcher,
            Sources = [source],
        });
        kernel.Start();

        // Loaded through its own PluginLoadContext; it waits for the Shell service, which is not in this kernel.
        // Seeing "IShellService" proves the [Requires] attribute types resolved to the host's SDK copy.
        var about = Assert.Single(kernel.Plugins.Plugins);
        Assert.Equal("magpienest.about", about.Manifest.Id);
        Assert.Equal(PluginState.Waiting, about.State);
        Assert.Equal(["IShellService"], about.MissingServices);
        Assert.Equal("關於", about.Manifest.Name["zh-TW"]);
        kernel.Shutdown();
    }

    [Fact]
    public void PluginFolderDoesNotShipTheSdk()
    {
        var folder = Path.Combine(BuiltPluginsDirectory(), "magpienest.about");
        Assert.True(File.Exists(Path.Combine(folder, "plugin.json")));
        Assert.True(File.Exists(Path.Combine(folder, "Magpienest.Plugins.About.dll")));
        Assert.True(File.Exists(Path.Combine(folder, "lang", "en.json")));
        Assert.True(File.Exists(Path.Combine(folder, "lang", "zh-TW.json")));
        Assert.False(File.Exists(Path.Combine(folder, "Magpienest.Sdk.dll")));
    }

    internal static string BuiltPluginsDirectory()
    {
        var configuration = typeof(DiskPluginTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "Configuration").Value!;
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Magpienest.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "artifacts", "bin", configuration, "plugins");
    }

    private sealed class FilteredSource(IPluginSource inner, params string[] ids) : IPluginSource
    {
        public IEnumerable<PluginCandidate> Discover(ILog log) => inner.Discover(log).Where(c => ids.Contains(c.Manifest.Id));
    }
}
