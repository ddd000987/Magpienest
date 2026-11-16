using Magpienest.Plugins.Catalogue;
using Magpienest.Sdk;
using static Magpienest.Plugins.Catalogue.Tests.FakePluginManager;

namespace Magpienest.Plugins.Catalogue.Tests;

public sealed class SemVerTests
{
    [Theory]
    [InlineData("1.0.0", "0.9.9", 1)]
    [InlineData("0.10.0", "0.9.0", 1)]
    [InlineData("1.0", "1.0.0", 0)]
    [InlineData("1.0.0", "1.0.0-beta", 1)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.10", -1)]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]
    [InlineData("1.0.0-1", "1.0.0-alpha", -1)]
    [InlineData("1.0.0+build.5", "1.0.0", 0)]
    [InlineData("garbage", "0.0.1", -1)]
    [InlineData("1.0.0", "", 1)]
    public void ComparesLikeSemanticVersions(string left, string right, int expected)
    {
        Assert.Equal(expected, Math.Sign(SemVer.Compare(left, right)));
        Assert.Equal(-expected, Math.Sign(SemVer.Compare(right, left)));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("1.2.3.4", true)]
    [InlineData("1.2.3.4.5", false)]
    [InlineData("1..2", false)]
    [InlineData("1.2-", false)]
    [InlineData("v1.2", false)]
    [InlineData("1.2.3-rc.1", true)]
    public void ValidatesVersions(string version, bool valid)
    {
        Assert.Equal(valid, SemVer.IsValid(version));
    }
}

public sealed class StoreStatusTests
{
    private static readonly CatalogueBundle Video = Sample.Bundle("m.video", "0.2.0", 1, Sample.Package("m.engine"), Sample.Package("m.video"));

    [Fact]
    public void NothingInstalledOffersInstall()
    {
        var entry = StoreStatus.Of(Video, [Plugin("other", "1.0.0")], 1);
        Assert.Equal(StoreBundleState.NotInstalled, entry.State);
        Assert.Null(entry.InstalledVersion);
    }

    [Fact]
    public void OtherApiVersionNeedsANewerMagpienest()
    {
        Assert.Equal(StoreBundleState.Incompatible, StoreStatus.Of(Video with { ApiVersion = 2 }, [], 1).State);
    }

    [Fact]
    public void SameVersionIsInstalled()
    {
        var entry = StoreStatus.Of(Video, [Plugin("m.engine", "0.2.0"), Plugin("m.video", "0.2.0")], 1);
        Assert.Equal(StoreBundleState.Installed, entry.State);
        Assert.Equal("0.2.0", entry.InstalledVersion);
    }

    [Fact]
    public void NewerCatalogueVersionOffersUpdate()
    {
        var entry = StoreStatus.Of(Video, [Plugin("m.engine", "0.1.0"), Plugin("m.video", "0.1.0")], 1);
        Assert.Equal(StoreBundleState.UpdateAvailable, entry.State);
        Assert.Equal("0.1.0", entry.InstalledVersion);
    }

    [Fact]
    public void CardVersionIsTheHighestMember()
    {
        var entry = StoreStatus.Of(Video, [Plugin("m.engine", "0.2.0"), Plugin("m.video", "0.1.9")], 1);
        Assert.Equal("0.2.0", entry.InstalledVersion);
        Assert.Equal(StoreBundleState.Installed, entry.State);
    }

    [Fact]
    public void MissingPackageOffersUpdate()
    {
        Assert.Equal(StoreBundleState.UpdateAvailable, StoreStatus.Of(Video, [Plugin("m.video", "0.2.0")], 1).State);
    }

    [Fact]
    public void MembersAreFoundByBundleIdToo()
    {
        var entry = StoreStatus.Of(Video, [Plugin("m.old", "0.1.0", bundle: "m.video"), Plugin("m.engine", "0.2.0"), Plugin("m.video", "0.2.0")], 1);
        Assert.Equal("0.2.0", entry.InstalledVersion);
    }

    [Fact]
    public void NewerVersionWithOtherApiIsNotOffered()
    {
        var entry = StoreStatus.Of(Video with { Version = "0.3.0", ApiVersion = 2 }, [Plugin("m.engine", "0.2.0"), Plugin("m.video", "0.2.0")], 1);
        Assert.Equal(StoreBundleState.Installed, entry.State);
    }

    [Fact]
    public void BuiltInMembersAreNeverUpdatedFromTheStore()
    {
        var entry = StoreStatus.Of(Video with { Version = "9.0.0" }, [Plugin("m.engine", "0.2.0", builtIn: true), Plugin("m.video", "0.2.0", builtIn: true)], 1);
        Assert.Equal(StoreBundleState.Installed, entry.State);
    }

    [Fact]
    public void AllMembersPendingRemoval()
    {
        var entry = StoreStatus.Of(Video, [Plugin("m.engine", "0.2.0", state: PluginState.PendingRemoval), Plugin("m.video", "0.2.0", state: PluginState.PendingRemoval)], 1);
        Assert.Equal(StoreBundleState.PendingRemoval, entry.State);
    }
}
