using Magpienest.Sdk;

namespace Magpienest.Core;

/// <summary>Kernel service <see cref="IAppEnvironment"/>.</summary>
internal sealed class AppEnvironment : IAppEnvironment
{
    private readonly Action<int> _requestShutdown;

    public AppEnvironment(KernelOptions options)
    {
        AppVersion = options.AppVersion;
        AppDirectory = options.AppDirectory;
        DataDirectory = options.DataDirectory;
        CacheDirectory = Path.Combine(options.DataDirectory, "cache");
        CommandLineArgs = [.. options.Args];
        _requestShutdown = options.RequestShutdown;
        Directory.CreateDirectory(CacheDirectory);
    }

    public Version AppVersion { get; }

    public string AppDirectory { get; }

    public string DataDirectory { get; }

    public string CacheDirectory { get; }

    public IReadOnlyList<string> CommandLineArgs { get; }

    public bool IsStarted { get; set; }

    public void RequestShutdown(int exitCode = 0) => _requestShutdown(exitCode);
}
