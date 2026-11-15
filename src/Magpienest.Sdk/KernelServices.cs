namespace Magpienest.Sdk;

/// <summary>Kernel service: lists plugins and switches them on and off.</summary>
public interface IPluginManager
{
    /// <summary>Snapshot of every discovered plugin.</summary>
    IReadOnlyList<PluginInfo> Plugins { get; }

    /// <summary>
    /// Persists the choice and applies it immediately.
    /// Throws <see cref="InvalidOperationException"/> for essential plugins when <paramref name="enabled"/> is false.
    /// </summary>
    /// <param name="pluginId">Plugin id.</param>
    /// <param name="enabled">The new choice.</param>
    void SetEnabled(string pluginId, bool enabled);
}

/// <summary>Kernel service: application paths, arguments and shutdown.</summary>
public interface IAppEnvironment
{
    /// <summary>Application version.</summary>
    Version AppVersion { get; }

    /// <summary>Folder of the exe.</summary>
    string AppDirectory { get; }

    /// <summary>Root of all user data.</summary>
    string DataDirectory { get; }

    /// <summary>&lt;data&gt;/cache ; safe to delete at any time.</summary>
    string CacheDirectory { get; }

    /// <summary>Command-line arguments not consumed by the host.</summary>
    IReadOnlyList<string> CommandLineArgs { get; }

    /// <summary>True after the first reconcile at startup finished.</summary>
    bool IsStarted { get; }

    /// <summary>Shuts the application down with the given exit code.</summary>
    /// <param name="exitCode">Process exit code.</param>
    void RequestShutdown(int exitCode = 0);
}

/// <summary>Kernel service: the UI thread dispatcher.</summary>
public interface IUiDispatcher
{
    /// <summary>True when called on the UI thread.</summary>
    bool CheckAccess();

    /// <summary>Queues the action; returns immediately.</summary>
    /// <param name="action">The action.</param>
    void Post(Action action);

    /// <summary>Runs the action on the UI thread.</summary>
    /// <param name="action">The action.</param>
    Task InvokeAsync(Action action);

    /// <summary>Runs the function on the UI thread.</summary>
    /// <typeparam name="T">Result type.</typeparam>
    /// <param name="func">The function.</param>
    Task<T> InvokeAsync<T>(Func<T> func);
}

/// <summary>Log of one plugin (or of the kernel).</summary>
public interface ILog
{
    /// <summary>Written only with --verbose.</summary>
    /// <param name="message">English message.</param>
    void Debug(string message);

    /// <summary>Informational message.</summary>
    /// <param name="message">English message.</param>
    void Info(string message);

    /// <summary>Warning.</summary>
    /// <param name="message">English message.</param>
    /// <param name="ex">Optional exception.</param>
    void Warn(string message, Exception? ex = null);

    /// <summary>Error.</summary>
    /// <param name="message">English message.</param>
    /// <param name="ex">Optional exception.</param>
    void Error(string message, Exception? ex = null);
}

/// <summary>Persistent key/value settings of one plugin (config/&lt;plugin id&gt;.json).</summary>
public interface IPluginConfig
{
    /// <summary>Returns the stored value, or <paramref name="defaultValue"/> when absent or unreadable.</summary>
    /// <typeparam name="T">JSON-serializable type.</typeparam>
    /// <param name="key">Key.</param>
    /// <param name="defaultValue">Default.</param>
    T Get<T>(string key, T defaultValue);

    /// <summary>Stores a value; saved to disk debounced (500 ms) and on deactivation.</summary>
    /// <typeparam name="T">JSON-serializable type.</typeparam>
    /// <param name="key">Key.</param>
    /// <param name="value">Value.</param>
    void Set<T>(string key, T value);
}

/// <summary>Emitted before a plugin's <see cref="Plugin.Activate"/> is called.</summary>
/// <param name="Manifest">The plugin's manifest.</param>
/// <param name="PluginDirectory">The plugin's folder.</param>
public sealed record PluginActivating(PluginManifest Manifest, string PluginDirectory);

/// <summary>Emitted after any plugin state change.</summary>
/// <param name="Plugin">The new state.</param>
public sealed record PluginStateChanged(PluginInfo Plugin);

/// <summary>Emitted after the first reconcile at startup.</summary>
public sealed record AppStarted;

/// <summary>Emitted before plugins are deactivated at exit.</summary>
public sealed record AppShuttingDown;
