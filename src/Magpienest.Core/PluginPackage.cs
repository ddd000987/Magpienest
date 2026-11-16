using System.IO.Compression;
using System.Text;
using Magpienest.Sdk;

namespace Magpienest.Core;

/// <summary>A rejected package: a stable <see cref="Code"/> plus an English message.</summary>
internal sealed class PackageException(PluginInstallError code, string message, string? pluginId = null) : Exception(message)
{
    public PluginInstallError Code { get; } = code;

    /// <summary>Set once the manifest could be read.</summary>
    public string? PluginId { get; } = pluginId;
}

/// <summary>One file or folder of a package, with a normalized '/'-separated path (no trailing slash).</summary>
internal sealed record PackageEntry(string Path, long Size, bool IsDirectory, string Source);

/// <summary>
/// A validated plugin package (a zip or a folder). <see cref="Open"/> applies every rule of phase-5 T5.1;
/// <see cref="Extract"/> writes the plugin files into an empty folder inside the data folder.
/// </summary>
internal sealed class PluginPackage : IDisposable
{
    public const int MaxEntries = 5000;
    public const long MaxTotalBytes = 500L * 1024 * 1024;
    private const long MaxManifestBytes = 1024 * 1024;
    private const string ManifestName = "plugin.json";

    private static readonly string[] ForbiddenFiles = ["Magpienest.Sdk.dll", "Magpienest.Core.dll", "Magpienest.exe", "Magpienest.dll"];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly Reader _reader;
    private readonly string _prefix;
    private readonly IReadOnlyList<PackageEntry> _entries;

    private PluginPackage(Reader reader, PluginManifest manifest, string prefix, IReadOnlyList<PackageEntry> entries)
    {
        _reader = reader;
        Manifest = manifest;
        _prefix = prefix;
        _entries = entries;
    }

    public PluginManifest Manifest { get; }

    /// <summary>
    /// Reads and validates the package. Throws <see cref="PackageException"/> for every rule violation.
    /// </summary>
    /// <param name="path">A .zip file or a folder.</param>
    /// <param name="pluginsRoot">&lt;data&gt;/plugins; only used to evaluate the zip-slip rule against a real path.</param>
    /// <param name="builtInIds">Ids that cannot be installed.</param>
    public static PluginPackage Open(string path, string pluginsRoot, ISet<string> builtInIds)
    {
        Reader reader;
        if (Directory.Exists(path))
        {
            reader = new FolderReader(path);
        }
        else if (File.Exists(path))
        {
            try
            {
                reader = new ZipReader(path);
            }
            catch (InvalidDataException ex)
            {
                throw new PackageException(PluginInstallError.InvalidPackage, $"'{Path.GetFileName(path)}' is not a valid zip file: {ex.Message}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PackageException(PluginInstallError.InvalidPackage, $"'{Path.GetFileName(path)}' could not be read: {ex.Message}");
            }
        }
        else
        {
            throw new PackageException(PluginInstallError.PackageNotFound, $"'{path}' does not exist.");
        }

        try
        {
            return Validate(reader, pluginsRoot, builtInIds);
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>Writes the plugin's files into <paramref name="destination"/> (created if needed).</summary>
    public void Extract(string destination)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        Directory.CreateDirectory(root);
        long total = 0;
        foreach (var entry in _entries)
        {
            if (!TryStrip(entry.Path, out var relative) || relative.Length == 0)
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(root, relative));
            if (!IsInside(root, target))
            {
                throw new PackageException(PluginInstallError.UnsafePath, $"Package entry '{entry.Path}' would be written outside the plugin folder.", Manifest.Id);
            }

            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = _reader.OpenRead(entry);
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan);
            var buffer = new byte[81920];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > MaxTotalBytes)
                {
                    throw TooLarge(Manifest.Id);
                }

                output.Write(buffer, 0, read);
            }
        }
    }

    public void Dispose() => _reader.Dispose();

    private static PluginPackage Validate(Reader reader, string pluginsRoot, ISet<string> builtInIds)
    {
        var raw = reader.List();
        if (raw.Count > MaxEntries)
        {
            throw new PackageException(PluginInstallError.TooManyEntries, $"The package has more than {MaxEntries} entries.");
        }

        long declared = 0;
        foreach (var entry in raw)
        {
            declared += entry.Size;
        }

        if (declared > MaxTotalBytes)
        {
            throw TooLarge(null);
        }

        // Normalize every path and apply the zip-slip rule against a real destination.
        var probeRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(pluginsRoot, ".staging", "validate")));
        var entries = new List<PackageEntry>(raw.Count);
        foreach (var entry in raw)
        {
            var normalized = Normalize(entry.Path);
            var destination = Path.GetFullPath(Path.Combine(probeRoot, normalized));
            if (!IsInside(probeRoot, destination))
            {
                throw new PackageException(PluginInstallError.UnsafePath, $"Package entry '{entry.Path}' would be written outside the plugin folder.");
            }

            entries.Add(entry with { Path = normalized });
        }

        foreach (var entry in entries.Where(e => !e.IsDirectory))
        {
            var name = entry.Path[(entry.Path.LastIndexOf('/') + 1)..];
            if (ForbiddenFiles.Contains(name, StringComparer.OrdinalIgnoreCase) || name.StartsWith("Magpienest.App.", StringComparison.OrdinalIgnoreCase))
            {
                throw new PackageException(PluginInstallError.ForbiddenFile, $"The package contains '{entry.Path}', which belongs to Magpienest itself.");
            }
        }

        var (prefix, manifestEntry) = FindManifest(entries);
        var manifest = ReadManifest(reader, manifestEntry);
        var id = manifest.Id;
        if (manifest.ApiVersion != Kernel.ApiVersion)
        {
            throw new PackageException(
                PluginInstallError.UnsupportedApiVersion,
                $"Plugin API version {manifest.ApiVersion} is not supported (this version of Magpienest uses {Kernel.ApiVersion}).",
                id);
        }

        var assemblyPath = prefix + manifest.Assembly;
        if (!entries.Any(e => !e.IsDirectory && string.Equals(e.Path, assemblyPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new PackageException(PluginInstallError.AssemblyMissing, $"The plugin assembly '{manifest.Assembly}' is not in the package.", id);
        }

        if (builtInIds.Contains(id))
        {
            throw new PackageException(PluginInstallError.BuiltInId, $"'{id}' is the id of a built-in plugin.", id);
        }

        return new PluginPackage(reader, manifest, prefix, entries);
    }

    private static (string Prefix, PackageEntry Manifest) FindManifest(List<PackageEntry> entries)
    {
        var root = entries.FirstOrDefault(e => !e.IsDirectory && string.Equals(e.Path, ManifestName, StringComparison.OrdinalIgnoreCase));
        if (root is not null)
        {
            return (string.Empty, root);
        }

        var folders = entries
            .Where(e => e.IsDirectory || e.Path.Contains('/'))
            .Select(e => e.Path.Split('/')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (folders.Count == 1)
        {
            var wanted = folders[0] + "/" + ManifestName;
            var nested = entries.FirstOrDefault(e => !e.IsDirectory && string.Equals(e.Path, wanted, StringComparison.OrdinalIgnoreCase));
            if (nested is not null)
            {
                return (folders[0] + "/", nested);
            }
        }

        throw new PackageException(
            PluginInstallError.ManifestMissing,
            "The package has no plugin.json at its root or inside a single top-level folder.");
    }

    private static PluginManifest ReadManifest(Reader reader, PackageEntry entry)
    {
        if (entry.Size > MaxManifestBytes)
        {
            throw new PackageException(PluginInstallError.ManifestInvalid, "plugin.json is too large.");
        }

        string text;
        try
        {
            using var stream = reader.OpenRead(entry);
            using var streamReader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = streamReader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new PackageException(PluginInstallError.ManifestInvalid, $"plugin.json could not be read: {ex.Message}");
        }

        try
        {
            return ManifestReader.Parse(text);
        }
        catch (InvalidDataException ex)
        {
            throw new PackageException(ManifestReader.IsIdError(ex) ? PluginInstallError.InvalidId : PluginInstallError.ManifestInvalid, ex.Message);
        }
    }

    /// <summary>Converts '\' to '/', drops a trailing '/', and rejects paths that cannot be extracted safely.</summary>
    private static string Normalize(string name)
    {
        var path = name.Replace('\\', '/').TrimEnd('/');
        if (path.Length == 0 || path.StartsWith('/'))
        {
            throw Unsafe(name);
        }

        var invalid = Path.GetInvalidFileNameChars();
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0
                || segment.EndsWith('.') || segment.EndsWith(' ')
                || segment.IndexOfAny(invalid) >= 0
                || ReservedNames.Contains(segment.Split('.')[0].TrimEnd()))
            {
                throw Unsafe(name);
            }
        }

        return path;
    }

    private bool TryStrip(string path, out string relative)
    {
        if (_prefix.Length == 0)
        {
            relative = path;
            return true;
        }

        if (path.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
        {
            relative = path[_prefix.Length..];
            return true;
        }

        // The top-level folder entry itself, or something outside the plugin folder.
        relative = string.Empty;
        return false;
    }

    private static bool IsInside(string root, string fullPath) =>
        fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static PackageException Unsafe(string name) =>
        new(PluginInstallError.UnsafePath, $"Package entry '{name}' has an unsafe path.");

    private static PackageException TooLarge(string? id) =>
        new(PluginInstallError.TooLarge, $"The package is larger than {MaxTotalBytes / (1024 * 1024)} MB when uncompressed.", id);

    private abstract class Reader : IDisposable
    {
        /// <summary>Raw entries; <see cref="PackageEntry.Source"/> identifies the entry to <see cref="OpenRead"/>.</summary>
        public abstract IReadOnlyList<PackageEntry> List();

        public abstract Stream OpenRead(PackageEntry entry);

        public virtual void Dispose()
        {
        }
    }

    private sealed class ZipReader : Reader
    {
        private readonly ZipArchive _archive;

        public ZipReader(string path) => _archive = ZipFile.OpenRead(path);

        public override IReadOnlyList<PackageEntry> List() =>
            [.. _archive.Entries.Select(e => new PackageEntry(
                e.FullName,
                e.Length,
                e.FullName.EndsWith('/') || e.FullName.EndsWith('\\'),
                e.FullName))];

        public override Stream OpenRead(PackageEntry entry) =>
            (_archive.GetEntry(entry.Source) ?? throw new InvalidDataException($"Entry '{entry.Source}' is missing.")).Open();

        public override void Dispose() => _archive.Dispose();
    }

    private sealed class FolderReader(string root) : Reader
    {
        private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        public override IReadOnlyList<PackageEntry> List()
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            var entries = new List<PackageEntry>();
            foreach (var file in new DirectoryInfo(_root).EnumerateFiles("*", options))
            {
                var relative = Path.GetRelativePath(_root, file.FullName).Replace('\\', '/');
                entries.Add(new PackageEntry(relative, file.Length, IsDirectory: false, file.FullName));
                if (entries.Count > MaxEntries)
                {
                    break;
                }
            }

            return entries;
        }

        public override Stream OpenRead(PackageEntry entry) => File.OpenRead(entry.Source);
    }
}
