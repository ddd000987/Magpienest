using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.Json.Nodes;
using Magpienest.Sdk;

namespace Magpienest.Core.Tests;

/// <summary>Installing, updating and removing plugins from zips and folders, with real assemblies.</summary>
public sealed class PluginInstallTests
{
    private const string AssemblyFile = "Test.EmittedPlugin.dll";

    // ---- validation: one test per rule -------------------------------------------------------------

    [Fact]
    public void Validation_MissingPathIsRejected()
    {
        using var w = new World();
        w.StartKernel();
        w.AssertRejected(w.Install(Path.Combine(w.Root, "nothing.zip")), PluginInstallError.PackageNotFound);
    }

    [Fact]
    public void Validation_NotAZipIsRejected()
    {
        using var w = new World();
        w.StartKernel();
        var path = Path.Combine(w.Root, "bad.zip");
        File.WriteAllText(path, "this is not a zip file");
        w.AssertRejected(w.Install(path), PluginInstallError.InvalidPackage);
    }

    [Fact]
    public void Validation_MissingManifestIsRejected()
    {
        using var w = new World();
        w.StartKernel();
        var zip = w.Zip(("readme.txt", "hi"), (AssemblyFile, Assembly()));
        w.AssertRejected(w.Install(zip), PluginInstallError.ManifestMissing);
    }

    [Fact]
    public void Validation_ManifestInTwoTopLevelFoldersIsRejected()
    {
        using var w = new World();
        w.StartKernel();
        var zip = w.Zip(("a/plugin.json", Manifest("t.one")), ("a/" + AssemblyFile, Assembly()), ("b/readme.txt", "x"));
        w.AssertRejected(w.Install(zip), PluginInstallError.ManifestMissing);
    }

    [Fact]
    public void Validation_UnparsableManifestIsRejected()
    {
        using var w = new World();
        w.StartKernel();
        var zip = w.Zip(("plugin.json", "{ not json"), (AssemblyFile, Assembly()));
        w.AssertRejected(w.Install(zip), PluginInstallError.ManifestInvalid);
    }

    [Fact]
    public void Validation_ManifestWithoutVersionIsRejected()
    {
        using var w = new World();
        w.StartKernel();
        var zip = w.Zip(("plugin.json", """{ "id": "t.one", "assembly": "Test.EmittedPlugin.dll" }"""), (AssemblyFile, Assembly()));
        w.AssertRejected(w.Install(zip), PluginInstallError.ManifestInvalid);
    }

    [Theory]
    [InlineData("Bad_Id")]
    [InlineData("UPPER")]
    [InlineData("has space")]
    [InlineData("a..b")]
    [InlineData("../x")]
    public void Validation_InvalidIdIsRejected(string id)
    {
        using var w = new World();
        w.StartKernel();
        var zip = w.Zip(("plugin.json", Manifest(id)), (AssemblyFile, Assembly()));
        w.AssertRejected(w.Install(zip), PluginInstallError.InvalidId);
    }

    [Fact]
    public void Validation_UnsupportedApiVersionIsRejected()
    {
        using var w = new World();
        w.StartKernel();
        var zip = w.Zip(("plugin.json", Manifest("t.one", apiVersion: 99)), (AssemblyFile, Assembly()));
        var result = w.Install(zip);
        w.AssertRejected(result, PluginInstallError.UnsupportedApiVersion);
        Assert.Equal("t.one", result.PluginId);
    }

    [Fact]
    public void Validation_MissingAssemblyIsRejected()
    {
        using var w = new World();
        w.StartKernel();
        var zip = w.Zip(("plugin.json", Manifest("t.one")), ("other.dll", Assembly()));
        w.AssertRejected(w.Install(zip), PluginInstallError.AssemblyMissing);
    }

    [Fact]
    public void Validation_BuiltInIdIsRejected()
    {
        using var w = new World();
        w.StartKernel();
        var zip = w.Zip(("plugin.json", Manifest(World.BuiltInId)), (AssemblyFile, Assembly()));
        w.AssertRejected(w.Install(zip), PluginInstallError.BuiltInId);
    }

    [Theory]
    [InlineData("Magpienest.Sdk.dll")]
    [InlineData("Magpienest.Core.dll")]
    [InlineData("magpienest.core.DLL")]
    [InlineData("Magpienest.App.exe")]
    [InlineData("Magpienest.App.dll")]
    [InlineData("Magpienest.exe")]
    [InlineData("Magpienest.dll")]
    [InlineData("lib/Magpienest.Sdk.dll")]
    public void Validation_HostAssembliesAreRejected(string forbidden)
    {
        using var w = new World();
        w.StartKernel();
        var zip = w.Zip(("plugin.json", Manifest("t.one")), (AssemblyFile, Assembly()), (forbidden, "x"));
        w.AssertRejected(w.Install(zip), PluginInstallError.ForbiddenFile);
    }

    [Fact]
    public void Validation_ForbiddenFileInsideTopLevelFolderIsRejected()
    {
        using var w = new World();
        w.StartKernel();
        var zip = w.Zip(("Hello/plugin.json", Manifest("t.one")), ("Hello/" + AssemblyFile, Assembly()), ("Hello/Magpienest.Sdk.dll", "x"));
        w.AssertRejected(w.Install(zip), PluginInstallError.ForbiddenFile);
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("..\\..\\evil.dll")]
    [InlineData("lib/../../evil.dll")]
    [InlineData("/evil.dll")]
    [InlineData("C:/evil.dll")]
    [InlineData("C:\\Windows\\evil.dll")]
    [InlineData("lib/evil.dll::$DATA")]
    [InlineData("lib/evil.dll.")]
    public void Validation_ZipSlipIsRejectedAndNothingIsWritten(string entryName)
    {
        using var w = new World();
        w.StartKernel();
        var zip = w.Zip(("plugin.json", Manifest("t.one")), (AssemblyFile, Assembly()), (entryName, "evil"));
        w.AssertRejected(w.Install(zip), PluginInstallError.UnsafePath);
        Assert.False(File.Exists(Path.Combine(w.DataDirectory, "evil.dll")));
        Assert.False(File.Exists(Path.Combine(w.DataDirectory, "plugins", "evil.dll")));
        Assert.False(File.Exists(Path.Combine(w.Root, "evil.dll")));
    }

    [Fact]
    public void Validation_MoreThan5000EntriesIsRejected()
    {
        using var w = new World();
        w.StartKernel();
        var files = new List<(string, byte[])> { ("plugin.json", Utf8(Manifest("t.one"))), (AssemblyFile, Assembly()) };
        files.AddRange(Enumerable.Range(0, 5000).Select(i => ($"data/f{i}.txt", Array.Empty<byte>())));
        var zip = w.ZipBytes(files);
        w.AssertRejected(w.Install(zip), PluginInstallError.TooManyEntries);
    }

    [Fact]
    public void Validation_Exactly5000EntriesIsAccepted()
    {
        using var w = new World();
        w.StartKernel();
        var files = new List<(string, byte[])> { ("plugin.json", Utf8(Manifest("t.one"))), (AssemblyFile, Assembly()) };
        files.AddRange(Enumerable.Range(0, 4998).Select(i => ($"data/f{i}.txt", Array.Empty<byte>())));
        var result = w.Install(w.ZipBytes(files));
        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public void Validation_MoreThan500MbUncompressedIsRejected()
    {
        using var w = new World();
        w.StartKernel();
        var zeros = new byte[90 * 1024 * 1024];
        var files = new List<(string, byte[])> { ("plugin.json", Utf8(Manifest("t.one"))), (AssemblyFile, Assembly()) };
        files.AddRange(Enumerable.Range(0, 6).Select(i => ($"big{i}.bin", zeros)));
        var zip = w.ZipBytes(files);
        Assert.True(new FileInfo(zip).Length < 50 * 1024 * 1024, "the test zip should compress well");
        w.AssertRejected(w.Install(zip), PluginInstallError.TooLarge);
    }

    [Fact]
    public void Validation_RejectedPackageLeavesNothingBehind()
    {
        using var w = new World();
        w.StartKernel();
        var before = w.Kernel.Plugins.Plugins.Count;
        w.AssertRejected(w.Install(w.Zip(("plugin.json", Manifest("t.one")), ("x/y.txt", "x"))), PluginInstallError.AssemblyMissing);
        Assert.Equal(before, w.Kernel.Plugins.Plugins.Count);
        Assert.False(w.Kernel.Plugins.RestartRequired);
        Assert.False(File.Exists(Path.Combine(w.DataDirectory, "config", "kernel.json")) && w.KernelJson()["pendingInstall"] is not null);
    }

    // ---- install -----------------------------------------------------------------------------------

    [Fact]
    public void Install_NewIdFromZipActivatesWithoutRestart()
    {
        using var w = new World();
        w.StartKernel();
        var zip = w.Zip(("plugin.json", Manifest("t.one")), (AssemblyFile, Assembly()), ("lang/en.json", "{}"));

        var result = w.Install(zip);

        Assert.True(result.Success, result.Error);
        Assert.Equal("t.one", result.PluginId);
        Assert.False(result.RestartRequired);
        Assert.Null(result.Error);
        Assert.Equal(PluginInstallError.None, result.ErrorCode);
        var info = w.Info("t.one");
        Assert.Equal(PluginState.Active, info.State);
        Assert.False(info.IsBuiltIn);
        Assert.True(File.Exists(Path.Combine(w.PluginsRoot, "t.one", "plugin.json")));
        Assert.True(File.Exists(Path.Combine(w.PluginsRoot, "t.one", "lang", "en.json")));
        Assert.False(w.Kernel.Plugins.RestartRequired);
        Assert.Empty(Directory.GetDirectories(Path.Combine(w.PluginsRoot, ".staging")));
    }

    [Fact]
    public void Install_ManifestInsideSingleTopLevelFolderWorks()
    {
        using var w = new World();
        w.StartKernel();
        var zip = w.Zip(("Hello/", ""), ("Hello/plugin.json", Manifest("t.one")), ("Hello/" + AssemblyFile, Assembly()), ("Hello/lang/", ""));
        var result = w.Install(zip);
        Assert.True(result.Success, result.Error);
        Assert.Equal(PluginState.Active, w.State("t.one"));
        Assert.True(File.Exists(Path.Combine(w.PluginsRoot, "t.one", "plugin.json")));
        Assert.False(Directory.Exists(Path.Combine(w.PluginsRoot, "t.one", "Hello")));
    }

    [Fact]
    public void Install_FromFolderWorks()
    {
        using var w = new World();
        w.StartKernel();
        var source = Path.Combine(w.Root, "source");
        Directory.CreateDirectory(Path.Combine(source, "lang"));
        File.WriteAllText(Path.Combine(source, "plugin.json"), Manifest("t.one"));
        File.WriteAllBytes(Path.Combine(source, AssemblyFile), Assembly());
        File.WriteAllText(Path.Combine(source, "lang", "en.json"), "{}");

        var result = w.Install(source);

        Assert.True(result.Success, result.Error);
        Assert.Equal(PluginState.Active, w.State("t.one"));
        Assert.True(File.Exists(Path.Combine(w.PluginsRoot, "t.one", "lang", "en.json")));
        Assert.True(File.Exists(Path.Combine(source, "plugin.json")), "the source folder is left alone");
    }

    [Fact]
    public void Install_FolderWithManifestInOneSubfolderWorks()
    {
        using var w = new World();
        w.StartKernel();
        var source = Path.Combine(w.Root, "source", "Hello");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "plugin.json"), Manifest("t.one"));
        File.WriteAllBytes(Path.Combine(source, AssemblyFile), Assembly());

        var result = w.Install(Path.Combine(w.Root, "source"));

        Assert.True(result.Success, result.Error);
        Assert.Equal(PluginState.Active, w.State("t.one"));
    }

    [Fact]
    public void Install_IsPersistedAndFoundByTheNextKernel()
    {
        using var w = new World();
        w.StartKernel();
        w.InstallOk(w.Zip(("plugin.json", Manifest("t.one")), (AssemblyFile, Assembly())));
        w.Kernel.Shutdown();

        var next = w.StartKernel();

        Assert.Equal(PluginState.Active, w.State("t.one"));
        Assert.Equal(PluginState.Active, w.Info("t.one").State);
        Assert.NotNull(next);
    }

    [Fact]
    public void Install_WhileAnotherInstallRunsReportsBusy()
    {
        using var w = new World();
        w.StartKernel();
        var first = w.Kernel.Plugins.InstallAsync(w.Zip(("plugin.json", Manifest("t.one")), (AssemblyFile, Assembly())));
        var second = w.Kernel.Plugins.InstallAsync(w.Zip(("plugin.json", Manifest("t.two")), (AssemblyFile, Assembly())));

        Assert.True(second.IsCompleted);
        Assert.Equal(PluginInstallError.Busy, w.Wait(second).ErrorCode);
        Assert.True(w.Wait(first).Success);
        Assert.Null(w.Kernel.Plugins.Plugins.FirstOrDefault(p => p.Manifest.Id == "t.two"));
        w.InstallOk(w.Zip(("plugin.json", Manifest("t.two")), (AssemblyFile, Assembly())));
    }

    [Fact]
    public void Install_PreviouslyDisabledChoiceIsHonoured()
    {
        using var w = new World();
        w.WriteKernelJson(new JsonObject { ["enabled"] = new JsonObject { ["t.one"] = false } });
        w.StartKernel();
        var result = w.Install(w.Zip(("plugin.json", Manifest("t.one")), (AssemblyFile, Assembly())));
        Assert.True(result.Success, result.Error);
        Assert.Equal(PluginState.Disabled, w.State("t.one"));
    }

    // ---- update --------------------------------------------------------------------------------------

    [Fact]
    public void Update_LoadedIdIsStagedAndTheRunningVersionKeepsWorking()
    {
        using var w = new World();
        w.StartKernel();
        w.InstallOk(w.Zip(("plugin.json", Manifest("t.one", "1.0.0")), (AssemblyFile, Assembly())));

        var result = w.Install(w.Zip(("plugin.json", Manifest("t.one", "2.0.0")), (AssemblyFile, Assembly())));

        Assert.True(result.Success, result.Error);
        Assert.True(result.RestartRequired);
        Assert.True(w.Kernel.Plugins.RestartRequired);
        var info = w.Info("t.one");
        Assert.Equal(PluginState.Active, info.State);
        Assert.Equal("1.0.0", info.Manifest.Version);
        Assert.Contains("\"1.0.0\"", File.ReadAllText(Path.Combine(w.PluginsRoot, "t.one", "plugin.json")));
        Assert.Contains("\"2.0.0\"", File.ReadAllText(Path.Combine(w.PluginsRoot, ".staging", "t.one", "plugin.json")));
        Assert.Equal(["t.one"], w.KernelJson()["pendingInstall"]!.AsArray().Select(n => (string)n!));
    }

    [Fact]
    public void Update_IsAppliedByTheNextKernelInstance()
    {
        // The plugin is disabled, so its assembly is never loaded and Windows lets the folder be replaced.
        using var w = new World();
        w.WriteKernelJson(new JsonObject { ["enabled"] = new JsonObject { ["t.one"] = false } });
        w.StartKernel();
        w.InstallOk(w.Zip(("plugin.json", Manifest("t.one", "1.0.0")), (AssemblyFile, Assembly())));
        var update = w.Install(w.Zip(("plugin.json", Manifest("t.one", "2.0.0")), (AssemblyFile, Assembly()), ("added.txt", "new")));
        Assert.True(update.RestartRequired);
        w.Kernel.Shutdown();

        w.StartKernel();

        Assert.Equal("2.0.0", w.Info("t.one").Manifest.Version);
        Assert.True(File.Exists(Path.Combine(w.PluginsRoot, "t.one", "added.txt")));
        Assert.False(Directory.Exists(Path.Combine(w.PluginsRoot, ".staging", "t.one")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(w.PluginsRoot, ".staging")));
        Assert.False(w.Kernel.Plugins.RestartRequired);
        Assert.Null(w.KernelJson()["pendingInstall"]);
        Assert.False(w.KernelJson()["enabled"]!["t.one"]!.GetValue<bool>(), "the enabled choice survives");
    }

    [Fact]
    public void Update_IsAppliedEvenWhenTheOldVersionWasLoadedByTheLastInstance()
    {
        using var w = new World();
        w.StartKernel();
        w.InstallOk(w.Zip(("plugin.json", Manifest("t.one", "1.0.0")), (AssemblyFile, Assembly())));
        Assert.True(w.Install(w.Zip(("plugin.json", Manifest("t.one", "2.0.0")), (AssemblyFile, Assembly()))).RestartRequired);

        w.StartKernel();

        Assert.Equal("2.0.0", w.Info("t.one").Manifest.Version);
        Assert.Equal(PluginState.Active, w.State("t.one"));
        Assert.Null(w.KernelJson()["pendingInstall"]);
        Assert.False(Directory.Exists(Path.Combine(w.PluginsRoot, ".staging", "t.one")));
    }

    [Fact]
    public void Update_ThatCannotBeAppliedKeepsTheOldVersionAndIsRetried()
    {
        using var w = new World();
        w.StartKernel();
        w.InstallOk(w.Zip(("plugin.json", Manifest("t.one", "1.0.0")), (AssemblyFile, Assembly()), ("locked.txt", "x")));
        Assert.True(w.Install(w.Zip(("plugin.json", Manifest("t.one", "2.0.0")), (AssemblyFile, Assembly()))).RestartRequired);

        using (new FileStream(Path.Combine(w.PluginsRoot, "t.one", "locked.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            w.StartKernel();

            Assert.Equal("1.0.0", w.Info("t.one").Manifest.Version);
            Assert.Equal(PluginState.Active, w.State("t.one"));
            Assert.Equal(["t.one"], w.KernelJson()["pendingInstall"]!.AsArray().Select(n => (string)n!));
            Assert.True(Directory.Exists(Path.Combine(w.PluginsRoot, ".staging", "t.one")));
        }

        w.StartKernel();

        Assert.Equal("2.0.0", w.Info("t.one").Manifest.Version);
        Assert.Null(w.KernelJson()["pendingInstall"]);
    }

    [Fact]
    public void Update_LeftoverInPluginsFolderWithoutAnEntryIsStagedToo()
    {
        using var w = new World();
        // A folder that discovery skipped (e.g. a broken manifest) must not be overwritten while running.
        Directory.CreateDirectory(Path.Combine(w.PluginsRoot, "t.one"));
        File.WriteAllText(Path.Combine(w.PluginsRoot, "t.one", "junk.txt"), "x");
        w.StartKernel();

        var result = w.Install(w.Zip(("plugin.json", Manifest("t.one")), (AssemblyFile, Assembly())));

        Assert.True(result.Success, result.Error);
        Assert.True(result.RestartRequired);
        Assert.True(File.Exists(Path.Combine(w.PluginsRoot, "t.one", "junk.txt")));
    }

    [Fact]
    public void Update_InstallingOverARemovedPluginCancelsTheRemoval()
    {
        using var w = new World();
        w.StartKernel();
        w.InstallOk(w.Zip(("plugin.json", Manifest("t.one", "1.0.0")), (AssemblyFile, Assembly())));
        w.Kernel.Plugins.Uninstall("t.one");
        Assert.Equal(PluginState.PendingRemoval, w.State("t.one"));

        var result = w.Install(w.Zip(("plugin.json", Manifest("t.one", "2.0.0")), (AssemblyFile, Assembly())));

        Assert.True(result.RestartRequired);
        Assert.Equal(PluginState.Active, w.State("t.one"));
        Assert.Null(w.KernelJson()["pendingRemoval"]);
        Assert.NotNull(w.KernelJson()["pendingInstall"]);
    }

    // ---- uninstall -----------------------------------------------------------------------------------

    [Fact]
    public void Uninstall_DeactivatesNowAndKeepsThePluginListed()
    {
        using var w = new World();
        w.StartKernel();
        w.InstallOk(w.Zip(("plugin.json", Manifest("t.one")), (AssemblyFile, Assembly())));
        Directory.CreateDirectory(Path.Combine(w.DataDirectory, "plugin-data", "t.one"));
        File.WriteAllText(Path.Combine(w.DataDirectory, "plugin-data", "t.one", "keep.txt"), "x");

        w.Kernel.Plugins.Uninstall("t.one");

        Assert.Equal(PluginState.PendingRemoval, w.State("t.one"));
        Assert.Equal(["t.one"], w.KernelJson()["pendingRemoval"]!.AsArray().Select(n => (string)n!));
        Assert.True(Directory.Exists(Path.Combine(w.PluginsRoot, "t.one")), "files are deleted at the next start");
        Assert.True(File.Exists(Path.Combine(w.DataDirectory, "plugin-data", "t.one", "keep.txt")));
    }

    [Fact]
    public void Uninstall_ThenNextKernelRemovesTheFolderButKeepsDataAndConfig()
    {
        // Disabled plugins are never loaded, so the folder is not locked and the removal can finish in-process.
        using var w = new World();
        w.WriteKernelJson(new JsonObject { ["enabled"] = new JsonObject { ["t.one"] = false } });
        w.StartKernel();
        w.InstallOk(w.Zip(("plugin.json", Manifest("t.one")), (AssemblyFile, Assembly())));
        Directory.CreateDirectory(Path.Combine(w.DataDirectory, "plugin-data", "t.one"));
        File.WriteAllText(Path.Combine(w.DataDirectory, "plugin-data", "t.one", "keep.txt"), "x");
        File.WriteAllText(Path.Combine(w.DataDirectory, "config", "t.one.json"), "{}");
        w.Kernel.Plugins.Uninstall("t.one");
        w.Kernel.Shutdown();

        w.StartKernel();

        Assert.False(Directory.Exists(Path.Combine(w.PluginsRoot, "t.one")));
        Assert.DoesNotContain(w.Kernel.Plugins.Plugins, p => p.Manifest.Id == "t.one");
        Assert.Null(w.KernelJson()["pendingRemoval"]);
        Assert.True(File.Exists(Path.Combine(w.DataDirectory, "plugin-data", "t.one", "keep.txt")));
        Assert.True(File.Exists(Path.Combine(w.DataDirectory, "config", "t.one.json")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(w.PluginsRoot, ".staging")));
    }
    [Fact]
    public void Uninstall_ThenNextKernelRemovesAnActivePluginFolder()
    {
        using var w = new World();
        w.StartKernel();
        w.InstallOk(w.Zip(("plugin.json", Manifest("t.one")), (AssemblyFile, Assembly())));
        w.Kernel.Plugins.Uninstall("t.one");

        w.StartKernel();

        Assert.False(Directory.Exists(Path.Combine(w.PluginsRoot, "t.one")));
        Assert.DoesNotContain(w.Kernel.Plugins.Plugins, p => p.Manifest.Id == "t.one");
        Assert.Null(w.KernelJson()["pendingRemoval"]);
    }

    [Fact]
    public void Uninstall_ThatCannotFinishStaysListedAsPendingRemovalAndIsRetried()
    {
        using var w = new World();
        w.StartKernel();
        w.InstallOk(w.Zip(("plugin.json", Manifest("t.one")), (AssemblyFile, Assembly()), ("locked.txt", "x")));
        w.Kernel.Plugins.Uninstall("t.one");

        using (new FileStream(Path.Combine(w.PluginsRoot, "t.one", "locked.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            w.StartKernel();

            Assert.True(Directory.Exists(Path.Combine(w.PluginsRoot, "t.one")));
            Assert.Equal(PluginState.PendingRemoval, w.State("t.one"));
            Assert.Equal(["t.one"], w.KernelJson()["pendingRemoval"]!.AsArray().Select(n => (string)n!));
        }

        w.StartKernel();

        Assert.False(Directory.Exists(Path.Combine(w.PluginsRoot, "t.one")));
        Assert.DoesNotContain(w.Kernel.Plugins.Plugins, p => p.Manifest.Id == "t.one");
        Assert.Null(w.KernelJson()["pendingRemoval"]);
    }

    [Fact]
    public void Uninstall_BuiltInPluginThrows()
    {
        using var w = new World();
        w.StartKernel();
        var error = Assert.Throws<InvalidOperationException>(() => w.Kernel.Plugins.Uninstall(World.BuiltInId));
        Assert.Contains("built in", error.Message);
        Assert.Equal(PluginState.Active, w.State(World.BuiltInId));
        Assert.Null(w.KernelJson()?["pendingRemoval"]);
    }

    [Fact]
    public void Uninstall_UnknownPluginThrows()
    {
        using var w = new World();
        w.StartKernel();
        Assert.Throws<InvalidOperationException>(() => w.Kernel.Plugins.Uninstall("t.missing"));
        Assert.Throws<InvalidOperationException>(() => w.Kernel.Plugins.UndoUninstall("t.missing"));
    }

    [Fact]
    public void Uninstall_TwiceIsHarmless()
    {
        using var w = new World();
        w.StartKernel();
        w.InstallOk(w.Zip(("plugin.json", Manifest("t.one")), (AssemblyFile, Assembly())));
        w.Kernel.Plugins.Uninstall("t.one");
        w.Kernel.Plugins.Uninstall("t.one");
        Assert.Single(w.KernelJson()["pendingRemoval"]!.AsArray());
    }

    [Fact]
    public void Uninstall_KeepsOtherKernelJsonContent()
    {
        using var w = new World();
        w.StartKernel();
        w.InstallOk(w.Zip(("plugin.json", Manifest("t.one")), (AssemblyFile, Assembly())));
        w.Kernel.Plugins.SetEnabled("t.one", true);
        w.Kernel.Plugins.Uninstall("t.one");
        Assert.True(w.KernelJson()["enabled"]!["t.one"]!.GetValue<bool>());
        w.Kernel.Plugins.SetEnabled("t.one", false);
        Assert.Equal(PluginState.PendingRemoval, w.State("t.one"));
        Assert.Single(w.KernelJson()["pendingRemoval"]!.AsArray());
    }

    // ---- undo ----------------------------------------------------------------------------------------

    [Fact]
    public void UndoUninstall_ReactivatesTheRunningPlugin()
    {
        using var w = new World();
        w.StartKernel();
        w.InstallOk(w.Zip(("plugin.json", Manifest("t.one")), (AssemblyFile, Assembly())));
        w.Kernel.Plugins.Uninstall("t.one");

        w.Kernel.Plugins.UndoUninstall("t.one");

        Assert.Equal(PluginState.Active, w.State("t.one"));
        Assert.Null(w.KernelJson()["pendingRemoval"]);
        w.Kernel.Shutdown();
        w.StartKernel();
        Assert.Equal(PluginState.Active, w.State("t.one"));
        Assert.True(Directory.Exists(Path.Combine(w.PluginsRoot, "t.one")));
    }

    [Fact]
    public void UndoUninstall_OfADisabledPluginLeavesItDisabled()
    {
        using var w = new World();
        w.StartKernel();
        w.InstallOk(w.Zip(("plugin.json", Manifest("t.one")), (AssemblyFile, Assembly())));
        w.Kernel.Plugins.SetEnabled("t.one", false);
        w.Kernel.Plugins.Uninstall("t.one");

        w.Kernel.Plugins.UndoUninstall("t.one");

        Assert.Equal(PluginState.Disabled, w.State("t.one"));
        Assert.False(w.Info("t.one").IsEnabled);
    }

    [Fact]
    public void UndoUninstall_WhenNotPendingDoesNothing()
    {
        using var w = new World();
        w.StartKernel();
        w.InstallOk(w.Zip(("plugin.json", Manifest("t.one")), (AssemblyFile, Assembly())));
        w.Kernel.Plugins.UndoUninstall("t.one");
        Assert.Equal(PluginState.Active, w.State("t.one"));
    }

    // ---- start-up queue safety -----------------------------------------------------------------------

    [Fact]
    public void Queue_InvalidIdsInKernelJsonNeverTouchOtherFolders()
    {
        using var w = new World();
        var victim = Path.Combine(w.DataDirectory, "victim");
        Directory.CreateDirectory(victim);
        File.WriteAllText(Path.Combine(victim, "important.txt"), "x");
        w.WriteKernelJson(new JsonObject
        {
            ["pendingRemoval"] = new JsonArray("../victim", "..\\victim", "", "C:/Windows"),
            ["pendingInstall"] = new JsonArray("../victim"),
        });

        w.StartKernel();

        Assert.True(File.Exists(Path.Combine(victim, "important.txt")));
        Assert.Null(w.KernelJson()["pendingRemoval"]);
        Assert.Null(w.KernelJson()["pendingInstall"]);
    }

    [Fact]
    public void Queue_StagedUpdateThatVanishedIsDropped()
    {
        using var w = new World();
        w.WriteKernelJson(new JsonObject { ["pendingInstall"] = new JsonArray("t.gone") });
        w.StartKernel();
        Assert.Null(w.KernelJson()["pendingInstall"]);
    }

    // ---- safe mode -----------------------------------------------------------------------------------

    [Fact]
    public void SafeMode_IgnoresUserPluginsButLoadsBuiltInOnes()
    {
        using var w = new World();
        w.StartKernel();
        w.InstallOk(w.Zip(("plugin.json", Manifest("t.one")), (AssemblyFile, Assembly())));
        w.Kernel.Shutdown();

        w.StartKernel("--safe-mode");

        Assert.DoesNotContain(w.Kernel.Plugins.Plugins, p => p.Manifest.Id == "t.one");
        Assert.Equal(PluginState.Active, w.State(World.BuiltInId));
        Assert.True(Directory.Exists(Path.Combine(w.PluginsRoot, "t.one")), "safe mode does not delete anything");

        w.Kernel.Shutdown();
        w.StartKernel();
        Assert.Equal(PluginState.Active, w.State("t.one"));
    }

    [Fact]
    public void SafeMode_InstallIsStagedUntilTheNextNormalStart()
    {
        using var w = new World();
        w.StartKernel("--safe-mode");

        var result = w.Install(w.Zip(("plugin.json", Manifest("t.one")), (AssemblyFile, Assembly())));

        Assert.True(result.Success, result.Error);
        Assert.True(result.RestartRequired);
        Assert.DoesNotContain(w.Kernel.Plugins.Plugins, p => p.Manifest.Id == "t.one");
        w.Kernel.Shutdown();

        w.StartKernel();
        Assert.Equal(PluginState.Active, w.State("t.one"));
    }

    // ---- inspect -------------------------------------------------------------------------------------

    [Fact]
    public void Inspect_ReturnsNameVersionAndAuthorWithoutInstalling()
    {
        using var w = new World();
        w.StartKernel();
        var zip = w.Zip(("plugin.json", Manifest("t.one", "1.2.3", author: "Ada", nameEn: "Hello", nameZh: "哈囉")), (AssemblyFile, Assembly()));

        var info = w.Wait(w.Kernel.Plugins.InspectAsync(zip));

        Assert.True(info.IsValid);
        Assert.NotNull(info.Manifest);
        Assert.Equal("t.one", info.Manifest.Id);
        Assert.Equal("1.2.3", info.Manifest.Version);
        Assert.Equal("Ada", info.Manifest.Author);
        Assert.Equal("Hello", info.Manifest.Name["en"]);
        Assert.Equal("哈囉", info.Manifest.Name["zh-TW"]);
        Assert.Null(info.InstalledVersion);
        Assert.Null(info.Error);
        Assert.False(Directory.Exists(Path.Combine(w.PluginsRoot, "t.one")));
        Assert.DoesNotContain(w.Kernel.Plugins.Plugins, p => p.Manifest.Id == "t.one");
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(w.PluginsRoot, ".staging")));
    }

    [Fact]
    public void Inspect_ReportsTheInstalledVersionForAnUpdate()
    {
        using var w = new World();
        w.StartKernel();
        w.InstallOk(w.Zip(("plugin.json", Manifest("t.one", "1.0.0")), (AssemblyFile, Assembly())));
        var info = w.Wait(w.Kernel.Plugins.InspectAsync(w.Zip(("plugin.json", Manifest("t.one", "2.0.0")), (AssemblyFile, Assembly()))));
        Assert.True(info.IsValid);
        Assert.Equal("1.0.0", info.InstalledVersion);
    }

    [Fact]
    public void Inspect_UsesTheSameRulesAsInstall()
    {
        using var w = new World();
        w.StartKernel();
        var builtIn = w.Wait(w.Kernel.Plugins.InspectAsync(w.Zip(("plugin.json", Manifest(World.BuiltInId)), (AssemblyFile, Assembly()))));
        Assert.False(builtIn.IsValid);
        Assert.Equal(PluginInstallError.BuiltInId, builtIn.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(builtIn.Error));

        var missing = w.Wait(w.Kernel.Plugins.InspectAsync(Path.Combine(w.Root, "missing.zip")));
        Assert.False(missing.IsValid);
        Assert.Equal(PluginInstallError.PackageNotFound, missing.ErrorCode);

        var slip = w.Wait(w.Kernel.Plugins.InspectAsync(w.Zip(("plugin.json", Manifest("t.one")), (AssemblyFile, Assembly()), ("../evil.dll", "x"))));
        Assert.Equal(PluginInstallError.UnsafePath, slip.ErrorCode);
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static string Manifest(string id, string version = "1.0.0", int apiVersion = 1, string author = "Tester", string nameEn = "Test", string nameZh = "測試") =>
        $$"""
        {
          "id": "{{id}}",
          "version": "{{version}}",
          "apiVersion": {{apiVersion}},
          "assembly": "{{AssemblyFile}}",
          "name": { "en": "{{nameEn}}", "zh-TW": "{{nameZh}}" },
          "author": "{{author}}"
        }
        """;

    private static byte[]? _assembly;

    /// <summary>A real assembly with one public <see cref="Plugin"/> subclass that does nothing.</summary>
    private static byte[] Assembly()
    {
        if (_assembly is not null)
        {
            return _assembly;
        }

        var builder = new PersistedAssemblyBuilder(new AssemblyName("Test.EmittedPlugin"), typeof(object).Assembly);
        var module = builder.DefineDynamicModule("Test.EmittedPlugin");
        var type = module.DefineType("TestPlugins.EmittedPlugin", TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed, typeof(Plugin));
        var ctor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
        var il = ctor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(Plugin).GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes)!);
        il.Emit(OpCodes.Ret);
        var activate = type.DefineMethod(
            "Activate",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            typeof(void),
            [typeof(IPluginContext)]);
        activate.GetILGenerator().Emit(OpCodes.Ret);
        type.CreateType();
        using var stream = new MemoryStream();
        builder.Save(stream);
        return _assembly = stream.ToArray();
    }

    /// <summary>Per-test folders (app + data), a built-in disk plugin, and helpers to start kernels and pump the dispatcher.</summary>
    private sealed class World : IDisposable
    {
        public const string BuiltInId = "test.builtin";

        private readonly Harness _harness = new();
        private int _packages;

        public World()
        {
            var builtIn = Path.Combine(AppDirectory, "plugins", BuiltInId);
            Directory.CreateDirectory(builtIn);
            File.WriteAllText(Path.Combine(builtIn, "plugin.json"), Manifest(BuiltInId));
            File.WriteAllBytes(Path.Combine(builtIn, AssemblyFile), Assembly());
            Directory.CreateDirectory(PluginsRoot);
        }

        public string Root => _harness.DataDirectory;

        public string AppDirectory => Path.Combine(Root, "app");

        public string DataDirectory => Path.Combine(Root, "data");

        public string PluginsRoot => Path.Combine(DataDirectory, "plugins");

        public Kernel Kernel { get; private set; } = null!;

        public Kernel StartKernel(params string[] args)
        {
            Kernel = new Kernel(new KernelOptions
            {
                AppDirectory = AppDirectory,
                DataDirectory = DataDirectory,
                Dispatcher = _harness.Dispatcher,
                Args = args,
            });
            Kernel.Start();
            Directory.CreateDirectory(Path.Combine(PluginsRoot, ".staging"));
            return Kernel;
        }

        public PluginInfo Info(string id) => Kernel.Plugins.Plugins.Single(p => p.Manifest.Id == id);

        public PluginState State(string id) => Info(id).State;

        public PluginInstallResult Install(string path) => Wait(Kernel.Plugins.InstallAsync(path));

        public void InstallOk(string path)
        {
            var result = Install(path);
            Assert.True(result.Success, $"{result.ErrorCode}: {result.Error}");
        }

        public T Wait<T>(Task<T> task)
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!task.IsCompleted)
            {
                _harness.Dispatcher.RunPending();
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("The task did not complete.");
                }

                Thread.Sleep(2);
            }

            _harness.Dispatcher.RunPending();
            return task.GetAwaiter().GetResult();
        }

        public string Zip(params (string Name, object Content)[] files) =>
            ZipBytes(files.Select(f => (f.Name, f.Content is byte[] bytes ? bytes : Utf8((string)f.Content))));

        public string ZipBytes(IEnumerable<(string Name, byte[] Data)> files)
        {
            var path = Path.Combine(Root, "packages", $"package{++_packages}.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var file = File.Create(path);
            using var zip = new ZipArchive(file, ZipArchiveMode.Create);
            foreach (var (name, data) in files)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
                using var stream = entry.Open();
                stream.Write(data);
            }

            return path;
        }

        public JsonNode KernelJson()
        {
            var path = Path.Combine(DataDirectory, "config", "kernel.json");
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))! : new JsonObject();
        }

        public void WriteKernelJson(JsonObject content)
        {
            Directory.CreateDirectory(Path.Combine(DataDirectory, "config"));
            File.WriteAllText(Path.Combine(DataDirectory, "config", "kernel.json"), content.ToJsonString());
        }

        /// <summary>A rejected install: stable code, an English message, and nothing left in the plugin folders.</summary>
        public void AssertRejected(PluginInstallResult result, PluginInstallError code)
        {
            Assert.False(result.Success);
            Assert.Equal(code, result.ErrorCode);
            Assert.False(result.RestartRequired);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
            Assert.DoesNotContain(Directory.GetDirectories(PluginsRoot), d => !Path.GetFileName(d).StartsWith('.'));
            var staging = Path.Combine(PluginsRoot, ".staging");
            Assert.True(!Directory.Exists(staging) || Directory.GetFileSystemEntries(staging).Length == 0, "nothing is left in the staging folder");
            Assert.Equal([BuiltInId], Kernel.Plugins.Plugins.Select(p => p.Manifest.Id));
        }

        public void Dispose() => _harness.Dispose();
    }
}
