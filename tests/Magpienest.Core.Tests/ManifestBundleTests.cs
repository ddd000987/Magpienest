namespace Magpienest.Core.Tests;

/// <summary>The optional "bundle" field of plugin.json.</summary>
public sealed class ManifestBundleTests
{
    [Fact]
    public void BundleIsReadWhenPresent()
    {
        var manifest = ManifestReader.Parse("""{ "id": "t.one", "version": "1.0.0", "assembly": "T.dll", "bundle": "t.group" }""");
        Assert.Equal("t.group", manifest.Bundle);
    }

    [Fact]
    public void BundleIsNullWhenAbsent()
    {
        var manifest = ManifestReader.Parse("""{ "id": "t.one", "version": "1.0.0", "assembly": "T.dll" }""");
        Assert.Null(manifest.Bundle);
    }

    [Theory]
    [InlineData("Bad_Id")]
    [InlineData("")]
    [InlineData("a..b")]
    public void InvalidBundleIdIsRejected(string bundle)
    {
        var json = $$"""{ "id": "t.one", "version": "1.0.0", "assembly": "T.dll", "bundle": "{{bundle}}" }""";
        Assert.Throws<InvalidDataException>(() => ManifestReader.Parse(json));
    }
}
