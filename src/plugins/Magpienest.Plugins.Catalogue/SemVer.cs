namespace Magpienest.Plugins.Catalogue;

/// <summary>Semantic version comparison ("1.2.3", "1.2.3-beta.1"; build metadata after '+' is ignored).</summary>
internal static class SemVer
{
    /// <summary>True when <paramref name="version"/> is 1 to 4 dot-separated numbers, optionally followed by a pre-release part.</summary>
    public static bool IsValid(string? version) => TryParse(version, out _, out _);

    /// <summary>Compares two versions; an invalid version sorts below every valid one.</summary>
    public static int Compare(string? left, string? right)
    {
        var leftValid = TryParse(left, out var leftCore, out var leftPre);
        var rightValid = TryParse(right, out var rightCore, out var rightPre);
        if (!leftValid || !rightValid)
        {
            return leftValid.CompareTo(rightValid);
        }

        for (var i = 0; i < 4; i++)
        {
            var a = i < leftCore.Length ? leftCore[i] : 0;
            var b = i < rightCore.Length ? rightCore[i] : 0;
            if (a != b)
            {
                return a.CompareTo(b);
            }
        }

        // A release is higher than any of its pre-releases.
        if (leftPre.Length == 0 || rightPre.Length == 0)
        {
            return rightPre.Length.CompareTo(leftPre.Length) switch { > 0 => 1, < 0 => -1, _ => 0 };
        }

        for (var i = 0; i < Math.Min(leftPre.Length, rightPre.Length); i++)
        {
            var result = CompareIdentifier(leftPre[i], rightPre[i]);
            if (result != 0)
            {
                return result;
            }
        }

        return leftPre.Length.CompareTo(rightPre.Length);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumber = long.TryParse(left, out var a);
        var rightNumber = long.TryParse(right, out var b);
        return (leftNumber, rightNumber) switch
        {
            (true, true) => a.CompareTo(b),
            (true, false) => -1,
            (false, true) => 1,
            _ => string.CompareOrdinal(left, right),
        };
    }

    private static bool TryParse(string? version, out int[] core, out string[] pre)
    {
        core = [];
        pre = [];
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var text = version.Trim();
        var plus = text.IndexOf('+');
        if (plus >= 0)
        {
            text = text[..plus];
        }

        var dash = text.IndexOf('-');
        var corePart = dash >= 0 ? text[..dash] : text;
        if (dash >= 0)
        {
            pre = text[(dash + 1)..].Split('.');
            if (pre.Any(p => p.Length == 0 || !p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
            {
                return false;
            }
        }

        var parts = corePart.Split('.');
        if (parts.Length is < 1 or > 4)
        {
            return false;
        }

        core = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit) || !int.TryParse(parts[i], out core[i]))
            {
                return false;
            }
        }

        return true;
    }
}
