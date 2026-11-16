using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace Magpienest.Plugins.Catalogue;

/// <summary>
/// Where the catalogue is read from and which addresses downloads may use. Production policies come from
/// <see cref="ForRepository"/>; tests build one for 127.0.0.1.
/// </summary>
/// <param name="CatalogueUrl">Address of catalog.json.</param>
/// <param name="PackagePrefix">Every package url must start with this (compared case-insensitively).</param>
/// <param name="RequireHttps">False only in tests.</param>
/// <param name="CatalogueHosts">Hosts a catalogue request may be redirected to.</param>
/// <param name="PackageHosts">Hosts a package download may be redirected to.</param>
internal sealed partial record StorePolicy(
    Uri CatalogueUrl,
    string PackagePrefix,
    bool RequireHttps,
    IReadOnlySet<string> CatalogueHosts,
    IReadOnlySet<string> PackageHosts)
{
    private static readonly IReadOnlySet<string> GitHubCatalogueHosts =
        new[] { "raw.githubusercontent.com" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> GitHubPackageHosts =
        new[] { "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The policy for a store repository written as "owner/name"; null when <paramref name="repository"/> is
    /// empty or not of that form.
    /// </summary>
    public static StorePolicy? ForRepository(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            return null;
        }

        var text = repository.Trim();
        if (!RepositoryPattern().IsMatch(text) || text.EndsWith("/.", StringComparison.Ordinal) || text.EndsWith("/..", StringComparison.Ordinal))
        {
            return null;
        }

        return new StorePolicy(
            new Uri($"https://raw.githubusercontent.com/{text}/main/catalog.json"),
            $"https://github.com/{text}/releases/download/",
            RequireHttps: true,
            GitHubCatalogueHosts,
            GitHubPackageHosts);
    }

    /// <summary>True when a package may be downloaded from <paramref name="url"/>.</summary>
    public bool IsAllowedPackageUrl(Uri url) =>
        url.IsAbsoluteUri
        && SchemeAllowed(url)
        && string.IsNullOrEmpty(url.UserInfo)
        && url.AbsoluteUri.StartsWith(PackagePrefix, StringComparison.OrdinalIgnoreCase)
        && url.AbsoluteUri.Length > PackagePrefix.Length;

    /// <summary>True when a request may follow a redirect to <paramref name="target"/>.</summary>
    public bool IsAllowedRedirect(Uri target, bool package) =>
        target.IsAbsoluteUri
        && SchemeAllowed(target)
        && string.IsNullOrEmpty(target.UserInfo)
        && (package ? PackageHosts : CatalogueHosts).Contains(target.Host);

    private bool SchemeAllowed(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps || (!RequireHttps && url.Scheme == Uri.UriSchemeHttp);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepositoryPattern();
}
