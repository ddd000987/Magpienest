using Magpienest.Sdk;

namespace Magpienest.Core;

/// <summary>Everything the kernel needs from its host.</summary>
public sealed class KernelOptions
{
    /// <summary>Folder of the exe; built-in plugins are under &lt;AppDirectory&gt;/plugins.</summary>
    public required string AppDirectory { get; init; }

    /// <summary>Root of all user data.</summary>
    public required string DataDirectory { get; init; }

    /// <summary>Command-line arguments passed through to plugins.</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>The UI thread dispatcher.</summary>
    public required IUiDispatcher Dispatcher { get; init; }

    /// <summary>Application version.</summary>
    public Version AppVersion { get; init; } = new(0, 1, 0);

    /// <summary>Called by <see cref="IAppEnvironment.RequestShutdown"/>.</summary>
    public Action<int> RequestShutdown { get; init; } = _ => { };

    /// <summary>Plugin sources; null = the built-in and user plugin folders. Used by tests.</summary>
    internal IReadOnlyList<IPluginSource>? Sources { get; init; }
}
