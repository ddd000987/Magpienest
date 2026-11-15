using System.Text.Json.Nodes;
using Magpienest.Sdk;

namespace Magpienest.Core;

/// <summary>config/kernel.json: explicit enable/disable choices (architecture §9).</summary>
internal sealed class KernelStateStore
{
    private readonly string _path;
    private readonly ILog _log;
    private readonly Dictionary<string, bool> _enabled = new(StringComparer.Ordinal);

    public KernelStateStore(string path, ILog log)
    {
        _path = path;
        _log = log;
    }

    public void Load()
    {
        _enabled.Clear();
        if (JsonFile.ReadObject(_path, _log)?["enabled"] is not JsonObject enabled)
        {
            return;
        }

        foreach (var (id, value) in enabled)
        {
            if (value is JsonValue v && v.TryGetValue<bool>(out var flag))
            {
                _enabled[id] = flag;
            }
        }
    }

    public bool? GetEnabled(string id) => _enabled.TryGetValue(id, out var value) ? value : null;

    public void SetEnabled(string id, bool enabled)
    {
        _enabled[id] = enabled;
        var map = new JsonObject();
        foreach (var (key, value) in _enabled.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            map[key] = value;
        }

        try
        {
            JsonFile.WriteAtomic(_path, new JsonObject { ["enabled"] = map }.ToJsonString(JsonFile.Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error($"Could not save '{_path}'.", ex);
        }
    }
}
