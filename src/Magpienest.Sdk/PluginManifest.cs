namespace Magpienest.Sdk;

/// <summary>The contents of a plugin's plugin.json.</summary>
public sealed record PluginManifest
{
    /// <summary>Lower-case, dot-separated id, e.g. "magpienest.shell".</summary>
    public required string Id { get; init; }

    /// <summary>Plugin version, e.g. "0.1.0".</summary>
    public required string Version { get; init; }

    /// <summary>File name of the plugin's main assembly.</summary>
    public required string Assembly { get; init; }

    /// <summary>Full name of the entry type; null = the single public subclass of <see cref="Plugin"/>.</summary>
    public string? EntryType { get; init; }

    /// <summary>Plugin API version the plugin was built for.</summary>
    public int ApiVersion { get; init; } = 1;

    /// <summary>Display name per language code.</summary>
    public IReadOnlyDictionary<string, string> Name { get; init; } = new Dictionary<string, string>();

    /// <summary>Description per language code.</summary>
    public IReadOnlyDictionary<string, string> Description { get; init; } = new Dictionary<string, string>();

    /// <summary>Author.</summary>
    public string? Author { get; init; }

    /// <summary>Essential plugins cannot be disabled.</summary>
    public bool Essential { get; init; }

    /// <summary>Whether the plugin is enabled when the user has made no choice.</summary>
    public bool EnabledByDefault { get; init; } = true;

    /// <summary>
    /// Id of the bundle the plugin belongs to (same rules as a plugin id); null = not in a bundle.
    /// Settings shows, switches and removes the plugins of one bundle together.
    /// </summary>
    public string? Bundle { get; init; }
}

/// <summary>Lifecycle state of a plugin.</summary>
public enum PluginState
{
    /// <summary>Switched off by the user (or by default).</summary>
    Disabled,

    /// <summary>Enabled, but a required service is missing.</summary>
    Waiting,

    /// <summary>Running.</summary>
    Active,

    /// <summary>Loading or activation failed; sticky until disabled and re-enabled.</summary>
    Failed,

    /// <summary>Will be removed on the next start (Phase 5).</summary>
    PendingRemoval,
}

/// <summary>Snapshot of a plugin's state.</summary>
/// <param name="Manifest">The manifest.</param>
/// <param name="State">Current state.</param>
/// <param name="IsEnabled">The effective enabled choice.</param>
/// <param name="IsBuiltIn">True for plugins shipped next to the exe.</param>
/// <param name="Error">One-line error, set when <see cref="PluginState.Failed"/>.</param>
/// <param name="MissingServices">Short interface names, set when <see cref="PluginState.Waiting"/>.</param>
public sealed record PluginInfo(
    PluginManifest Manifest,
    PluginState State,
    bool IsEnabled,
    bool IsBuiltIn,
    string? Error,
    IReadOnlyList<string> MissingServices);
