using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Magpienest.Sdk;

namespace Magpienest.Core;

/// <summary>Config-file helpers: UTF-8 without BOM, indented, atomic writes, corrupt-file recovery.</summary>
internal static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Reads a JSON object. Missing file → null. Corrupt file → renamed to *.corrupt-&lt;timestamp&gt;, null.</summary>
    public static JsonObject? ReadObject(string path, ILog log)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(path);
            if (JsonNode.Parse(text, documentOptions: DocumentOptions) is JsonObject obj)
            {
                return obj;
            }

            throw new JsonException("The root is not a JSON object.");
        }
        catch (JsonException ex)
        {
            var target = $"{path}.corrupt-{DateTime.Now.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)}";
            log.Warn($"Config file '{path}' is corrupt; renamed to '{target}', using defaults.", ex);
            try
            {
                File.Move(path, target, overwrite: true);
            }
            catch (IOException moveError)
            {
                log.Error($"Could not rename corrupt file '{path}'.", moveError);
            }

            return null;
        }
        catch (IOException ex)
        {
            log.Error($"Could not read '{path}'; using defaults.", ex);
            return null;
        }
    }

    /// <summary>Writes <c>*.tmp</c>, then moves it over the target.</summary>
    public static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, Utf8NoBom);
        File.Move(temp, path, overwrite: true);
    }
}
