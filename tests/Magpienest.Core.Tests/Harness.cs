using System.Collections.Concurrent;
using Magpienest.Sdk;

namespace Magpienest.Core.Tests;

/// <summary>Immediate dispatcher bound to the creating thread; off-thread posts wait for <see cref="RunPending"/>.</summary>
internal sealed class TestDispatcher : IUiDispatcher
{
    private readonly Thread _thread = Thread.CurrentThread;
    private readonly ConcurrentQueue<Action> _queue = new();

    public bool CheckAccess() => Thread.CurrentThread == _thread;

    public void Post(Action action) => _queue.Enqueue(action);

    public Task InvokeAsync(Action action)
    {
        if (CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource();
        _queue.Enqueue(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        if (CheckAccess())
        {
            return Task.FromResult(func());
        }

        var tcs = new TaskCompletionSource<T>();
        _queue.Enqueue(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    public int RunPending()
    {
        var count = 0;
        while (_queue.TryDequeue(out var action))
        {
            action();
            count++;
        }

        return count;
    }
}

internal sealed class ListSource(IReadOnlyList<PluginCandidate> candidates) : IPluginSource
{
    public IEnumerable<PluginCandidate> Discover(ILog log) => candidates;
}

/// <summary>
/// Per-test world: data dir, dispatcher, plugin candidates, a recorded event log and per-type behaviours.
/// Must be created inside the test method (it sets an AsyncLocal that test plugins read).
/// </summary>
internal sealed class Harness : IDisposable
{
    private static readonly AsyncLocal<Harness?> CurrentHarness = new();

    private readonly List<PluginCandidate> _candidates = [];
    private readonly Dictionary<Type, Action<IPluginContext>> _onActivate = [];
    private readonly Dictionary<Type, Action<IPluginContext>> _onDeactivate = [];
    private readonly Dictionary<Type, string> _ids = [];

    public Harness(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(Path.GetTempPath(), "magpienest-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataDirectory);
        CurrentHarness.Value = this;
    }

    public static Harness Current => CurrentHarness.Value ?? throw new InvalidOperationException("No harness on this flow.");

    public string DataDirectory { get; }

    public TestDispatcher Dispatcher { get; } = new();

    public List<string> Log { get; } = [];

    public List<Plugin> Instances { get; } = [];

    public Dictionary<string, int> Activations { get; } = [];

    public Kernel? Kernel { get; private set; }

    public List<PluginCandidate> Candidates => _candidates;

    public Harness Add<T>(string id, bool essential = false, bool enabledByDefault = true, int apiVersion = 1, bool builtIn = true)
        where T : TestPlugin, new()
    {
        _ids[typeof(T)] = id;
        _candidates.Add(new PluginCandidate(
            new PluginManifest
            {
                Id = id,
                Version = "1.0.0",
                Assembly = "Test.dll",
                Essential = essential,
                EnabledByDefault = enabledByDefault,
                ApiVersion = apiVersion,
            },
            Path.Combine(DataDirectory, "fake-plugins", id),
            builtIn,
            () => typeof(T)));
        return this;
    }

    public Harness OnActivate<T>(Action<IPluginContext> action) where T : TestPlugin
    {
        _onActivate[typeof(T)] = action;
        return this;
    }

    public Harness OnDeactivate<T>(Action<IPluginContext> action) where T : TestPlugin
    {
        _onDeactivate[typeof(T)] = action;
        return this;
    }

    public Kernel CreateKernel() => new(new KernelOptions
    {
        AppDirectory = DataDirectory,
        DataDirectory = DataDirectory,
        Dispatcher = Dispatcher,
        Sources = [new ListSource(_candidates)],
    });

    public Kernel Start()
    {
        Kernel = CreateKernel();
        Kernel.Start();
        return Kernel;
    }

    public PluginInfo Info(string id) => Kernel!.Plugins.Plugins.Single(p => p.Manifest.Id == id);

    public PluginState State(string id) => Info(id).State;

    public string IdOf(Type type) => _ids.TryGetValue(type, out var id) ? id : type.Name;

    public void Activated(TestPlugin plugin, IPluginContext ctx)
    {
        var id = ctx.Manifest.Id;
        Log.Add("activate:" + id);
        Activations[id] = Activations.GetValueOrDefault(id) + 1;
        Instances.Add(plugin);
        if (_onActivate.TryGetValue(plugin.GetType(), out var action))
        {
            action(ctx);
        }
    }

    public void Deactivated(TestPlugin plugin, IPluginContext ctx)
    {
        Log.Add("deactivate:" + ctx.Manifest.Id);
        if (_onDeactivate.TryGetValue(plugin.GetType(), out var action))
        {
            action(ctx);
        }
    }

    public void Dispose()
    {
        CurrentHarness.Value = null;
        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public abstract class TestPlugin : Plugin
{
    private IPluginContext? _ctx;

    public override void Activate(IPluginContext ctx)
    {
        _ctx = ctx;
        Harness.Current.Activated(this, ctx);
    }

    public override void Deactivate() => Harness.Current.Deactivated(this, _ctx!);
}

public sealed class P1 : TestPlugin;

public sealed class P2 : TestPlugin;

public sealed class P3 : TestPlugin;

[Requires(typeof(IServiceA))]
public sealed class NeedsA : TestPlugin;

[Requires(typeof(IServiceB))]
public sealed class NeedsB : TestPlugin;

[Optional(typeof(IServiceA))]
public sealed class OptionalA : TestPlugin;

[Requires(typeof(IRegistry))]
public sealed class UsesRegistry : TestPlugin;

public interface IServiceA;

public interface IServiceB;

public sealed class ServiceA : IServiceA;

public sealed class ServiceB : IServiceB;

public interface IRegistry
{
    IReadOnlyList<string> Items { get; }

    void Add(IPluginContext owner, string item);
}

public sealed class Registry : IRegistry
{
    private readonly List<string> _items = [];

    public IReadOnlyList<string> Items => _items;

    public void Add(IPluginContext owner, string item)
    {
        _items.Add(item);
        owner.Track(() => _items.Remove(item));
    }
}

public sealed record TestEvent(string Value);
