using Magpienest.Sdk;

namespace Magpienest.Plugins.Settings;

/// <summary>Which plugins have a dot on which section. A dot shows while at least one plugin has it on.</summary>
internal sealed class BadgeSet
{
    private readonly Dictionary<string, HashSet<IPluginContext>> _owners = new(StringComparer.Ordinal);
    private readonly HashSet<(IPluginContext Owner, string Id)> _tracked = [];

    /// <summary>Raised with the id whose dot appeared or disappeared.</summary>
    public event Action<string>? Changed;

    public bool Has(string id) => _owners.TryGetValue(id, out var owners) && owners.Count > 0;

    public bool Any => _owners.Values.Any(o => o.Count > 0);

    public void Set(IPluginContext owner, string id, bool visible)
    {
        var before = Has(id);
        if (visible)
        {
            if (!_owners.TryGetValue(id, out var owners))
            {
                _owners[id] = owners = [];
            }

            owners.Add(owner);
            if (_tracked.Add((owner, id)))
            {
                owner.Track(() =>
                {
                    _tracked.Remove((owner, id));
                    Set(owner, id, false);
                });
            }
        }
        else if (_owners.TryGetValue(id, out var owners))
        {
            owners.Remove(owner);
        }

        if (Has(id) != before)
        {
            Changed?.Invoke(id);
        }
    }
}
