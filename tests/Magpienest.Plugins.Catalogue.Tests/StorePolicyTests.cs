using Magpienest.Plugins.Catalogue;

namespace Magpienest.Plugins.Catalogue.Tests;

public sealed class StorePolicyTests
{
    private static readonly StorePolicy Official = StorePolicy.ForRepository("owner/Magpienest-Store")!;

    [Fact]
    public void AddressesComeFromTheRepository()
    {
        Assert.Equal("https://raw.githubusercontent.com/owner/Magpienest-Store/main/catalog.json", Official.CatalogueUrl.AbsoluteUri);
        Assert.Equal("https://github.com/owner/Magpienest-Store/releases/download/", Official.PackagePrefix);
        Assert.True(Official.RequireHttps);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("owner")]
    [InlineData("owner/")]
    [InlineData("/name")]
    [InlineData("owner/name/extra")]
    [InlineData("owner/..")]
    [InlineData("owner/.")]
    [InlineData("https://github.com/owner/name")]
    [InlineData("own er/name")]
    public void MalformedRepositoryIsNotConfigured(string? repository)
    {
        Assert.Null(StorePolicy.ForRepository(repository));
    }

    [Theory]
    [InlineData("https://github.com/owner/Magpienest-Store/releases/download/magpienest.video-0.2.0/magpienest.video-0.2.0.zip", true)]
    [InlineData("https://GITHUB.com/Owner/magpienest-store/releases/download/t/a.zip", true)]
    [InlineData("http://github.com/owner/Magpienest-Store/releases/download/t/a.zip", false)]
    [InlineData("https://github.com/owner/Magpienest/releases/download/t/a.zip", false)]
    [InlineData("https://github.com/owner/Magpienest-Store2/releases/download/t/a.zip", false)]
    [InlineData("https://github.com/other/Magpienest-Store/releases/download/t/a.zip", false)]
    [InlineData("https://github.com/owner/Magpienest-Store/releases/download/../../../evil/releases/download/t/a.zip", false)]
    [InlineData("https://github.com/owner/Magpienest-Store/archive/main.zip", false)]
    [InlineData("https://github.com/owner/Magpienest-Store/releases/download/", false)]
    [InlineData("https://github.com@evil.example/owner/Magpienest-Store/releases/download/t/a.zip", false)]
    [InlineData("https://evil.example/owner/Magpienest-Store/releases/download/t/a.zip", false)]
    [InlineData("https://objects.githubusercontent.com/owner/Magpienest-Store/releases/download/t/a.zip", false)]
    public void PackageMustBeInTheStoreRepositorysReleases(string url, bool allowed)
    {
        Assert.Equal(allowed, Official.IsAllowedPackageUrl(new Uri(url)));
    }

    [Theory]
    [InlineData("https://github.com/x", true)]
    [InlineData("https://objects.githubusercontent.com/x", true)]
    [InlineData("https://release-assets.githubusercontent.com/x", true)]
    [InlineData("http://release-assets.githubusercontent.com/x", false)]
    [InlineData("https://raw.githubusercontent.com/x", false)]
    [InlineData("https://githubusercontent.com.evil.example/x", false)]
    [InlineData("https://evil.example/x", false)]
    public void PackageRedirectsMayOnlyGoToGitHubDownloadHosts(string url, bool allowed)
    {
        Assert.Equal(allowed, Official.IsAllowedRedirect(new Uri(url), package: true));
    }

    [Theory]
    [InlineData("https://raw.githubusercontent.com/x", true)]
    [InlineData("https://github.com/x", false)]
    [InlineData("http://raw.githubusercontent.com/x", false)]
    public void CatalogueRedirectsStayOnRawGitHub(string url, bool allowed)
    {
        Assert.Equal(allowed, Official.IsAllowedRedirect(new Uri(url), package: false));
    }
}
