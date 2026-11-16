using System.Text.Json.Nodes;
using Magpienest.Sdk;

namespace Magpienest.Core;

/// <summary>config/kernel.json: explicit enable/disable choices and the install / removal queues (architecture §9).</summary>
internal sealed class KernelStateStore
{
    private readonly string _path;
    private readonly ILog _log;
    private readonly Dictionary<string, bool> _enabled = new(StringComparer.Ordinal);
    private readonly List<string> _pendingInstall = [];
    private readonly List<string> _pendingRemoval = [];

    public KernelStateStore(string path, ILog log)
    {
        _path = path;
        _log = log;
    }

    /// <summary>Ids whose staged package replaces the installed folder at the next start.</summary>
    public IReadOnlyList<string> PendingInstall => _pendingInstall;

    /// <summary>Ids whose folder is deleted at the next start.</summary>
    public IReadOnlyList<string> PendingRemoval => _pendingRemoval;

    public void Load()
    {
        _enabled.Clear();
        _pendingInstall.Clear();
        _pendingRemoval.Clear();
        var root = JsonFile.ReadObject(_path, _log);
        if (root is null)
        {
            return;
        }

        if (root["enabled"] is JsonObject enabled)
        {
            foreach (var (id, value) in enabled)
            {
                if (value is JsonValue v && v.TryGetValue<bool>(out var flag))
                {
                    _enabled[id] = flag;
                }
            }
        }

        ReadList(root["pendingInstall"], _pendingInstall);
        ReadList(root["pendingRemoval"], _pendingRemoval);
    }

    public bool? GetEnabled(string id) => _enabled.TryGetValue(id, out var value) ? value : null;

    public bool IsPendingRemoval(string id) => _pendingRemoval.Contains(id, StringComparer.Ordinal);

    public void SetEnabled(string id, bool enabled)
    {
        _enabled[id] = enabled;
        Save();
    }

    public void AddPendingInstall(string id)
    {
        if (!_pendingInstall.Contains(id, StringComparer.Ordinal))
        {
            _pendingInstall.Add(id);
        }

        Save();
    }

    public void AddPendingRemoval(string id)
    {
        if (!_pendingRemoval.Contains(id, StringComparer.Ordinal))
        {
            _pendingRemoval.Add(id);
        }

        Save();
    }

    public void RemovePendingRemoval(string id)
    {
        if (_pendingRemoval.RemoveAll(p => string.Equals(p, id, StringComparison.Ordinal)) > 0)
        {
            Save();
        }
    }

    /// <summary>Replaces both queues (used after the start-up processing) and saves.</summary>
    public void SetQueues(IEnumerable<string> pendingInstall, IEnumerable<string> pendingRemoval)
    {
        var install = pendingInstall.ToList();
        var removal = pendingRemoval.ToList();
        _pendingInstall.Clear();
        _pendingInstall.AddRange(install);
        _pendingRemoval.Clear();
        _pendingRemoval.AddRange(removal);
        Save();
    }

    private void Save()
    {
        var map = new JsonObject();
        foreach (var (key, value) in _enabled.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            map[key] = value;
        }

        var root = new JsonObject { ["enabled"] = map };
        if (_pendingInstall.Count > 0)
        {
            root["pendingInstall"] = new JsonArray([.. _pendingInstall.Select(id => (JsonNode?)JsonValue.Create(id))]);
        }

        if (_pendingRemoval.Count > 0)
        {
            root["pendingRemoval"] = new JsonArray([.. _pendingRemoval.Select(id => (JsonNode?)JsonValue.Create(id))]);
        }

        try
        {
            JsonFile.WriteAtomic(_path, root.ToJsonString(JsonFile.Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error($"Could not save '{_path}'.", ex);
        }
    }

    private static void ReadList(JsonNode? node, List<string> target)
    {
        if (node is not JsonArray array)
        {
            return;
        }

        foreach (var item in array)
        {
            if (item is JsonValue v && v.TryGetValue<string>(out var id) && !string.IsNullOrWhiteSpace(id) && !target.Contains(id, StringComparer.Ordinal))
            {
                target.Add(id);
            }
        }
    }
}
