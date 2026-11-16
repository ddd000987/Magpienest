using Magpienest.Sdk;

namespace Magpienest.Plugins.Settings;

/// <summary>What a bundle card shows as its state.</summary>
internal enum BundleState
{
    /// <summary>Every member that is not being removed is running.</summary>
    On,

    /// <summary>Some members are running, others are switched off.</summary>
    PartlyOn,

    /// <summary>Every member is switched off.</summary>
    Off,

    /// <summary>A member waits for a service no active plugin provides.</summary>
    Waiting,

    /// <summary>A member failed to load or to start.</summary>
    Failed,

    /// <summary>Every member will be deleted at the next start.</summary>
    PendingRemoval,
}

/// <summary>
/// One card in Settings → Plugins: the plugins sharing a <see cref="PluginManifest.Bundle"/>, or a single plugin
/// without one. Decides the card's state and which buttons it offers; the view model only shows it.
/// </summary>
/// <param name="Key">Unique key of the card.</param>
/// <param name="BundleId">The bundle id; null for a plugin without a bundle.</param>
/// <param name="Members">The plugins, by id.</param>
internal sealed record PluginBundle(string Key, string? BundleId, IReadOnlyList<PluginInfo> Members)
{
    /// <summary>Every member is essential: no switch and no remove button.</summary>
    public bool IsEssential => Members.All(m => m.Manifest.Essential);

    /// <summary>Every member ships next to the exe: the "Built-in" label.</summary>
    public bool IsBuiltIn => Members.All(m => m.IsBuiltIn);

    /// <summary>A member is queued for removal: "Undo" is offered and the switch is locked.</summary>
    public bool CanUndo => Members.Any(m => m.State == PluginState.PendingRemoval);

    /// <summary>A user-installed member can still be removed.</summary>
    public bool CanRemove => !IsEssential && RemovableIds.Any();

    /// <summary>The switch can be used.</summary>
    public bool CanToggle => !IsEssential && !CanUndo;

    /// <summary>The switch shows on when every member that can be switched is enabled.</summary>
    public bool IsEnabled => IsEssential || Members.Where(m => !m.Manifest.Essential).All(m => m.IsEnabled);

    /// <summary>Members the switch turns on or off.</summary>
    public IEnumerable<string> ToggleIds =>
        Members.Where(m => !m.Manifest.Essential && m.State != PluginState.PendingRemoval).Select(m => m.Manifest.Id);

    /// <summary>Members "Remove" uninstalls.</summary>
    public IEnumerable<string> RemovableIds =>
        Members.Where(m => !m.IsBuiltIn && m.State != PluginState.PendingRemoval).Select(m => m.Manifest.Id);

    /// <summary>Members "Undo" brings back.</summary>
    public IEnumerable<string> PendingIds =>
        Members.Where(m => m.State == PluginState.PendingRemoval).Select(m => m.Manifest.Id);

    /// <summary>The member that names a bundle the language files do not know: the one whose id is the bundle id, else the first.</summary>
    public PluginInfo Lead => Members.FirstOrDefault(m => m.Manifest.Id == BundleId) ?? Members[0];

    /// <summary>The highest member version.</summary>
    public string Version => Members.Select(m => m.Manifest.Version).OrderBy(v => v, VersionComparer.Instance).Last();

    /// <summary>The state the card shows.</summary>
    public BundleState State
    {
        get
        {
            if (Members.Any(m => m.State == PluginState.Failed))
            {
                return BundleState.Failed;
            }

            if (Members.Any(m => m.State == PluginState.Waiting))
            {
                return BundleState.Waiting;
            }

            var remaining = Members.Where(m => m.State != PluginState.PendingRemoval).ToList();
            if (remaining.Count == 0)
            {
                return BundleState.PendingRemoval;
            }

            var active = remaining.Count(m => m.State == PluginState.Active);
            return active == remaining.Count ? BundleState.On : active == 0 ? BundleState.Off : BundleState.PartlyOn;
        }
    }
}

/// <summary>Groups plugins into bundle cards.</summary>
internal static class PluginBundles
{
    /// <summary>
    /// One card per bundle id, and one per plugin without a bundle. Order: essential bundles, then built-in ones,
    /// then installed ones, each by <paramref name="nameOf"/>.
    /// </summary>
    /// <param name="plugins">Every discovered plugin.</param>
    /// <param name="nameOf">The name shown on a card.</param>
    public static IReadOnlyList<PluginBundle> Group(IEnumerable<PluginInfo> plugins, Func<PluginBundle, string> nameOf)
    {
        var bundles = plugins
            .GroupBy(p => p.Manifest.Bundle is { } bundle ? "bundle:" + bundle : "plugin:" + p.Manifest.Id, StringComparer.Ordinal)
            .Select(g => new PluginBundle(
                g.Key,
                g.First().Manifest.Bundle,
                g.OrderBy(p => p.Manifest.Id, StringComparer.Ordinal).ToList()))
            .ToList();

        return bundles
            .OrderBy(b => b.IsEssential ? 0 : b.IsBuiltIn ? 1 : 2)
            .ThenBy(nameOf, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(b => b.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Language key of a bundle's name.</summary>
    public static string NameKey(string bundleId) => $"Settings.Bundle.{bundleId}.Name";

    /// <summary>Language key of a bundle's description.</summary>
    public static string DescriptionKey(string bundleId) => $"Settings.Bundle.{bundleId}.Description";
}

/// <summary>Compares "major.minor.patch[-suffix]" versions numerically; text that does not parse sorts first, by ordinal.</summary>
internal sealed class VersionComparer : IComparer<string>
{
    public static VersionComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        var a = Parse(x);
        var b = Parse(y);
        if (a is not null && b is not null)
        {
            var result = a.CompareTo(b);
            return result != 0 ? result : string.CompareOrdinal(x, y);
        }

        if (a is null != (b is null))
        {
            return a is null ? -1 : 1;
        }

        return string.CompareOrdinal(x, y);
    }

    private static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var core = text.Split('-', '+')[0];
        return System.Version.TryParse(core.Contains('.') ? core : core + ".0", out var version) ? version : null;
    }
}
