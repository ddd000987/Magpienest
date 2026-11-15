using Magpienest.Sdk;

namespace Magpienest.Core;

/// <summary>Supplies plugin candidates to the kernel.</summary>
internal interface IPluginSource
{
    /// <summary>Returns the candidates; must not throw for a single bad plugin.</summary>
    IEnumerable<PluginCandidate> Discover(ILog log);
}

/// <summary>A discovered plugin, not yet loaded.</summary>
internal sealed record PluginCandidate(
    PluginManifest Manifest,
    string Directory,
    bool IsBuiltIn,
    Func<Type> LoadEntryType);
