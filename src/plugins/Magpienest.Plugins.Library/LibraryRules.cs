using System.Globalization;
using System.Text;

namespace Magpienest.Plugins.Library;

/// <summary>Pure rules of the data model: path normalisation, tags, titles, times.</summary>
internal static class LibraryRules
{
    public const int MaxTagLength = 100;

    /// <summary>Natural order (<c>2</c> before <c>10</c>), case-insensitive (architecture pitfall 12).</summary>
    public static readonly StringComparer Natural =
        StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.NumericOrdering | CompareOptions.IgnoreCase);

    /// <summary><c>GetFullPath</c>, no trailing separator except on a drive root, original casing kept.</summary>
    public static string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path);
        var rootLength = Path.GetPathRoot(full)?.Length ?? 0;
        var end = full.Length;
        while (end > rootLength && (full[end - 1] == Path.DirectorySeparatorChar || full[end - 1] == Path.AltDirectorySeparatorChar))
        {
            end--;
        }

        return end == full.Length ? full : full[..end];
    }

    /// <summary>The <c>dir</c> column value of a normalised path.</summary>
    public static string DirectoryOf(string normalizedPath) => Path.GetDirectoryName(normalizedPath) ?? "";

    /// <summary>True when <paramref name="child"/> equals <paramref name="parent"/> or lies below it (both normalised).</summary>
    public static bool IsSameOrInside(string child, string parent)
    {
        if (string.Equals(child, parent, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar;
        return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// "Title was auto": the stored title still equals what a scanner produces from the path
    /// (file or folder name, with or without extension).
    /// </summary>
    public static bool IsAutoTitle(string title, string path) =>
        string.Equals(title, Path.GetFileName(path), StringComparison.Ordinal)
        || string.Equals(title, Path.GetFileNameWithoutExtension(path), StringComparison.Ordinal);

    public static long ToUnixMs(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    public static DateTimeOffset FromUnixMs(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);

    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Trims, collapses inner whitespace, caps at <see cref="MaxTagLength"/>; null when nothing is left.</summary>
    public static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var builder = new StringBuilder(tag.Length);
        var pendingSpace = false;
        foreach (var ch in tag)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        if (builder.Length > MaxTagLength)
        {
            builder.Length = MaxTagLength;
            while (builder.Length > 0 && builder[^1] == ' ')
            {
                builder.Length--;
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>Normalised, case-insensitively de-duplicated tags (first spelling wins).</summary>
    public static List<string> NormalizeTags(IEnumerable<string> tags)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var tag in tags)
        {
            var normalized = NormalizeTag(tag);
            if (normalized is not null && seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    /// <summary>Escapes <c>\</c>, <c>%</c> and <c>_</c> for <c>LIKE … ESCAPE '\'</c>.</summary>
    public static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
