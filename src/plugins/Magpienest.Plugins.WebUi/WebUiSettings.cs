using System.Globalization;
using Magpienest.Sdk;

namespace Magpienest.Plugins.WebUi;

/// <summary>The plugin's stored settings (config/magpienest.webui.json).</summary>
internal sealed class WebUiSettings(IPluginConfig config)
{
    internal const string PortKey = "port";
    internal const string TokenKey = "token";

    /// <summary>Default port.</summary>
    public const int DefaultPort = 47290;

    /// <summary>Lowest port the user may choose (below are system ports).</summary>
    public const int MinPort = 1024;

    /// <summary>Highest TCP port.</summary>
    public const int MaxPort = 65535;

    public int Port
    {
        get => IsValidPort(config.Get(PortKey, DefaultPort)) ? config.Get(PortKey, DefaultPort) : DefaultPort;
        set => config.Set(PortKey, IsValidPort(value) ? value : DefaultPort);
    }

    /// <summary>The access token; created on first use.</summary>
    public string Token
    {
        get
        {
            var token = config.Get<string?>(TokenKey, null);
            if (string.IsNullOrEmpty(token))
            {
                token = RequestGuards.NewToken();
                config.Set(TokenKey, token);
            }

            return token;
        }
    }

    internal const string AccessKey = "access";
    internal const string RequirePasswordKey = "requirePasswordAtHome";
    internal const string TailscaleKey = "tailscaleIsHome";
    internal const string ExtraHostsKey = "extraHosts";
    internal const string PasswordKey = "passwordHash";
    internal const string HttpsKey = "https";
    internal const string OwnCertificateKey = "ownCertificate";
    internal const string OwnCertificatePasswordKey = "ownCertificatePassword";
    internal const string AutoCertificatePasswordKey = "autoCertificatePassword";
    internal const string ShowPageKey = "showPage";

    /// <summary>Who can connect.</summary>
    public AccessMode Access
    {
        get => Enum.TryParse<AccessMode>(config.Get(AccessKey, nameof(AccessMode.Local)), out var mode) && Enum.IsDefined(mode)
            ? mode
            : AccessMode.Local;
        set => config.Set(AccessKey, value.ToString());
    }

    /// <summary>Whether home-network devices must sign in (default on).</summary>
    public bool RequirePasswordAtHome
    {
        get => config.Get(RequirePasswordKey, true);
        set => config.Set(RequirePasswordKey, value);
    }

    /// <summary>Whether the "Web" page is in the navigation (default on).</summary>
    public bool ShowPage
    {
        get => config.Get(ShowPageKey, true);
        set => config.Set(ShowPageKey, value);
    }

    /// <summary>Whether 100.64.0.0/10 (Tailscale) counts as home network (default on).</summary>
    public bool TailscaleIsHome
    {
        get => config.Get(TailscaleKey, true);
        set => config.Set(TailscaleKey, value);
    }

    /// <summary>Extra host names allowed without a password.</summary>
    public IReadOnlyList<string> ExtraHosts
    {
        get => config.Get<string[]?>(ExtraHostsKey, null) ?? [];
        set => config.Set<string[]?>(ExtraHostsKey, value.Count == 0 ? null : [.. value]);
    }

    /// <summary>The PBKDF2 hash of the password, or null when none is set.</summary>
    public string? PasswordHash
    {
        get => config.Get<string?>(PasswordKey, null);
        set => config.Set(PasswordKey, value);
    }

    /// <summary>Whether a password exists.</summary>
    public bool HasPassword => !string.IsNullOrEmpty(PasswordHash);

    /// <summary>The HTTPS setting as chosen.</summary>
    public HttpsMode Https
    {
        get => Enum.TryParse<HttpsMode>(config.Get(HttpsKey, nameof(HttpsMode.Off)), out var mode) && Enum.IsDefined(mode)
            ? mode
            : HttpsMode.Off;
        set => config.Set(HttpsKey, value.ToString());
    }

    /// <summary>The HTTPS mode in force: internet access never runs without HTTPS.</summary>
    public HttpsMode EffectiveHttps => Access == AccessMode.Internet && Https == HttpsMode.Off ? HttpsMode.Automatic : Https;

    /// <summary>The user's own certificate file (.pfx), for <see cref="HttpsMode.Own"/>.</summary>
    public string? OwnCertificatePath
    {
        get => config.Get<string?>(OwnCertificateKey, null);
        set => config.Set(OwnCertificateKey, value);
    }

    /// <summary>Its password, encrypted with DPAPI (current user).</summary>
    public string? OwnCertificatePassword
    {
        get => config.Get<string?>(OwnCertificatePasswordKey, null);
        set => config.Set(OwnCertificatePasswordKey, value);
    }

    /// <summary>The automatic certificate's random password, encrypted with DPAPI (current user).</summary>
    public string? AutoCertificatePassword
    {
        get => config.Get<string?>(AutoCertificatePasswordKey, null);
        set => config.Set(AutoCertificatePasswordKey, value);
    }

    /// <summary>Parses the "extra host names" box: comma or space separated, trimmed, lower-case, distinct.</summary>
    /// <param name="text">User text.</param>
    public static IReadOnlyList<string> ParseHosts(string? text) =>
        [.. (text ?? "").Split([',', ' ', ';', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(h => h.Trim().TrimEnd('.').ToLowerInvariant())
            .Where(h => h.Length > 0 && h.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
            .Distinct()];

    /// <summary>Replaces the token; links made with the old one stop working.</summary>
    public string ResetToken()
    {
        var token = RequestGuards.NewToken();
        config.Set(TokenKey, token);
        return token;
    }

    /// <summary>Whether a port number may be used.</summary>
    /// <param name="port">Port.</param>
    public static bool IsValidPort(int port) => port is >= MinPort and <= MaxPort;

    /// <summary>Parses a port typed by the user; null when it is not a valid port.</summary>
    /// <param name="text">User text.</param>
    public static int? ParsePort(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && IsValidPort(port) ? port : null;

    /// <summary>The <c>--webui-port &lt;p&gt;</c> command-line override (tests), or null.</summary>
    /// <param name="args">Command-line arguments.</param>
    public static int? PortOverride(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "--webui-port" &&
                int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= MaxPort)
            {
                return port;
            }
        }

        return null;
    }
}
