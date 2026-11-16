using Magpienest.Sdk;

namespace Magpienest.Core;

/// <summary>Façade of the plugin kernel. All members must be called on the UI thread.</summary>
public sealed class Kernel
{
    /// <summary>The plugin API version this kernel supports.</summary>
    public const int ApiVersion = 1;

    private readonly PluginHost _host;

    /// <summary>Creates the kernel; nothing is loaded until <see cref="Start"/>.</summary>
    /// <param name="options">Host options.</param>
    public Kernel(KernelOptions options)
    {
        Directory.CreateDirectory(options.DataDirectory);
        var verbose = options.Args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);
        var fileLog = new FileLog(Path.Combine(options.DataDirectory, "logs"), verbose);
        Log = new PluginLog(fileLog, "kernel");
        var environment = new AppEnvironment(options);
        var safeMode = options.Args.Contains("--safe-mode", StringComparer.OrdinalIgnoreCase);
        var sources = options.Sources ?? DefaultSources(options, safeMode);
        _host = new PluginHost(options.Dispatcher, environment, sources, fileLog, Log, safeMode);
    }

    /// <summary>The plugin manager (also available to plugins as a kernel service).</summary>
    public IPluginManager Plugins => _host;

    /// <summary>The kernel's own log.</summary>
    public ILog Log { get; }

    /// <summary>Path of today's log file.</summary>
    public string LogFile => _host.FileLog.CurrentFile;

    /// <summary>Discovers plugins and activates everything that can be activated.</summary>
    public void Start() => _host.Start();

    /// <summary>Emits <see cref="AppShuttingDown"/> and deactivates every plugin.</summary>
    public void Shutdown() => _host.Shutdown();

    /// <summary>Publishes a host-originated event. Callable from any thread.</summary>
    /// <typeparam name="TEvent">Event record type.</typeparam>
    /// <param name="evt">The event.</param>
    public void Emit<TEvent>(TEvent evt) => _host.Bus.Emit(evt);

    private static IReadOnlyList<IPluginSource> DefaultSources(KernelOptions options, bool safeMode)
    {
        var builtIn = new DirectoryPluginSource(Path.Combine(options.AppDirectory, "plugins"), isBuiltIn: true);
        return safeMode
            ? [builtIn]
            : [builtIn, new DirectoryPluginSource(Path.Combine(options.DataDirectory, "plugins"), isBuiltIn: false)];
    }
}
