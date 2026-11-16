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

        if (!IsValidId(manifest.Id))
        {
            throw IdError($"Invalid plugin id '{manifest.Id}': use lower-case letters, digits and dots.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            throw new InvalidDataException($"Plugin '{manifest.Id}' has no version.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Assembly) || Path.GetFileName(manifest.Assembly) != manifest.Assembly)
        {
            throw new InvalidDataException($"Plugin '{manifest.Id}' has an invalid assembly file name.");
        }

        if (manifest.Bundle is not null && !IsValidId(manifest.Bundle))
        {
            throw new InvalidDataException($"Plugin '{manifest.Id}' has an invalid bundle id '{manifest.Bundle}'.");
        }

        return manifest with
        {
            Name = new Dictionary<string, string>(manifest.Name ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
            Description = new Dictionary<string, string>(manifest.Description ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>Reads and parses a plugin.json file.</summary>
    public static PluginManifest Read(string path) => Parse(File.ReadAllText(path));

    /// <summary>True when the exception came from an invalid plugin id (as opposed to another manifest problem).</summary>
    public static bool IsIdError(InvalidDataException ex) => ex.Data.Contains(IdErrorKey);

    /// <summary>True when <paramref name="id"/> is a valid plugin id.</summary>
    public static bool IsValidId(string? id) => !string.IsNullOrWhiteSpace(id) && IdPattern().IsMatch(id);

    private const string IdErrorKey = "pluginIdError";

    private static InvalidDataException IdError(string message)
    {
        var ex = new InvalidDataException(message);
        ex.Data[IdErrorKey] = true;
        return ex;
    }

    [GeneratedRegex("^[a-z0-9]+(\\.[a-z0-9]+)*$")]
    private static partial Regex IdPattern();
}

