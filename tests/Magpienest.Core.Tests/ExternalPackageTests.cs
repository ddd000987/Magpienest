using System.IO.Compression;
using Magpienest.Core;

namespace Magpienest.Core.Tests;

/// <summary>External plugins (built outside the app's plugin folder) must pass the install validation as built.</summary>
public sealed class ExternalPackageTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "mn-external-" + Guid.NewGuid().ToString("N"));

    public ExternalPackageTests() => Directory.CreateDirectory(_temp);

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    [Fact]
    public void Web_interface_build_is_a_valid_package_folder_and_zip()
    {
        var builtIn = Directory.GetDirectories(DiskPluginTests.BuiltPluginsDirectory())
            .Select(d => Path.GetFileName(d)!)
            .ToHashSet(StringComparer.Ordinal);
        var folder = Path.Combine(Path.GetDirectoryName(DiskPluginTests.BuiltPluginsDirectory())!, "external", "magpienest.webui");
        Assert.True(Directory.Exists(folder), folder);
        Assert.DoesNotContain("magpienest.webui", builtIn);
        Assert.False(File.Exists(Path.Combine(folder, "Magpienest.Sdk.dll")));

        var pluginsRoot = Path.Combine(_temp, "plugins");
        Directory.CreateDirectory(pluginsRoot);
        using (var package = PluginPackage.Open(folder, pluginsRoot, builtIn))
        {
            Assert.Equal("magpienest.webui", package.Manifest.Id);
        }

        var zip = Path.Combine(_temp, "Magpienest.WebUi.zip");
        ZipFile.CreateFromDirectory(folder, zip, CompressionLevel.Fastest, includeBaseDirectory: false);
        using (var package = PluginPackage.Open(zip, pluginsRoot, builtIn))
        {
            Assert.Equal("magpienest.webui", package.Manifest.Id);
            package.Extract(Path.Combine(pluginsRoot, "magpienest.webui"));
        }

        Assert.True(File.Exists(Path.Combine(pluginsRoot, "magpienest.webui", "www", "index.html")));
    }
}
