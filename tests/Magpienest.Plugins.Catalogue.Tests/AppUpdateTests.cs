using System.Diagnostics;
using System.IO.Compression;
using Magpienest.Plugins.Catalogue;
using Magpienest.Sdk;

namespace Magpienest.Plugins.Catalogue.Tests;

/// <summary>The catalogue's "app" entry and getting a new Magpienest ready to apply.</summary>
public sealed class AppUpdateTests : IDisposable
{
    private readonly string _directory = Sample.TempDirectory();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public void TheCatalogueListsTheNewestApp()
    {
        var sha = new string('b', 64);
        var catalogue = CatalogueParser.Parse($$"""
            {
              "schema": 1,
              "bundles": [],
              "app": {
                "version": "0.2.0",
                "size": 1234,
                "url": "https://github.com/o/Magpienest/releases/download/v0.2.0/Magpienest-0.2.0-win-x64.zip",
                "sha256": "{{sha}}",
                "notes": { "en": "Automatic updates.", "zh-TW": "自動更新。" },
                "page": "https://github.com/o/Magpienest/releases/tag/v0.2.0"
              }
            }
            """);

        var app = Assert.IsType<CatalogueApp>(catalogue.App);
        Assert.Equal("0.2.0", app.Version);
        Assert.Equal(CatalogueApp.PackageId, app.Package.Id);
        Assert.Equal(1234, app.Package.Size);
        Assert.Equal(sha, app.Package.Sha256);
        Assert.Equal("自動更新。", app.NotesFor("zh-TW"));
        Assert.Equal("Automatic updates.", app.NotesFor("fr"));
        Assert.Equal("https://github.com/o/Magpienest/releases/tag/v0.2.0", app.Page?.AbsoluteUri);
    }

    [Fact]
    public void ABadAppEntryIsLeftOutAndTheBundlesStay()
    {
        var catalogue = CatalogueParser.Parse("""
            { "schema": 1, "bundles": [], "app": { "version": "0.2.0", "size": 5, "url": "https://x/y.zip", "sha256": "nope" } }
            """);

        Assert.Null(catalogue.App);
        Assert.Contains(catalogue.Rejected, r => r.StartsWith("App:", StringComparison.Ordinal));
        Assert.Null(CatalogueParser.Parse("""{ "schema": 1, "bundles": [] }""").App);
    }

    [Theory]
    [InlineData("https://github.com/owner/Magpienest/releases/download/v0.2.0/Magpienest-0.2.0-win-x64.zip", true)]
    [InlineData("https://github.com/owner/Magpienest-Store/releases/download/x/Magpienest.zip", true)]
    [InlineData("https://github.com/other/Magpienest/releases/download/v0.2.0/Magpienest.zip", false)]
    [InlineData("https://github.com/owner/Magpienest/archive/v0.2.0.zip", false)]
    [InlineData("http://github.com/owner/Magpienest/releases/download/v0.2.0/Magpienest.zip", false)]
    public void TheAppComesOnlyFromTheMainRepositoryOrTheStore(string url, bool allowed)
    {
        var policy = StorePolicy.ForRepository("owner/Magpienest-Store")!;

        Assert.Equal(allowed, policy.IsAllowedAppUrl(new Uri(url)));
        Assert.False(policy.IsAllowedPackageUrl(new Uri("https://github.com/owner/Magpienest/releases/download/v0.2.0/p.zip")));
    }

    [Theory]
    [InlineData("0.1.2", true)]
    [InlineData("0.2.0", true)]
    [InlineData("0.1.1", false)]
    [InlineData("0.1.0", false)]
    public void OnlyANewerVersionIsOffered(string listed, bool offered)
    {
        var updater = new AppUpdater(new FakeEnvironment(_directory, new Version(0, 1, 1)), new NullLog());
        var catalogue = new StoreCatalogue([], [], App(listed));

        Assert.Equal(offered, updater.NewerThanRunning(catalogue) is not null);
        Assert.Null(updater.NewerThanRunning(new StoreCatalogue([], [])));
    }

    [Fact]
    public async Task TheDownloadIsUnpackedAndItsExeChecked()
    {
        var (zip, version) = ReleaseZip();
        var environment = new FakeEnvironment(_directory, new Version(0, 0, 1));
        var updater = new AppUpdater(environment, new NullLog());

        var (outcome, exe) = await updater.PrepareAsync(App(version), new ZipDownloader(zip), null, TestContext.Current.CancellationToken);

        Assert.Equal(AppUpdateOutcome.Ready, outcome);
        Assert.Equal(Path.Combine(updater.UpdatesDirectory, version, "Magpienest", "Magpienest.exe"), exe);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "plugins", "readme.txt")));

        var arguments = updater.LaunchArguments(4242);
        Assert.Equal(["--apply-update", environment.AppDirectory, "--wait-for-pid", "4242", "--data-dir", environment.DataDirectory,
                      "--update-result", Path.Combine(updater.UpdatesDirectory, "result.json"), "--safe-mode"], arguments);
    }

    [Fact]
    public async Task AnExeOfAnotherVersionIsRefused()
    {
        var (zip, _) = ReleaseZip();
        var updater = new AppUpdater(new FakeEnvironment(_directory, new Version(0, 0, 1)), new NullLog());

        var (outcome, exe) = await updater.PrepareAsync(App("99.0.0"), new ZipDownloader(zip), null, TestContext.Current.CancellationToken);

        Assert.Equal(AppUpdateOutcome.Failed, outcome);
        Assert.Null(exe);
    }

    [Fact]
    public void TheResultOfTheLastUpdateIsReadOnceAndCleanedUp()
    {
        var updater = new AppUpdater(new FakeEnvironment(_directory, new Version(0, 2, 0)), new NullLog());
        Assert.Null(updater.TakeReport());

        Directory.CreateDirectory(Path.Combine(updater.UpdatesDirectory, "0.2.0"));
        File.WriteAllText(Path.Combine(updater.UpdatesDirectory, "result.json"), """{"version":"0.2.0","success":false,"error":"locked"}""");

        Assert.Equal(new AppUpdateReport("0.2.0", false, "locked"), updater.TakeReport());
        Assert.False(Directory.Exists(updater.UpdatesDirectory));
        Assert.Null(updater.TakeReport());
    }

    private static CatalogueApp App(string version) =>
        new(version, Sample.Package(CatalogueApp.PackageId, 10, "https://github.com/o/Magpienest/releases/download/v/x.zip"), new Dictionary<string, string>(), null);

    /// <summary>A zip shaped like the release: Magpienest\Magpienest.exe (a real versioned file) and a plugins folder.</summary>
    private (string Zip, string Version) ReleaseZip()
    {
        var versioned = typeof(AppUpdater).Assembly.Location;
        var version = (FileVersionInfo.GetVersionInfo(versioned).ProductVersion ?? "0.0.0").Split('+')[0];
        var stage = Path.Combine(_directory, "stage", "Magpienest");
        Directory.CreateDirectory(Path.Combine(stage, "plugins"));
        File.Copy(versioned, Path.Combine(stage, "Magpienest.exe"));
        File.WriteAllText(Path.Combine(stage, "plugins", "readme.txt"), "x");
        var zip = Path.Combine(_directory, "release.zip");
        ZipFile.CreateFromDirectory(stage, zip, CompressionLevel.Fastest, includeBaseDirectory: true);
        return (zip, version);
    }

    private sealed class ZipDownloader(string zip) : IPackageDownloader
    {
        public Task<string> DownloadAsync(CataloguePackage package, string version, string directory, IProgress<long>? progress, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, $"{package.Id}-{version}.zip");
            File.Copy(zip, target, overwrite: true);
            return Task.FromResult(target);
        }
    }

    private sealed class FakeEnvironment : IAppEnvironment
    {
        public FakeEnvironment(string root, Version version)
        {
            AppVersion = version;
            AppDirectory = Path.Combine(root, "app");
            DataDirectory = Path.Combine(root, "data");
            Directory.CreateDirectory(AppDirectory);
        }

        public Version AppVersion { get; }

        public string AppDirectory { get; }

        public string DataDirectory { get; }

        public string CacheDirectory => Path.Combine(DataDirectory, "cache");

        public IReadOnlyList<string> CommandLineArgs { get; } = ["--safe-mode"];

        public bool IsStarted => true;

        public void RequestShutdown(int exitCode = 0) => throw new NotSupportedException();
    }
}
