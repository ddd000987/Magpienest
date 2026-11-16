namespace Magpienest.Sdk;

/// <summary>
/// Why a plugin package was rejected. The kernel has no localization, so
/// <see cref="PluginInstallResult.Error"/> is an English log-style sentence; UI code maps this code to a localized text.
/// </summary>
public enum PluginInstallError
{
    /// <summary>No error.</summary>
    None,

    /// <summary>The path does not exist.</summary>
    PackageNotFound,

    /// <summary>The path is neither a readable .zip file nor a folder.</summary>
    InvalidPackage,

    /// <summary>More than 5000 entries.</summary>
    TooManyEntries,

    /// <summary>More than 500 MB when uncompressed.</summary>
    TooLarge,

    /// <summary>An entry would be written outside the plugin folder (zip-slip) or has an invalid path.</summary>
    UnsafePath,

    /// <summary>The package contains Magpienest.Sdk.dll, Magpienest.Core.dll, Magpienest.exe, Magpienest.dll or Magpienest.App.*.</summary>
    ForbiddenFile,

    /// <summary>No plugin.json at the root or inside exactly one top-level folder.</summary>
    ManifestMissing,

    /// <summary>plugin.json cannot be parsed or lacks required fields.</summary>
    ManifestInvalid,

    /// <summary>The id does not match [a-z0-9.]+ (lower-case letters, digits, dots).</summary>
    InvalidId,

    /// <summary>The plugin was built for a plugin API version this kernel does not support.</summary>
    UnsupportedApiVersion,

    /// <summary>The assembly named in plugin.json is not in the package.</summary>
    AssemblyMissing,

    /// <summary>The id belongs to a built-in plugin.</summary>
    BuiltInId,

    /// <summary>Another install is still running.</summary>
    Busy,

    /// <summary>Reading or writing files failed.</summary>
    IoError,
}

/// <summary>Result of <see cref="IPluginManager.InstallAsync"/>.</summary>
/// <param name="Success">True when the plugin was installed (or staged).</param>
/// <param name="PluginId">The plugin's id; set whenever the manifest could be read.</param>
/// <param name="RestartRequired">True when the package was staged and takes effect at the next start.</param>
/// <param name="Error">English description of the failure; null on success.</param>
/// <param name="ErrorCode">Machine-readable failure reason; <see cref="PluginInstallError.None"/> on success.</param>
public sealed record PluginInstallResult(
    bool Success,
    string? PluginId,
    bool RestartRequired,
    string? Error,
    PluginInstallError ErrorCode = PluginInstallError.None);

/// <summary>Result of <see cref="IPluginManager.InspectAsync"/>.</summary>
/// <param name="IsValid">True when the package would be accepted by <see cref="IPluginManager.InstallAsync"/>.</param>
/// <param name="Manifest">The package's manifest (name, version, author, ...); set whenever it could be read.</param>
/// <param name="InstalledVersion">Version of the plugin with the same id that is already installed, or null (a new plugin).</param>
/// <param name="Error">English description of the failure; null when valid.</param>
/// <param name="ErrorCode">Machine-readable failure reason; <see cref="PluginInstallError.None"/> when valid.</param>
public sealed record PluginPackageInfo(
    bool IsValid,
    PluginManifest? Manifest,
    string? InstalledVersion,
    string? Error,
    PluginInstallError ErrorCode = PluginInstallError.None);
