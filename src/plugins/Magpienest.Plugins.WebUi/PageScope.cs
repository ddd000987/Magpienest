using System.Diagnostics.CodeAnalysis;
using Magpienest.Sdk;

namespace Magpienest.Plugins.WebUi;

/// <summary>
/// A plugin context whose <see cref="Track(Action)"/> effects are undone when the scope is disposed, before the plugin
/// deactivates. Registrations made through it (the "Web" page) can be removed while the plugin keeps running.
/// Everything else goes to the plugin's own context. Thread-safe.
/// </summary>
internal sealed class PageScope(IPluginContext owner) : IPluginContext, IDisposable
{
    private readonly object _lock = new();
    private readonly List<Action> _undo = [];
    private bool _disposed;

    public PluginManifest Manifest => owner.Manifest;

    public string PluginDirectory => owner.PluginDirectory;

    public string DataDirectory => owner.DataDirectory;

    public IAppEnvironment Environment => owner.Environment;

    public ILog Log => owner.Log;

    public IPluginConfig Config => owner.Config;

    public IUiDispatcher Ui => owner.Ui;

    public CancellationToken Deactivating => owner.Deactivating;

    public T Get<T>() where T : class => owner.Get<T>();

    public bool TryGet<T>([NotNullWhen(true)] out T? service) where T : class => owner.TryGet(out service);

    public void Provide<T>(T implementation) where T : class => owner.Provide(implementation);

    public void On<TEvent>(Action<TEvent> handler) => owner.On(handler);

    public void Emit<TEvent>(TEvent evt) => owner.Emit(evt);

    public void Track(IDisposable effect) => Track(effect.Dispose);

    public void Track(Action undo)
    {
        lock (_lock)
        {
            if (!_disposed)
            {
                _undo.Add(undo);
                return;
            }
        }

        undo(); // Already closed: undo at once.
    }

    /// <summary>Undoes every tracked effect, newest first; one failure does not stop the others.</summary>
    public void Dispose()
    {
        Action[] undo;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            undo = [.. _undo];
            _undo.Clear();
        }

        for (var i = undo.Length - 1; i >= 0; i--)
        {
            try
            {
                undo[i]();
            }
            catch (Exception ex)
            {
                owner.Log.Error("Web interface: could not remove the Web page.", ex);
            }
        }
    }
}
