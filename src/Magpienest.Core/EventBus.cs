using Magpienest.Sdk;

namespace Magpienest.Core;

/// <summary>Typed events, always dispatched on the UI thread.</summary>
internal sealed class EventBus
{
    private readonly Dictionary<Type, List<Subscription>> _handlers = [];
    private readonly Lock _lock = new();
    private readonly IUiDispatcher _ui;
    private readonly ILog _log;

    public EventBus(IUiDispatcher ui, ILog log)
    {
        _ui = ui;
        _log = log;
    }

    public Subscription Subscribe<TEvent>(Action<TEvent> handler, PluginEntry? owner)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var subscription = new Subscription(typeof(TEvent), evt => handler((TEvent)evt), owner);
        lock (_lock)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var list))
            {
                list = [];
                _handlers[typeof(TEvent)] = list;
            }

            list.Add(subscription);
        }

        return subscription;
    }

    public void Unsubscribe(Subscription subscription)
    {
        lock (_lock)
        {
            subscription.Removed = true;
            if (_handlers.TryGetValue(subscription.EventType, out var list))
            {
                list.Remove(subscription);
            }
        }
    }

    public void Emit<TEvent>(TEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (_ui.CheckAccess())
        {
            Dispatch(typeof(TEvent), evt);
        }
        else
        {
            _ui.Post(() => Dispatch(typeof(TEvent), evt));
        }
    }

    private void Dispatch(Type type, object evt)
    {
        Subscription[] snapshot;
        lock (_lock)
        {
            snapshot = _handlers.TryGetValue(type, out var list) ? [.. list] : [];
        }

        foreach (var subscription in snapshot)
        {
            if (subscription.Removed)
            {
                continue;
            }

            try
            {
                subscription.Invoke(evt);
            }
            catch (Exception ex)
            {
                (subscription.Owner?.Log ?? _log).Error($"Handler for {type.Name} threw.", ex);
            }
        }
    }

    internal sealed class Subscription(Type eventType, Action<object> invoke, PluginEntry? owner)
    {
        public Type EventType { get; } = eventType;

        public Action<object> Invoke { get; } = invoke;

        public PluginEntry? Owner { get; } = owner;

        public bool Removed { get; set; }
    }
}
