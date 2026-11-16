using Magpienest.Sdk;

namespace Magpienest.Plugins.Catalogue;

/// <summary>What the store offers for one bundle.</summary>
internal enum StoreBundleState
{
    /// <summary>None of its plugins is installed: "Install".</summary>
    NotInstalled,

    /// <summary>Installed and up to date: "Installed".</summary>
    Installed,

    /// <summary>Installed, and the catalogue has a newer version (or a missing package): "Update".</summary>
    UpdateAvailable,

    /// <summary>Not installable by this version of Magpienest: "Needs a newer Magpienest".</summary>
    Incompatible,

    /// <summary>Every installed member waits for removal at the next start.</summary>
    PendingRemoval,
}

/// <summary>A catalogue bundle with its state on this computer.</summary>
/// <param name="Bundle">The catalogue entry.</param>
/// <param name="State">What the store offers.</param>
/// <param name="InstalledVersion">Highest installed member version, or null.</param>
internal sealed record StoreEntry(CatalogueBundle Bundle, StoreBundleState State, string? InstalledVersion);

/// <summary>Compares the catalogue with the installed plugins.</summary>
internal static class StoreStatus
{
    /// <summary>The state of every bundle in the catalogue.</summary>
    /// <param name="catalogue">The catalogue.</param>
    /// <param name="plugins">The installed plugins.</param>
    /// <param name="apiVersion">The plugin API version of this Magpienest.</param>
    public static IReadOnlyList<StoreEntry> Of(StoreCatalogue catalogue, IReadOnlyList<PluginInfo> plugins, int apiVersion) =>
        catalogue.Bundles.Select(b => Of(b, plugins, apiVersion)).ToList();

    /// <summary>The state of one bundle.</summary>
    public static StoreEntry Of(CatalogueBundle bundle, IReadOnlyList<PluginInfo> plugins, int apiVersion)
    {
        var packageIds = bundle.Packages.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var members = plugins
            .Where(p => p.Manifest.Bundle == bundle.Id || packageIds.Contains(p.Manifest.Id))
            .ToList();
        var compatible = bundle.ApiVersion == apiVersion;

        if (members.Count == 0)
        {
            return new StoreEntry(bundle, compatible ? StoreBundleState.NotInstalled : StoreBundleState.Incompatible, null);
        }

        var installed = members.Select(m => m.Manifest.Version).Aggregate((a, b) => SemVer.Compare(a, b) >= 0 ? a : b);
        if (members.All(m => m.State == PluginState.PendingRemoval))
        {
            return new StoreEntry(bundle, StoreBundleState.PendingRemoval, installed);
        }

        // Built-in plugins cannot be replaced by a download.
        var updatable = compatible && !members.Any(m => m.IsBuiltIn);
        var newer = SemVer.Compare(bundle.Version, installed) > 0;
        var missing = packageIds.Any(id => !members.Any(m => m.Manifest.Id == id));
        return new StoreEntry(bundle, updatable && (newer || missing) ? StoreBundleState.UpdateAvailable : StoreBundleState.Installed, installed);
    }
}
