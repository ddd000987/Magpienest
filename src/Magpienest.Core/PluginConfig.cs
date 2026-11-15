using System.Text.Json;
using System.Text.Json.Nodes;
using Magpienest.Sdk;

namespace Magpienest.Core;

/// <summary>Creates one <see cref="PluginConfig"/> per plugin id.</summary>
internal sealed class PluginConfigStore
{
    private readonly string _directory;
    private readonly FileLog _fileLog;
    private readonly Dictionary<string, PluginConfig> _configs = new(StringComparer.Ordinal);

    public PluginConfigStore(string directory, FileLog fileLog)
    {
        _directory = directory;
        _fileLog = fileLog;
    }

    public PluginConfig Get(string pluginId)
    {
        if (!_configs.TryGetValue(pluginId, out var config))
        {
            config = new PluginConfig(Path.Combine(_directory, pluginId + ".json"), new PluginLog(_fileLog, pluginId));
            _configs[pluginId] = config;
        }

        return config;
    }

    public void FlushAll()
    {
        foreach (var config in _configs.Values)
        {
            config.Flush();
            config.StopTimer();
        }
    }
}

/// <summary>config/&lt;id&gt;.json; debounced save (500 ms).</summary>
internal sealed class PluginConfig : IPluginConfig
{
    private const int DebounceMilliseconds = 500;

    private readonly string _path;
    private readonly ILog _log;
    private readonly Lock _lock = new();
    private JsonObject? _data;
    private Timer? _timer;
    private bool _dirty;

    public PluginConfig(string path, ILog log)
    {
        _path = path;
        _log = log;
    }

    public T Get<T>(string key, T defaultValue)
    {
        lock (_lock)
        {
            var data = Data();
            if (!data.TryGetPropertyValue(key, out var node) || node is null)
            {
                return defaultValue;
            }

            try
            {
                var value = node.Deserialize<T>(JsonFile.Options);
                return value is null ? defaultValue : value;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or FormatException)
            {
                _log.Warn($"Config value '{key}' could not be read as {typeof(T).Name}; using the default.", ex);
                return defaultValue;
            }
        }
    }

    public void Set<T>(string key, T value)
    {
        lock (_lock)
        {
            Data()[key] = JsonSerializer.SerializeToNode(value, JsonFile.Options);
            _dirty = true;
            _timer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(DebounceMilliseconds, Timeout.Infinite);
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            if (!_dirty || _data is null)
            {
                return;
            }

            _dirty = false;
            try
            {
                JsonFile.WriteAtomic(_path, _data.ToJsonString(JsonFile.Options));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Error($"Could not save '{_path}'.", ex);
            }
        }
    }

    public void StopTimer()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private JsonObject Data() => _data ??= JsonFile.ReadObject(_path, _log) ?? [];
}
