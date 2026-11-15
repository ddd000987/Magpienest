using System.Diagnostics.CodeAnalysis;
using Magpienest.Sdk;

namespace Magpienest.Core;

/// <summary>The context of one activation: effects, provided services, subscriptions.</summary>
internal sealed class PluginContext : IPluginContext
{
    private readonly PluginHost _host;
    private readonly PluginEntry _entry;
    private readonly List<Action> _effects = [];
    private readonly List<EventBus.Subscription> _subscriptions = [];
    private readonly CancellationTokenSource _deactivating = new();
    private readonly Lock _lock = new();
    private string? _dataDirectory;
    private bool _closed;

    public PluginContext(PluginHost host, PluginEntry entry)
    {
        _host = host;
        _entry = entry;
    }

    public PluginManifest Manifest => _entry.Manifest;

    public string PluginDirectory => _entry.Directory;

    public string DataDirectory
    {
        get
        {
            if (_dataDirectory is null)
            {
                var path = Path.Combine(_host.Environment.DataDirectory, "plugin-data", _entry.Id);
                Directory.CreateDirectory(path);
                _dataDirectory = path;
            }

            return _dataDirectory;
        }
    }

    public IAppEnvironment Environment => _host.Environment;

    public ILog Log => _entry.Log;

    public IPluginConfig Config => _entry.Config;

    public IUiDispatcher Ui => _host.Ui;

    public CancellationToken Deactivating => _deactivating.Token;

    public T Get<T>() where T : class
    {
        var type = typeof(T);
        if (!PluginHost.IsKernelService(type) && !_entry.Requires.Contains(type))
        {
            throw new InvalidOperationException(
                $"Plugin '{_entry.Id}' must declare [Requires(typeof({type.Name}))] to use {type.Name}.");
        }

        if (_host.Registry.TryGet(type, out var implementation))
        {
            return (T)implementation;
        }

        throw new InvalidOperationException($"Service {type.Name} is not available to plugin '{_entry.Id}'.");
    }

    public bool TryGet<T>([NotNullWhen(true)] out T? service) where T : class
    {
        var type = typeof(T);
        if (!PluginHost.IsKernelService(type) && !_entry.Requires.Contains(type) && !_entry.Optional.Contains(type))
        {
            throw new InvalidOperationException(
                $"Plugin '{_entry.Id}' must declare [Requires(typeof({type.Name}))] or [Optional(typeof({type.Name}))] to use {type.Name}.");
        }

        if (_host.Registry.TryGet(type, out var implementation))
        {
            service = (T)implementation;
            return true;
        }

        service = null;
        return false;
    }

    public void Provide<T>(T implementation) where T : class
    {
        ArgumentNullException.ThrowIfNull(implementation);
        _host.AssertUiThread();
        ThrowIfClosed();
        _host.Registry.Add(typeof(T), implementation, _entry);
        _entry.Provided.Add(typeof(T));
        _host.Reconcile();
    }

    public void On<TEvent>(Action<TEvent> handler)
    {
        ThrowIfClosed();
        var subscription = _host.Bus.Subscribe(handler, _entry);
        lock (_lock)
        {
            _subscriptions.Add(subscription);
        }
    }

    public void Emit<TEvent>(TEvent evt) => _host.Bus.Emit(evt);

    public void Track(IDisposable effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        Track(effect.Dispose);
    }

    public void Track(Action undo)
    {
        ArgumentNullException.ThrowIfNull(undo);
        lock (_lock)
        {
            if (!_closed)
            {
                _effects.Add(undo);
                return;
            }
        }

        // Registered after deactivation (late background work): undo at once.
        _entry.Log.Warn("Track called after deactivation; running the undo action immediately.");
        RunSafely(undo);
    }

    /// <summary>Cancels <see cref="Deactivating"/>.</summary>
    public void CancelDeactivating()
    {
        try
        {
            _deactivating.Cancel();
        }
        catch (AggregateException ex)
        {
            _entry.Log.Error("A Deactivating callback threw.", ex);
        }
    }

    /// <summary>Closes the context and runs the effects in LIFO order.</summary>
    public void RunEffects()
    {
        Action[] effects;
        lock (_lock)
        {
            _closed = true;
            effects = [.. _effects];
            _effects.Clear();
        }

        for (var i = effects.Length - 1; i >= 0; i--)
        {
            RunSafely(effects[i]);
        }
    }

    public void UnsubscribeAll()
    {
        EventBus.Subscription[] subscriptions;
        lock (_lock)
        {
            subscriptions = [.. _subscriptions];
            _subscriptions.Clear();
        }

        foreach (var subscription in subscriptions)
        {
            _host.Bus.Unsubscribe(subscription);
        }
    }

    private void RunSafely(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _entry.Log.Error("An undo action threw.", ex);
        }
    }

    private void ThrowIfClosed()
    {
        lock (_lock)
        {
            if (_closed)
            {
                throw new InvalidOperationException($"Plugin '{_entry.Id}' is no longer active.");
            }
        }
    }
}
