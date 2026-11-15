using System.Diagnostics.CodeAnalysis;

namespace Magpienest.Sdk;

/// <summary>
/// Handed to <see cref="Plugin.Activate"/>. Every side effect of a plugin goes through it so the kernel can undo it.
/// </summary>
public interface IPluginContext
{
    /// <summary>The plugin's manifest.</summary>
    PluginManifest Manifest { get; }

    /// <summary>Folder that contains plugin.json; treat as read-only.</summary>
    string PluginDirectory { get; }

    /// <summary>&lt;data&gt;/plugin-data/&lt;plugin id&gt;/ ; created on first access.</summary>
    string DataDirectory { get; }

    /// <summary>Application paths, arguments and shutdown.</summary>
    IAppEnvironment Environment { get; }

    /// <summary>Log tagged with this plugin's id.</summary>
    ILog Log { get; }

    /// <summary>Persistent key/value settings of this plugin.</summary>
    IPluginConfig Config { get; }

    /// <summary>The UI thread dispatcher.</summary>
    IUiDispatcher Ui { get; }

    /// <summary>Cancelled when deactivation of this plugin starts. Pass it to all background work.</summary>
    CancellationToken Deactivating { get; }

    /// <summary>
    /// Returns a service. Throws <see cref="InvalidOperationException"/> if <typeparamref name="T"/> is not a
    /// kernel service and is not listed in this plugin's <see cref="RequiresAttribute"/>.
    /// </summary>
    /// <typeparam name="T">Service interface type.</typeparam>
    T Get<T>() where T : class;

    /// <summary>
    /// For services listed in <see cref="OptionalAttribute"/> (or <see cref="RequiresAttribute"/>).
    /// Throws <see cref="InvalidOperationException"/> if <typeparamref name="T"/> is declared in neither.
    /// </summary>
    /// <typeparam name="T">Service interface type.</typeparam>
    /// <param name="service">The service when available.</param>
    bool TryGet<T>([NotNullWhen(true)] out T? service) where T : class;

    /// <summary>
    /// Publishes a service; removed automatically on deactivation.
    /// Throws <see cref="InvalidOperationException"/> if another plugin already provides <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">Service interface type.</typeparam>
    /// <param name="implementation">The implementation.</param>
    void Provide<T>(T implementation) where T : class;

    /// <summary>Subscribes to an event; unsubscribed automatically. The handler runs on the UI thread.</summary>
    /// <typeparam name="TEvent">Event record type.</typeparam>
    /// <param name="handler">The handler.</param>
    void On<TEvent>(Action<TEvent> handler);

    /// <summary>Publishes an event. Callable from any thread.</summary>
    /// <typeparam name="TEvent">Event record type.</typeparam>
    /// <param name="evt">The event.</param>
    void Emit<TEvent>(TEvent evt);

    /// <summary>Records an undo action, run (LIFO) on deactivation.</summary>
    /// <param name="effect">Disposed on deactivation.</param>
    void Track(IDisposable effect);

    /// <summary>Records an undo action, run (LIFO) on deactivation.</summary>
    /// <param name="undo">Invoked on deactivation.</param>
    void Track(Action undo);
}
