using Magpienest.Sdk;

namespace Magpienest.Plugins.Settings;

/// <summary>Maps kernel install results to localization keys. Pure logic, no UI.</summary>
internal static class PluginInstallMessages
{
    /// <summary>The localization key of the message for an install error; unknown or empty codes get a generic message.</summary>
    public static string KeyFor(PluginInstallError code) => code switch
    {
        PluginInstallError.PackageNotFound => "Settings.Plugins.Error.PackageNotFound",
        PluginInstallError.InvalidPackage => "Settings.Plugins.Error.InvalidPackage",
        PluginInstallError.TooManyEntries => "Settings.Plugins.Error.TooManyEntries",
        PluginInstallError.TooLarge => "Settings.Plugins.Error.TooLarge",
        PluginInstallError.UnsafePath => "Settings.Plugins.Error.UnsafePath",
        PluginInstallError.ForbiddenFile => "Settings.Plugins.Error.ForbiddenFile",
        PluginInstallError.ManifestMissing => "Settings.Plugins.Error.ManifestMissing",
        PluginInstallError.ManifestInvalid => "Settings.Plugins.Error.ManifestInvalid",
        PluginInstallError.InvalidId => "Settings.Plugins.Error.InvalidId",
        PluginInstallError.UnsupportedApiVersion => "Settings.Plugins.Error.UnsupportedApiVersion",
        PluginInstallError.AssemblyMissing => "Settings.Plugins.Error.AssemblyMissing",
        PluginInstallError.BuiltInId => "Settings.Plugins.Error.BuiltInId",
        PluginInstallError.Busy => "Settings.Plugins.Error.Busy",
        PluginInstallError.IoError => "Settings.Plugins.Error.IoError",
        _ => "Settings.Plugins.Error.Generic",
    };

    /// <summary>
    /// The key of the "restart Magpienest" bar text, or null when no restart is needed.
    /// </summary>
    /// <param name="updatePending">A staged update waits for the next start.</param>
    /// <param name="removalPending">At least one plugin is listed as pending removal.</param>
    public static string? RestartBarKey(bool updatePending, bool removalPending) => (updatePending, removalPending) switch
    {
        (true, true) => "Settings.Plugins.RestartBar.Both",
        (true, false) => "Settings.Plugins.RestartBar.Update",
        (false, true) => "Settings.Plugins.RestartBar.Remove",
        _ => null,
    };
}
