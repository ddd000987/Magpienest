using System.Diagnostics.CodeAnalysis;
using Magpienest.Sdk;

namespace Magpienest.Plugins.Imaging.Tests;

internal sealed class NullLog : ILog
{
    public void Debug(string message) { }

    public void Info(string message) { }

    public void Warn(string message, Exception? ex = null) { }

    public void Error(string message, Exception? ex = null) { }
}

/// <summary>Minimal context: only <see cref="Track(IDisposable)"/> is implemented; disposing it undoes the effects (LIFO).</summary>
internal sealed class FakePluginContext : IPluginContext, IDisposable
{
    private readonly Stack<Action> _undo = new();

    public PluginManifest Manifest => throw new NotSupportedException();

    public string PluginDirectory => throw new NotSupportedException();

    public string DataDirectory => throw new NotSupportedException();

    public IAppEnvironment Environment => throw new NotSupportedException();

    public ILog Log => throw new NotSupportedException();

    public IPluginConfig Config => throw new NotSupportedException();

    public IUiDispatcher Ui => throw new NotSupportedException();

    public CancellationToken Deactivating => CancellationToken.None;

    public T Get<T>() where T : class => throw new NotSupportedException();

    public bool TryGet<T>([NotNullWhen(true)] out T? service) where T : class => throw new NotSupportedException();

    public void Provide<T>(T implementation) where T : class => throw new NotSupportedException();

    public void On<TEvent>(Action<TEvent> handler) => throw new NotSupportedException();

    public void Emit<TEvent>(TEvent evt) => throw new NotSupportedException();

    public void Track(IDisposable effect) => _undo.Push(effect.Dispose);

    public void Track(Action undo) => _undo.Push(undo);

    public void Dispose()
    {
        while (_undo.Count > 0)
        {
            _undo.Pop()();
        }
    }
}
