using System.Text.Json;
using System.Text.RegularExpressions;

namespace Magpienest.Plugins.Catalogue;

/// <summary>Why a store operation failed. UI code maps each code to a localized text.</summary>
internal enum StoreError
{
    /// <summary>No store repository is configured.</summary>
    NotConfigured,

    /// <summary>The server could not be reached, or it stopped answering.</summary>
    Offline,

    /// <summary>The server answered with an error status, or redirected too often.</summary>
    HttpError,

    /// <summary>The catalogue is not valid JSON or lacks required fields.</summary>
    BadCatalogue,

    /// <summary>The catalogue's schema number is not one this version understands.</summary>
    UnknownSchema,

    /// <summary>A download address, or a redirect, is outside the allowed places.</summary>
    UrlNotAllowed,

    /// <summary>A download is larger than the catalogue says (or larger than 500 MB).</summary>
    TooLarge,

    /// <summary>A download is not exactly as large as the catalogue says.</summary>
    SizeMismatch,

    /// <summary>A download's SHA-256 differs from the catalogue.</summary>
    HashMismatch,

    /// <summary>The kernel rejected a downloaded package.</summary>
    InstallFailed,

    /// <summary>Writing the download failed.</summary>
    IoError,

    /// <summary>Another store install is still running.</summary>
    Busy,
}

/// <summary>A store failure with its code; the message is English, for the log.</summary>
internal sealed class StoreException(StoreError code, string message, Exception? inner = null) : Exception(message, inner)
{
    public StoreError Code { get; } = code;
}

/// <summary>One plugin package of a bundle.</summary>
/// <param name="Id">Plugin id inside the package.</param>
/// <param name="Size">Exact size of the zip in bytes.</param>
/// <param name="Url">Download address.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the zip.</param>
internal sealed record CataloguePackage(string Id, long Size, Uri Url, string Sha256);

/// <summary>A bundle offered by the store: plugins installed together.</summary>
internal sealed record CatalogueBundle(
    string Id,
    IReadOnlyDictionary<string, string> Name,
    IReadOnlyDictionary<string, string> Description,
    string? Author,
    string Version,
    int ApiVersion,
    IReadOnlyList<CataloguePackage> Packages)
{
    /// <summary>Sum of the package sizes.</summary>
    public long TotalSize => Packages.Sum(p => p.Size);

    /// <summary>Name in <paramref name="language"/>, else English, else the id.</summary>
    public string NameFor(string language) => Texts.Pick(Name, language) ?? Id;

    /// <summary>Description in <paramref name="language"/>, else English, else empty.</summary>
    public string DescriptionFor(string language) => Texts.Pick(Description, language) ?? string.Empty;
}

/// <summary>The newest Magpienest release, as the catalogue's optional "app" entry lists it.</summary>
/// <param name="Version">Version of the release.</param>
/// <param name="Package">The release zip (id <see cref="CatalogueApp.PackageId"/>).</param>
/// <param name="Notes">Short release notes by language.</param>
/// <param name="Page">The release page, or null.</param>
internal sealed record CatalogueApp(string Version, CataloguePackage Package, IReadOnlyDictionary<string, string> Notes, Uri? Page)
{
    /// <summary>The id the app's zip is downloaded under.</summary>
    public const string PackageId = "magpienest";

    /// <summary>Notes in <paramref name="language"/>, else English, else empty.</summary>
    public string NotesFor(string language) => Texts.Pick(Notes, language) ?? string.Empty;
}

/// <summary>A parsed catalogue.</summary>
/// <param name="Bundles">Valid bundles, in file order.</param>
/// <param name="Rejected">English reasons for entries that were left out.</param>
/// <param name="App">The newest Magpienest release, or null when the catalogue lists none (or an invalid one).</param>
internal sealed record StoreCatalogue(IReadOnlyList<CatalogueBundle> Bundles, IReadOnlyList<string> Rejected, CatalogueApp? App = null);

/// <summary>Picks a text for a language.</summary>
internal static class Texts
{
    public static string? Pick(IReadOnlyDictionary<string, string> texts, string language)
    {
        if (texts.TryGetValue(language, out var exact) && !string.IsNullOrWhiteSpace(exact))
        {
            return exact;
        }

        if (texts.TryGetValue("en", out var english) && !string.IsNullOrWhiteSpace(english))
        {
            return english;
        }

        return texts.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }
}

/// <summary>Reads catalog.json.</summary>
internal static partial class CatalogueParser
{
    /// <summary>The only schema this version understands.</summary>
    public const int Schema = 1;

    /// <summary>
    /// Parses the catalogue. Throws <see cref="StoreException"/> (<see cref="StoreError.BadCatalogue"/> or
    /// <see cref="StoreError.UnknownSchema"/>) when the file as a whole is unusable; an invalid bundle is left out
    /// and listed in <see cref="StoreCatalogue.Rejected"/>.
    /// </summary>
    public static StoreCatalogue Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json.TrimStart('﻿'), new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException ex)
        {
            throw new StoreException(StoreError.BadCatalogue, $"The catalogue is not valid JSON: {ex.Message}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new StoreException(StoreError.BadCatalogue, "The catalogue is not a JSON object.");
            }

            if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var number))
            {
                throw new StoreException(StoreError.BadCatalogue, "The catalogue has no schema number.");
            }

            if (number != Schema)
            {
                throw new StoreException(StoreError.UnknownSchema, $"Catalogue schema {number} is not supported (expected {Schema}).");
            }

            if (!root.TryGetProperty("bundles", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                throw new StoreException(StoreError.BadCatalogue, "The catalogue has no bundles array.");
            }

            var bundles = new List<CatalogueBundle>();
            var rejected = new List<string>();
            var index = 0;
            foreach (var element in list.EnumerateArray())
            {
                try
                {
                    var bundle = ReadBundle(element);
                    if (bundles.Any(b => b.Id == bundle.Id))
                    {
                        throw new FormatException($"bundle '{bundle.Id}' is listed twice");
                    }

                    bundles.Add(bundle);
                }
                catch (FormatException ex)
                {
                    rejected.Add($"Bundle {index}: {ex.Message}.");
                }

                index++;
            }

            CatalogueApp? app = null;
            if (root.TryGetProperty("app", out var appElement))
            {
                try
                {
                    app = ReadApp(appElement);
                }
                catch (FormatException ex)
                {
                    rejected.Add($"App: {ex.Message}.");
                }
            }

            return new StoreCatalogue(bundles, rejected, app);
        }
    }

    /// <summary>Same rule as plugin ids: lower-case letters and digits separated by single dots.</summary>
    public static bool IsValidId(string? id) => !string.IsNullOrWhiteSpace(id) && IdPattern().IsMatch(id);

    private static CatalogueBundle ReadBundle(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("not an object");
        }

        var id = String(element, "id") ?? throw new FormatException("no id");
        if (!IsValidId(id))
        {
            throw new FormatException($"invalid id '{id}'");
        }

        var name = TextMap(element, "name");
        if (name.Count == 0)
        {
            throw new FormatException($"'{id}' has no name");
        }

        var version = String(element, "version");
        if (!SemVer.IsValid(version))
        {
            throw new FormatException($"'{id}' has an invalid version");
        }

        if (!element.TryGetProperty("apiVersion", out var api) || api.ValueKind != JsonValueKind.Number || !api.TryGetInt32(out var apiVersion) || apiVersion < 1)
        {
            throw new FormatException($"'{id}' has no valid apiVersion");
        }

        if (!element.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Array || plugins.GetArrayLength() == 0)
        {
            throw new FormatException($"'{id}' lists no plugins");
        }

        var packages = new List<CataloguePackage>();
        foreach (var plugin in plugins.EnumerateArray())
        {
            var package = ReadPackage(id, plugin);
            if (packages.Any(p => p.Id == package.Id))
            {
                throw new FormatException($"'{id}' lists '{package.Id}' twice");
            }

            packages.Add(package);
        }

        return new CatalogueBundle(id, name, TextMap(element, "description"), String(element, "author"), version!.Trim(), apiVersion, packages);
    }

    /// <summary>{ "version", "size", "url", "sha256", "notes": { language: text }, "page" }.</summary>
    private static CatalogueApp ReadApp(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("not an object");
        }

        var version = String(element, "version");
        if (!SemVer.IsValid(version))
        {
            throw new FormatException("invalid version");
        }

        if (!element.TryGetProperty("size", out var sizeElement) || sizeElement.ValueKind != JsonValueKind.Number || !sizeElement.TryGetInt64(out var size) || size <= 0)
        {
            throw new FormatException("no valid size");
        }

        var url = String(element, "url");
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new FormatException("no valid url");
        }

        var sha = String(element, "sha256");
        if (sha is null || !HexPattern().IsMatch(sha))
        {
            throw new FormatException("no valid sha256");
        }

        Uri? page = null;
        if (String(element, "page") is { } pageText && Uri.TryCreate(pageText, UriKind.Absolute, out var pageUri) && pageUri.Scheme == Uri.UriSchemeHttps)
        {
            page = pageUri;
        }

        return new CatalogueApp(version!.Trim(), new CataloguePackage(CatalogueApp.PackageId, size, uri, sha.ToLowerInvariant()), TextMap(element, "notes"), page);
    }

    private static CataloguePackage ReadPackage(string bundle, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"'{bundle}' has a plugin entry that is not an object");
        }

        var id = String(element, "id");
        if (!IsValidId(id))
        {
            throw new FormatException($"'{bundle}' has a plugin with an invalid id");
        }

        if (!element.TryGetProperty("size", out var sizeElement) || sizeElement.ValueKind != JsonValueKind.Number || !sizeElement.TryGetInt64(out var size) || size <= 0)
        {
            throw new FormatException($"'{id}' has no valid size");
        }

        var url = String(element, "url");
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new FormatException($"'{id}' has no valid url");
        }

        var sha = String(element, "sha256");
        if (sha is null || !HexPattern().IsMatch(sha))
        {
            throw new FormatException($"'{id}' has no valid sha256");
        }

        return new CataloguePackage(id!, size, uri, sha.ToLowerInvariant());
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static Dictionary<string, string> TextMap(JsonElement element, string name)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    map[property.Name] = property.Value.GetString()!;
                }
            }
        }

        return map;
    }

    [GeneratedRegex("^[a-z0-9]+(\\.[a-z0-9]+)*$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex HexPattern();
}
