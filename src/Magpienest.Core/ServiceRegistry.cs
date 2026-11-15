using System.Diagnostics.CodeAnalysis;

namespace Magpienest.Core;

/// <summary>Services by interface type. Kernel services have no owner.</summary>
internal sealed class ServiceRegistry
{
    private readonly Dictionary<Type, (object Impl, PluginEntry? Owner)> _services = [];
    private readonly Lock _lock = new();

    public bool Has(Type type)
    {
        lock (_lock)
        {
            return _services.ContainsKey(type);
        }
    }

    public bool TryGet(Type type, [NotNullWhen(true)] out object? implementation)
    {
        lock (_lock)
        {
            if (_services.TryGetValue(type, out var entry))
            {
                implementation = entry.Impl;
                return true;
            }

            implementation = null;
            return false;
        }
    }

    public void Add(Type type, object implementation, PluginEntry? owner)
    {
        lock (_lock)
        {
            if (_services.TryGetValue(type, out var existing))
            {
                var by = existing.Owner is null ? "the kernel" : $"plugin '{existing.Owner.Id}'";
                throw new InvalidOperationException($"Service {type.Name} is already provided by {by}.");
            }

            _services[type] = (implementation, owner);
        }
    }

    /// <summary>Removes the service only when <paramref name="owner"/> provides it.</summary>
    public void Remove(Type type, PluginEntry owner)
    {
        lock (_lock)
        {
            if (_services.TryGetValue(type, out var existing) && existing.Owner == owner)
            {
                _services.Remove(type);
            }
        }
    }
}
