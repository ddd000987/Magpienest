using System.Text;
using Magpienest.Plugins.Catalogue;
using Magpienest.Sdk;

namespace Magpienest.Plugins.Catalogue.Tests;

public sealed class BundleInstallerTests : IDisposable
{
    private readonly string _directory = Sample.TempDirectory();
    private readonly FakePluginManager _manager = new();
    private readonly NullLog _log = new();

    private static CatalogueBundle Video => Sample.Bundle("m.video", "0.2.0", 1, Sample.Package("m.engine", 100), Sample.Package("m.video", 20));

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task DownloadsEverythingThenInstallsInCatalogueOrder()
    {
        var downloader = new FakeDownloader();
        var outcome = await Installer(downloader).InstallAsync(Video, null, TestContext.Current.CancellationToken);

        Assert.True(outcome.Success);
        Assert.False(outcome.RestartRequired);
        Assert.Equal(["m.engine", "m.video"], downloader.Downloaded);
        Assert.Equal(["m.engine", "m.video"], _manager.InstallCalls.Select(FakePluginManager.IdOf));
        Assert.All(_manager.FileExistedAtInstall, Assert.True);
    }

    [Fact]
    public async Task ZipsAreDeletedAfterwards()
    {
        await Installer(new FakeDownloader()).InstallAsync(Video, null, TestContext.Current.CancellationToken);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task FailedDownloadInstallsNothing()
    {
        var outcome = await Installer(new FakeDownloader(failId: "m.video", StoreError.SizeMismatch)).InstallAsync(Video, null, TestContext.Current.CancellationToken);

        Assert.False(outcome.Success);
        Assert.Equal(StoreError.SizeMismatch, outcome.Error);
        Assert.Equal("m.video", outcome.PackageId);
        Assert.Empty(_manager.InstallCalls);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task RejectedPackageRollsBackWhatThisRunInstalled()
    {
        _manager.Result = path => FakePluginManager.IdOf(path) == "m.video"
            ? new PluginInstallResult(false, "m.video", false, "bad", PluginInstallError.AssemblyMissing)
            : new PluginInstallResult(true, FakePluginManager.IdOf(path), false, null);

        var outcome = await Installer(new FakeDownloader()).InstallAsync(Video, null, TestContext.Current.CancellationToken);

        Assert.False(outcome.Success);
        Assert.Equal(StoreError.InstallFailed, outcome.Error);
        Assert.Equal(PluginInstallError.AssemblyMissing, outcome.InstallError);
        Assert.Equal("m.video", outcome.PackageId);
        Assert.Equal(["m.engine"], _manager.Uninstalled);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task PluginsThatExistedBeforeAreNotRolledBack()
    {
        _manager.Installed.Add(FakePluginManager.Plugin("m.engine", "0.1.0"));
        _manager.Result = path => FakePluginManager.IdOf(path) == "m.video"
            ? new PluginInstallResult(false, "m.video", false, "bad", PluginInstallError.ManifestInvalid)
            : new PluginInstallResult(true, "m.engine", true, null);

        await Installer(new FakeDownloader()).InstallAsync(Video, null, TestContext.Current.CancellationToken);

        Assert.Empty(_manager.Uninstalled);
    }

    [Fact]
    public async Task PackageHoldingAnotherPluginIsRejectedAndUndone()
    {
        _manager.Result = path => new PluginInstallResult(true, FakePluginManager.IdOf(path) == "m.video" ? "evil.plugin" : FakePluginManager.IdOf(path), false, null);

        var outcome = await Installer(new FakeDownloader()).InstallAsync(Video, null, TestContext.Current.CancellationToken);

        Assert.False(outcome.Success);
        Assert.Equal(StoreError.InstallFailed, outcome.Error);
        Assert.Equal(["evil.plugin", "m.engine"], _manager.Uninstalled);
    }

    [Fact]
    public async Task EveryPackageIsCheckedBeforeTheFirstIsInstalled()
    {
        _manager.Inspection = path => new PluginPackageInfo(true,
            new PluginManifest { Id = FakePluginManager.IdOf(path) == "m.video" ? "evil.plugin" : FakePluginManager.IdOf(path), Version = "0.2.0", Assembly = "T.dll" },
            null, null);

        var outcome = await Installer(new FakeDownloader()).InstallAsync(Video, null, TestContext.Current.CancellationToken);

        Assert.False(outcome.Success);
        Assert.Equal(StoreError.InstallFailed, outcome.Error);
        Assert.Equal(PluginInstallError.ManifestInvalid, outcome.InstallError);
        Assert.Equal("m.video", outcome.PackageId);
        Assert.Equal(2, _manager.InspectCalls.Count);
        Assert.Empty(_manager.InstallCalls);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task PackageTheKernelWouldRefuseInstallsNothing()
    {
        _manager.Inspection = path => FakePluginManager.IdOf(path) == "m.video"
            ? new PluginPackageInfo(false, null, null, "unsafe", PluginInstallError.UnsafePath)
            : new PluginPackageInfo(true, new PluginManifest { Id = "m.engine", Version = "0.2.0", Assembly = "T.dll" }, null, null);

        var outcome = await Installer(new FakeDownloader()).InstallAsync(Video, null, TestContext.Current.CancellationToken);

        Assert.False(outcome.Success);
        Assert.Equal(PluginInstallError.UnsafePath, outcome.InstallError);
        Assert.Empty(_manager.InstallCalls);
    }

    [Fact]
    public async Task PackageWithAnotherVersionInstallsNothing()
    {
        _manager.Inspection = path => new PluginPackageInfo(true, new PluginManifest { Id = FakePluginManager.IdOf(path), Version = "0.1.0", Assembly = "T.dll" }, null, null);

        var outcome = await Installer(new FakeDownloader()).InstallAsync(Video, null, TestContext.Current.CancellationToken);

        Assert.False(outcome.Success);
        Assert.Equal("m.engine", outcome.PackageId);
        Assert.Empty(_manager.InstallCalls);
    }

    [Fact]
    public async Task UpdateNeedsARestart()
    {
        _manager.Result = path => new PluginInstallResult(true, FakePluginManager.IdOf(path), RestartRequired: FakePluginManager.IdOf(path) == "m.video", null);
        var outcome = await Installer(new FakeDownloader()).InstallAsync(Video, null, TestContext.Current.CancellationToken);
        Assert.True(outcome.Success);
        Assert.True(outcome.RestartRequired);
    }

    [Fact]
    public async Task CancelWhileDownloadingInstallsNothing()
    {
        using var cancel = new CancellationTokenSource();
        var downloader = new FakeDownloader
        {
            BeforeDownload = (package, _) =>
            {
                if (package.Id == "m.video")
                {
                    cancel.Cancel();
                }

                return Task.CompletedTask;
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Installer(downloader).InstallAsync(Video, null, cancel.Token));

        Assert.Empty(_manager.InstallCalls);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task ProgressIsWeightedBySize()
    {
        var reports = new List<double>();
        await Installer(new FakeDownloader()).InstallAsync(Video, new ProgressReporter<double>(reports.Add), TestContext.Current.CancellationToken);
        Assert.Equal([100 / 120d, 1d], reports);
    }

    [Fact]
    public async Task HashMismatchFromTheServerLeavesNothingInstalled()
    {
        await using var server = new TestServer();
        var good = Encoding.ASCII.GetBytes("engine");
        var bad = Encoding.ASCII.GetBytes("tampered");
        server.Serve("/releases/t/engine.zip", good);
        server.Serve("/releases/t/video.zip", bad);
        var bundle = Sample.Bundle("m.video", "0.2.0", 1,
            new CataloguePackage("m.engine", good.Length, new Uri(server.BaseUrl + "releases/t/engine.zip"), Sample.Sha(good)),
            new CataloguePackage("m.video", bad.Length, new Uri(server.BaseUrl + "releases/t/video.zip"), Sample.Sha(good)));
        using var client = new StoreClient(server.Policy, new Version(0, 3, 0));

        var outcome = await new BundleInstaller(_manager, client, _directory, _log).InstallAsync(bundle, null, TestContext.Current.CancellationToken);

        Assert.Equal(StoreError.HashMismatch, outcome.Error);
        Assert.Empty(_manager.InstallCalls);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task RealDownloadsAreInstalledFromTheLocalServer()
    {
        await using var server = new TestServer();
        var engine = Encoding.ASCII.GetBytes("engine");
        var video = Encoding.ASCII.GetBytes("video");
        server.Serve("/releases/t/engine.zip", engine);
        server.Serve("/releases/t/video.zip", video);
        var bundle = Sample.Bundle("m.video", "0.2.0", 1,
            new CataloguePackage("m.engine", engine.Length, new Uri(server.BaseUrl + "releases/t/engine.zip"), Sample.Sha(engine)),
            new CataloguePackage("m.video", video.Length, new Uri(server.BaseUrl + "releases/t/video.zip"), Sample.Sha(video)));
        using var client = new StoreClient(server.Policy, new Version(0, 3, 0));

        var outcome = await new BundleInstaller(_manager, client, _directory, _log).InstallAsync(bundle, null, TestContext.Current.CancellationToken);

        Assert.True(outcome.Success);
        Assert.Equal(["m.engine-0.2.0.zip", "m.video-0.2.0.zip"], _manager.InstallCalls.Select(Path.GetFileName));
        Assert.All(_manager.FileExistedAtInstall, Assert.True);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    private BundleInstaller Installer(IPackageDownloader downloader) => new(_manager, downloader, _directory, _log);
}
