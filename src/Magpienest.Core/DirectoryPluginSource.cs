using System.Reflection;
using System.Runtime.Loader;
using Magpienest.Sdk;

namespace Magpienest.Core;

/// <summary>Discovers &lt;root&gt;/*/plugin.json (architecture §5.2).</summary>
internal sealed class DirectoryPluginSource : IPluginSource
{
    private readonly string _root;
    private readonly bool _isBuiltIn;

    public DirectoryPluginSource(string root, bool isBuiltIn)
    {
        _root = root;
        _isBuiltIn = isBuiltIn;
    }

    public IEnumerable<PluginCandidate> Discover(ILog log)
    {
        var candidates = new List<PluginCandidate>();
        if (!Directory.Exists(_root))
        {
            return candidates;
        }

        foreach (var directory in Directory.GetDirectories(_root).Order(StringComparer.OrdinalIgnoreCase))
        {
            var manifestPath = Path.Combine(directory, "plugin.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            PluginManifest manifest;
            try
            {
                manifest = ManifestReader.Read(manifestPath);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                log.Warn($"Skipping plugin folder '{directory}': {ex.Message}");
                continue;
            }

            var loader = new EntryTypeLoader(directory, manifest);
            candidates.Add(new PluginCandidate(manifest, directory, _isBuiltIn, loader.Load));
        }

        return candidates;
    }

    /// <summary>Loads the plugin assembly at most once and resolves its entry type.</summary>
    private sealed class EntryTypeLoader
    {
        private readonly string _directory;
        private readonly PluginManifest _manifest;
        private Assembly? _assembly;
        private Type? _type;

        public EntryTypeLoader(string directory, PluginManifest manifest)
        {
            _directory = directory;
            _manifest = manifest;
        }

        public Type Load()
        {
            if (_type is not null)
            {
                return _type;
            }

            if (_assembly is null)
            {
                var path = Path.Combine(_directory, _manifest.Assembly);
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException($"Plugin assembly '{_manifest.Assembly}' was not found.", path);
                }

                _assembly = new PluginLoadContext(path).LoadFromAssemblyPath(path);
            }

            Type type;
            if (_manifest.EntryType is { Length: > 0 } name)
            {
                type = _assembly.GetType(name, throwOnError: false)
                    ?? throw new InvalidOperationException($"Entry type '{name}' was not found in '{_manifest.Assembly}'.");
            }
            else
            {
                var found = _assembly.GetExportedTypes()
                    .Where(t => t.IsClass && !t.IsAbstract && typeof(Plugin).IsAssignableFrom(t))
                    .ToArray();
                type = found.Length switch
                {
                    1 => found[0],
                    0 => throw new InvalidOperationException($"'{_manifest.Assembly}' contains no public Plugin subclass."),
                    _ => throw new InvalidOperationException($"'{_manifest.Assembly}' contains several Plugin subclasses; set \"entryType\" in plugin.json."),
                };
            }

            _type = type;
            return type;
        }
    }
}

/// <summary>
/// Non-collectible load context with host-first resolution (architecture §6, verified by spike).
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string mainAssemblyPath)
        : base(Path.GetFileNameWithoutExtension(mainAssemblyPath), isCollectible: false)
        => _resolver = new AssemblyDependencyResolver(mainAssemblyPath);

    protected override Assembly? Load(AssemblyName name)
    {
        try
        {
            return Default.LoadFromAssemblyName(name);
        }
        catch
        {
            // not a host assembly
        }

        var path = _resolver.ResolveAssemblyToPath(name);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
