using System.Text.Json;
using System.Text.RegularExpressions;
using Magpienest.Sdk;

namespace Magpienest.Core;

/// <summary>Parses and validates plugin.json (architecture §5.3).</summary>
internal static partial class ManifestReader
{
    /// <summary>Parses a manifest; throws <see cref="InvalidDataException"/> when invalid.</summary>
    public static PluginManifest Parse(string json)
    {
        PluginManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PluginManifest>(json, JsonFile.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Invalid plugin.json: {ex.Message}", ex);
        }

        if (manifest is null)
        {
            throw new InvalidDataException("Invalid plugin.json: empty.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Id) || !IdPattern().IsMatch(manifest.Id))
        {
            throw new InvalidDataException($"Invalid plugin id '{manifest.Id}': use lower-case letters, digits and dots.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            throw new InvalidDataException($"Plugin '{manifest.Id}' has no version.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Assembly) || Path.GetFileName(manifest.Assembly) != manifest.Assembly)
        {
            throw new InvalidDataException($"Plugin '{manifest.Id}' has an invalid assembly file name.");
        }

        return manifest with
        {
            Name = new Dictionary<string, string>(manifest.Name ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
            Description = new Dictionary<string, string>(manifest.Description ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>Reads and parses a plugin.json file.</summary>
    public static PluginManifest Read(string path) => Parse(File.ReadAllText(path));

    [GeneratedRegex("^[a-z0-9]+(\\.[a-z0-9]+)*$")]
    private static partial Regex IdPattern();
}
