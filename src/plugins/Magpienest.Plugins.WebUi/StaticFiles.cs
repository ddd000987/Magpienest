namespace Magpienest.Plugins.WebUi;

/// <summary>Maps request paths to files in the plugin's <c>www</c> folder. Never leaves that folder.</summary>
internal sealed class StaticFiles(string root)
{
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".ico"] = "image/x-icon",
        [".woff2"] = "font/woff2",
    };

    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    /// <summary>The content type served for a file extension, or null when such files are never served (.svg included).</summary>
    /// <param name="extension">Extension with the dot.</param>
    public static string? ContentType(string extension) => Types.GetValueOrDefault(extension);

    /// <summary>The file for a request path ("/" is index.html) and its content type, or null.</summary>
    /// <param name="requestPath">The URL path, still percent-encoded.</param>
    public (string Path, string ContentType)? Resolve(string requestPath)
    {
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(requestPath);
        }
        catch (UriFormatException)
        {
            return null;
        }

        if (decoded == "/" || decoded.Length == 0)
        {
            decoded = "/index.html";
        }

        // No back-slashes, drive letters, alternate streams, parent or hidden segments.
        if (!decoded.StartsWith('/') || decoded.Contains('\\') || decoded.Contains(':') || decoded.Contains('\0'))
        {
            return null;
        }

        var segments = decoded[1..].Split('/');
        if (segments.Any(s => s.Length == 0 || s.StartsWith('.')))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine([_root, .. segments]));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var type = ContentType(Path.GetExtension(full));
        return type is not null && File.Exists(full) ? (full, type) : null;
    }
}
