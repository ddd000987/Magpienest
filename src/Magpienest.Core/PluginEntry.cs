using Magpienest.Sdk;

namespace Magpienest.Core;

/// <summary>Kernel-side record of one plugin.</summary>
internal sealed class PluginEntry
{
    public PluginEntry(PluginCandidate candidate, bool enabled, ILog log, PluginConfig config)
    {
        Candidate = candidate;
        Enabled = enabled;
        Log = log;
        Config = config;
    }

    public PluginCandidate Candidate { get; }

    public PluginManifest Manifest => Candidate.Manifest;

    public string Id => Candidate.Manifest.Id;

    public string Directory => Candidate.Directory;

    public bool IsBuiltIn => Candidate.IsBuiltIn;

    public bool Enabled { get; set; }

    public PluginState State { get; set; }

    public string? Error { get; set; }

    public ILog Log { get; }

    public PluginConfig Config { get; }

    /// <summary>Null until the entry type was loaded successfully.</summary>
    public Type? EntryType { get; set; }

    public bool IsLoaded => EntryType is not null;

    public IReadOnlyList<Type> Requires { get; set; } = [];

    public IReadOnlyList<Type> Optional { get; set; } = [];

    public Plugin? Instance { get; set; }

    public PluginContext? Context { get; set; }

    public bool[] OptionalSnapshot { get; set; } = [];

    public HashSet<Type> Provided { get; } = [];

    public int ActivationOrder { get; set; }
}
