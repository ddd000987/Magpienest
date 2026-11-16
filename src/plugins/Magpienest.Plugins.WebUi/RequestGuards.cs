using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Magpienest.Plugins.WebUi;

/// <summary>Pure checks applied to every request.</summary>
internal static class RequestGuards
{
    /// <summary>Creates a new access token: 32 random bytes, base64url without padding.</summary>
    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>Base64url without padding.</summary>
    /// <param name="bytes">Bytes to encode.</param>
    public static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Compares two secrets in time that does not depend on where they differ.</summary>
    /// <param name="given">The value from the request (may be null).</param>
    /// <param name="expected">The stored secret.</param>
    public static bool SecretEquals(string? given, string expected)
    {
        if (given is null || expected.Length == 0)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));
    }

    /// <summary>The host name of a <c>Host</c> header without its port; IPv6 literals lose their brackets. Null when malformed.</summary>
    /// <param name="hostHeader">The header value.</param>
    public static string? HostName(string? hostHeader)
    {
        if (string.IsNullOrWhiteSpace(hostHeader))
        {
            return null;
        }

        var value = hostHeader.Trim();
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close < 0)
            {
                return null;
            }

            var rest = value[(close + 1)..];
            if (rest.Length > 0 && (rest[0] != ':' || !ushort.TryParse(rest[1..], out _)))
            {
                return null;
            }

            return value[1..close];
        }

        var colon = value.LastIndexOf(':');
        if (colon >= 0)
        {
            if (!ushort.TryParse(value[(colon + 1)..], out _))
            {
                return null;
            }

            value = value[..colon];
        }

        return value.Length == 0 ? null : value.TrimEnd('.');
    }

    /// <summary>
    /// Whether a request that can get in without a password may use this <c>Host</c>: localhost, an IP literal,
    /// the machine name, <c>&lt;machine&gt;.local</c> or one of the extra names. Defeats DNS rebinding.
    /// </summary>
    /// <param name="hostHeader">The <c>Host</c> header.</param>
    /// <param name="machineName">This computer's name.</param>
    /// <param name="extraHosts">Names the user added in settings.</param>
    public static bool IsAllowedHost(string? hostHeader, string machineName, IReadOnlyCollection<string> extraHosts)
    {
        var name = HostName(hostHeader);
        if (name is null)
        {
            return false;
        }

        // "1" or "1.2" also parse as IPv4; only the full dotted form counts as a literal.
        if (IPAddress.TryParse(name, out var address) &&
            (address.AddressFamily == AddressFamily.InterNetworkV6 || name.Count(c => c == '.') == 3))
        {
            return true;
        }

        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
               || name.Equals(machineName, StringComparison.OrdinalIgnoreCase)
               || name.Equals(machineName + ".local", StringComparison.OrdinalIgnoreCase)
               || extraHosts.Any(h => name.Equals(h.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether a write request's <c>Origin</c> is this server itself (cross-site request forgery guard).</summary>
    /// <param name="origin">The <c>Origin</c> header (null when absent).</param>
    /// <param name="secure">Whether the request came over HTTPS.</param>
    /// <param name="hostHeader">The <c>Host</c> header.</param>
    public static bool IsSameOrigin(string? origin, bool secure, string? hostHeader)
    {
        if (string.IsNullOrEmpty(origin) || string.IsNullOrEmpty(hostHeader))
        {
            return false;
        }

        var expected = (secure ? "https://" : "http://") + hostHeader.Trim();
        if (string.Equals(origin, expected, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Browsers leave out the default port in Origin while some clients keep it in Host (and the other way round).
        return Uri.TryCreate(origin, UriKind.Absolute, out var o) && Uri.TryCreate(expected, UriKind.Absolute, out var e)
               && o.Scheme == e.Scheme && o.Port == e.Port
               && string.Equals(o.IdnHost, e.IdnHost, StringComparison.OrdinalIgnoreCase)
               && o.AbsolutePath == "/";
    }

    /// <summary>Reads one cookie from a <c>Cookie</c> header.</summary>
    /// <param name="cookieHeader">The header value.</param>
    /// <param name="name">Cookie name.</param>
    public static string? Cookie(string? cookieHeader, string name)
    {
        if (string.IsNullOrEmpty(cookieHeader))
        {
            return null;
        }

        foreach (var part in cookieHeader.Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].Trim() == name)
            {
                return part[(eq + 1)..].Trim();
            }
        }

        return null;
    }

    /// <summary>The token of an <c>Authorization: Bearer …</c> header, or null.</summary>
    /// <param name="authorization">The header value.</param>
    public static string? Bearer(string? authorization) =>
        authorization is not null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization[7..].Trim()
            : null;
}
