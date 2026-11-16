using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Magpienest.Plugins.WebUi;

/// <summary>The HTTPS setting.</summary>
internal enum HttpsMode
{
    /// <summary>Plain HTTP.</summary>
    Off,

    /// <summary>A self-signed certificate made by the plugin.</summary>
    Automatic,

    /// <summary>A .pfx file chosen by the user.</summary>
    Own,
}

/// <summary>
/// Certificates for HTTPS (phase 7 "HTTPS"). The automatic one is <c>cert.pfx</c> in the plugin's data folder with a
/// random password encrypted by DPAPI. Keys are loaded into the user key set: Schannel cannot use ephemeral keys (T7.1).
/// </summary>
internal sealed class CertificateStore(string dataDirectory, WebUiSettings settings)
{
    /// <summary>How long a new automatic certificate is valid.</summary>
    public static readonly TimeSpan Validity = TimeSpan.FromDays(5 * 365);

    private string AutomaticPath => Path.Combine(dataDirectory, "cert.pfx");

    /// <summary>The certificate for the current settings, or null for plain HTTP.</summary>
    /// <param name="names">Host names and addresses for a new automatic certificate.</param>
    public X509Certificate2? Current(IReadOnlyCollection<string> names) => settings.EffectiveHttps switch
    {
        HttpsMode.Automatic => LoadAutomatic() ?? CreateAutomatic(names),
        HttpsMode.Own => LoadOwn(),
        _ => null,
    };

    /// <summary>Makes and stores a new automatic certificate (also "Create new certificate" in settings).</summary>
    /// <param name="names">DNS names and IP addresses it is valid for.</param>
    public X509Certificate2 CreateAutomatic(IReadOnlyCollection<string> names)
    {
        var password = RequestGuards.NewToken();
        var pfx = CreateSelfSigned(names, password, DateTimeOffset.UtcNow);
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllBytes(AutomaticPath, pfx);
        settings.AutoCertificatePassword = Protect(password);
        return Load(pfx, password);
    }

    /// <summary>Checks a user's .pfx and password; returns null when it cannot be used for a server.</summary>
    /// <param name="path">File.</param>
    /// <param name="password">Its password.</param>
    public static X509Certificate2? TryLoad(string path, string password)
    {
        try
        {
            var certificate = Load(File.ReadAllBytes(path), password);
            if (certificate.HasPrivateKey)
            {
                return certificate;
            }

            certificate.Dispose();
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // Not a certificate, wrong password or unreadable.
        }

        return null;
    }

    /// <summary>A self-signed server certificate as a password-protected PKCS#12 file.</summary>
    /// <param name="names">DNS names and IP addresses.</param>
    /// <param name="password">PFX password.</param>
    /// <param name="now">Start of validity.</param>
    public static byte[] CreateSelfSigned(IReadOnlyCollection<string> names, string password, DateTimeOffset now)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Magpienest", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IPAddress.TryParse(name, out var address))
            {
                san.AddIpAddress(address);
            }
            else if (name.Length > 0)
            {
                san.AddDnsName(name);
            }
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now + Validity);
        return certificate.Export(X509ContentType.Pfx, password);
    }

    /// <summary>Encrypts a secret for the current Windows user.</summary>
    /// <param name="secret">Plain text.</param>
    public static string Protect(string secret) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));

    /// <summary>Decrypts <see cref="Protect"/>; null when it cannot (another user, damaged).</summary>
    /// <param name="protectedSecret">Base64 from <see cref="Protect"/>.</param>
    public static string? Unprotect(string? protectedSecret)
    {
        if (string.IsNullOrEmpty(protectedSecret))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    private X509Certificate2? LoadAutomatic()
    {
        if (!File.Exists(AutomaticPath) || Unprotect(settings.AutoCertificatePassword) is not { } password)
        {
            return null;
        }

        var certificate = TryLoad(AutomaticPath, password);
        if (certificate is not null && certificate.NotAfter.ToUniversalTime() < DateTime.UtcNow.AddDays(7))
        {
            certificate.Dispose();
            return null;
        }

        return certificate;
    }

    private X509Certificate2? LoadOwn() =>
        settings.OwnCertificatePath is { } path && Unprotect(settings.OwnCertificatePassword) is { } password
            ? TryLoad(path, password)
            : null;

    private static X509Certificate2 Load(byte[] pfx, string password) =>
        X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.UserKeySet);
}
