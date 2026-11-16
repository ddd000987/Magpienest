using System.Net;
using System.Net.Sockets;
using System.Text;
using Magpienest.Plugins.Catalogue;

namespace Magpienest.Plugins.Catalogue.Tests;

/// <summary>Downloads against <see cref="TestServer"/> on 127.0.0.1; never the network.</summary>
public sealed class StoreClientTests : IAsyncLifetime
{
    private static readonly byte[] Zip = Encoding.ASCII.GetBytes("PK fake package bytes for the test");

    private readonly TestServer _server = new();
    private readonly string _directory = Sample.TempDirectory();
    private StoreClient _client = null!;

    public ValueTask InitializeAsync()
    {
        _client = new StoreClient(_server.Policy, new Version(0, 3, 0));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task FetchesTheCatalogueWithOnlyAUserAgent()
    {
        _server.Serve("/catalog.json", """{ "schema": 1, "bundles": [] }""");

        var catalogue = await _client.FetchCatalogueAsync(TestContext.Current.CancellationToken);

        Assert.Empty(catalogue.Bundles);
        var request = Assert.Single(_server.Requests);
        Assert.Equal("Magpienest/0.3.0", request.Headers["User-Agent"]);
        Assert.False(request.Headers.ContainsKey("Cookie"));
        Assert.False(request.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task MissingCatalogueIsAnHttpError()
    {
        var ex = await Assert.ThrowsAsync<StoreException>(() => _client.FetchCatalogueAsync(TestContext.Current.CancellationToken));
        Assert.Equal(StoreError.HttpError, ex.Code);
    }

    [Fact]
    public async Task UnreachableServerIsOffline()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var policy = _server.Policy with { CatalogueUrl = new Uri($"http://127.0.0.1:{port}/catalog.json") };
        using var client = new StoreClient(policy, new Version(0, 3, 0));

        var ex = await Assert.ThrowsAsync<StoreException>(() => client.FetchCatalogueAsync(TestContext.Current.CancellationToken));
        Assert.Equal(StoreError.Offline, ex.Code);
    }

    [Fact]
    public async Task VerifiedDownloadIsRenamedToZip()
    {
        _server.Serve("/releases/t/a.zip", Zip);

        var path = await _client.DownloadAsync(Package(), "1.0.0", _directory, null, TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(_directory, "a.b-1.0.0.zip"), path);
        Assert.Equal(Zip, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal([Path.GetFileName(path)], Directory.GetFiles(_directory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task ProgressCountsBytes()
    {
        _server.Serve("/releases/t/a.zip", Zip);
        var reports = new List<long>();

        await _client.DownloadAsync(Package(), "1.0.0", _directory, new ProgressReporter<long>(reports.Add), TestContext.Current.CancellationToken);

        Assert.Equal(Zip.Length, reports[^1]);
    }

    [Fact]
    public async Task HashMismatchLeavesNothing()
    {
        _server.Serve("/releases/t/a.zip", Zip);
        await AssertFailsAsync(Package() with { Sha256 = new string('0', 64) }, StoreError.HashMismatch);
    }

    [Fact]
    public async Task ShorterDownloadIsASizeMismatch()
    {
        _server.Serve("/releases/t/a.zip", Zip);
        await AssertFailsAsync(Package() with { Size = Zip.Length + 1 }, StoreError.SizeMismatch);
    }

    [Fact]
    public async Task LargerAnnouncedLengthStopsBeforeDownloading()
    {
        var big = new byte[1000];
        _server.Serve("/releases/t/a.zip", big);
        await AssertFailsAsync(Package() with { Size = 900, Sha256 = Sample.Sha(big) }, StoreError.TooLarge);
    }

    [Fact]
    public async Task LargerBodyThanAnnouncedIsCutOff()
    {
        // The server claims no length, then sends more than size + 1 %.
        var big = new byte[1000];
        _server.Serve("/releases/t/a.zip", big, contentLength: -1);
        await AssertFailsAsync(Package() with { Size = 500, Sha256 = Sample.Sha(big) }, StoreError.TooLarge);
    }

    [Fact]
    public async Task UrlOutsideTheStoreIsRefusedWithoutARequest()
    {
        var package = Package() with { Url = new Uri(_server.BaseUrl + "other/a.zip") };
        await AssertFailsAsync(package, StoreError.UrlNotAllowed);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task AllowedRedirectIsFollowed()
    {
        _server.Redirect("/releases/t/a.zip", _server.BaseUrl + "storage/a.zip");
        _server.Serve("/storage/a.zip", Zip);

        var path = await _client.DownloadAsync(Package(), "1.0.0", _directory, null, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(path));
        Assert.Equal(["/releases/t/a.zip", "/storage/a.zip"], _server.Requests.Select(r => r.Path));
    }

    [Fact]
    public async Task RedirectToAnotherHostIsRefused()
    {
        _server.Redirect("/releases/t/a.zip", _server.BaseUrl.Replace("127.0.0.1", "localhost") + "storage/a.zip");
        _server.Serve("/storage/a.zip", Zip);

        await AssertFailsAsync(Package(), StoreError.UrlNotAllowed);
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task EndlessRedirectsStop()
    {
        _server.Redirect("/releases/t/a.zip", "/releases/t/a.zip");
        await AssertFailsAsync(Package(), StoreError.HttpError);
    }

    [Fact]
    public async Task HttpsIsRequiredByTheOfficialPolicy()
    {
        using var client = new StoreClient(StorePolicy.ForRepository("o/s")!, new Version(0, 3, 0));
        var package = Package() with { Url = new Uri("http://github.com/o/s/releases/download/t/a.zip") };

        var ex = await Assert.ThrowsAsync<StoreException>(() => client.DownloadAsync(package, "1.0.0", _directory, null, TestContext.Current.CancellationToken));
        Assert.Equal(StoreError.UrlNotAllowed, ex.Code);
    }

    [Fact]
    public async Task CancelledDownloadLeavesNothing()
    {
        _server.Serve("/releases/t/a.zip", Zip);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _client.DownloadAsync(Package(), "1.0.0", _directory, null, cancel.Token));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    private CataloguePackage Package() => new("a.b", Zip.Length, new Uri(_server.BaseUrl + "releases/t/a.zip"), Sample.Sha(Zip));

    private async Task AssertFailsAsync(CataloguePackage package, StoreError expected)
    {
        var ex = await Assert.ThrowsAsync<StoreException>(() => _client.DownloadAsync(package, "1.0.0", _directory, null, TestContext.Current.CancellationToken));
        Assert.Equal(expected, ex.Code);
        Assert.Empty(Directory.GetFiles(_directory));
    }
}
