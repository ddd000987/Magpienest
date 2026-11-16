using Magpienest.Plugins.Catalogue;

namespace Magpienest.Plugins.Catalogue.Tests;

public sealed class CatalogueParserTests
{
    private static readonly string Hex = new('a', 64);

    [Fact]
    public void ReadsBundlesWithTheirPackagesInOrder()
    {
        var catalogue = CatalogueParser.Parse(Catalogue(Bundle("magpienest.video", Package("magpienest.mediaengine.libvlc", 100), Package("magpienest.video", 20))));

        var bundle = Assert.Single(catalogue.Bundles);
        Assert.Empty(catalogue.Rejected);
        Assert.Equal("magpienest.video", bundle.Id);
        Assert.Equal("0.2.0", bundle.Version);
        Assert.Equal(1, bundle.ApiVersion);
        Assert.Equal("Magpienest", bundle.Author);
        Assert.Equal(["magpienest.mediaengine.libvlc", "magpienest.video"], bundle.Packages.Select(p => p.Id));
        Assert.Equal(120, bundle.TotalSize);
    }

    [Fact]
    public void NamesFallBackToEnglish()
    {
        var bundle = Assert.Single(CatalogueParser.Parse(Catalogue(Bundle("a.b", Package("a.b", 1)))).Bundles);
        Assert.Equal("影片", bundle.NameFor("zh-TW"));
        Assert.Equal("Videos", bundle.NameFor("ja"));
        Assert.Equal("Videos", bundle.DescriptionFor("fr"));
    }

    [Fact]
    public void HashIsStoredInLowerCase()
    {
        var json = Catalogue(Bundle("a.b", $$"""{ "id": "a.b", "size": 1, "url": "https://github.com/o/s/releases/download/t/a.zip", "sha256": "{{new string('A', 64)}}" }"""));
        Assert.Equal(Hex, Assert.Single(Assert.Single(CatalogueParser.Parse(json).Bundles).Packages).Sha256);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "bundles": [] }""")]
    [InlineData("""{ "schema": 1 }""")]
    [InlineData("""{ "schema": "1", "bundles": [] }""")]
    public void UnusableFileIsABadCatalogue(string json)
    {
        var ex = Assert.Throws<StoreException>(() => CatalogueParser.Parse(json));
        Assert.Equal(StoreError.BadCatalogue, ex.Code);
    }

    [Fact]
    public void UnknownSchemaIsReported()
    {
        var ex = Assert.Throws<StoreException>(() => CatalogueParser.Parse("""{ "schema": 2, "bundles": [] }"""));
        Assert.Equal(StoreError.UnknownSchema, ex.Code);
    }

    [Theory]
    [InlineData("""{ "name": { "en": "X" }, "version": "1.0.0", "apiVersion": 1, "plugins": [PKG] }""")]
    [InlineData("""{ "id": "Bad_Id", "name": { "en": "X" }, "version": "1.0.0", "apiVersion": 1, "plugins": [PKG] }""")]
    [InlineData("""{ "id": "a.b", "version": "1.0.0", "apiVersion": 1, "plugins": [PKG] }""")]
    [InlineData("""{ "id": "a.b", "name": { "en": "X" }, "version": "one", "apiVersion": 1, "plugins": [PKG] }""")]
    [InlineData("""{ "id": "a.b", "name": { "en": "X" }, "version": "1.0.0", "plugins": [PKG] }""")]
    [InlineData("""{ "id": "a.b", "name": { "en": "X" }, "version": "1.0.0", "apiVersion": 1, "plugins": [] }""")]
    [InlineData("""{ "id": "a.b", "name": { "en": "X" }, "version": "1.0.0", "apiVersion": 1, "plugins": [PKG, PKG] }""")]
    [InlineData("""{ "id": "a.b", "name": { "en": "X" }, "version": "1.0.0", "apiVersion": 1, "plugins": [{ "id": "a.b", "size": 0, "url": "https://github.com/x", "sha256": "HEX" }] }""")]
    [InlineData("""{ "id": "a.b", "name": { "en": "X" }, "version": "1.0.0", "apiVersion": 1, "plugins": [{ "id": "a.b", "size": 1, "url": "ftp://github.com/x", "sha256": "HEX" }] }""")]
    [InlineData("""{ "id": "a.b", "name": { "en": "X" }, "version": "1.0.0", "apiVersion": 1, "plugins": [{ "id": "a.b", "size": 1, "url": "https://github.com/x", "sha256": "abc" }] }""")]
    [InlineData("""{ "id": "a.b", "name": { "en": "X" }, "version": "1.0.0", "apiVersion": 1, "plugins": [{ "id": "a.b", "size": 1, "url": "https://github.com/x", "sha256": "HEXG" }] }""")]
    [InlineData("""{ "id": "a.b", "name": { "en": "X" }, "version": "1.0.0", "apiVersion": 1, "plugins": [{ "size": 1, "url": "https://github.com/x", "sha256": "HEX" }] }""")]
    public void InvalidBundleIsLeftOutAndTheOthersAreKept(string bad)
    {
        var package = $$"""{ "id": "a.b", "size": 1, "url": "https://github.com/x", "sha256": "{{Hex}}" }""";
        var json = Catalogue(
            bad.Replace("PKG", package).Replace("HEXG", Hex[..63] + "g").Replace("HEX", Hex),
            Bundle("good.one", Package("good.one", 1)));

        var catalogue = CatalogueParser.Parse(json);

        Assert.Equal("good.one", Assert.Single(catalogue.Bundles).Id);
        Assert.Single(catalogue.Rejected);
    }

    [Fact]
    public void DuplicateBundleKeepsTheFirst()
    {
        var catalogue = CatalogueParser.Parse(Catalogue(Bundle("a.b", Package("a.b", 1)), Bundle("a.b", Package("a.b", 2))));
        Assert.Equal(1, Assert.Single(catalogue.Bundles).TotalSize);
        Assert.Single(catalogue.Rejected);
    }

    [Fact]
    public void ByteOrderMarkIsAccepted()
    {
        Assert.Empty(CatalogueParser.Parse("﻿" + Catalogue()).Bundles);
    }

    private static string Catalogue(params string[] bundles) => $$"""{ "schema": 1, "bundles": [{{string.Join(",", bundles)}}] }""";

    private static string Bundle(string id, params string[] packages) => $$"""
        {
          "id": "{{id}}",
          "name": { "en": "Videos", "zh-TW": "影片" },
          "description": { "en": "Videos" },
          "author": "Magpienest",
          "version": "0.2.0",
          "apiVersion": 1,
          "plugins": [{{string.Join(",", packages)}}]
        }
        """;

    private static string Package(string id, long size) =>
        $$"""{ "id": "{{id}}", "size": {{size}}, "url": "https://github.com/o/s/releases/download/t/{{id}}.zip", "sha256": "{{Hex}}" }""";
}
