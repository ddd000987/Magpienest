using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Magpienest.Plugins.WebUi;

/// <summary>PBKDF2-SHA256 password hashes, stored as <c>pbkdf2-sha256$&lt;iterations&gt;$&lt;salt&gt;$&lt;hash&gt;</c>.</summary>
internal static class PasswordHasher
{
    /// <summary>Iterations for new hashes (phase 7 "Password and sessions").</summary>
    public const int Iterations = 600_000;

    /// <summary>Shortest password accepted.</summary>
    public const int MinLength = 8;

    private const string Scheme = "pbkdf2-sha256";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>Whether a password is long enough.</summary>
    /// <param name="password">Candidate.</param>
    public static bool IsAcceptable(string? password) => password is not null && password.Length >= MinLength;

    /// <summary>Hashes a password with a new random salt. Slow on purpose: call it off the UI thread.</summary>
    /// <param name="password">The password.</param>
    /// <param name="iterations">Iterations (tests use fewer).</param>
    public static string Hash(string password, int iterations = Iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return string.Join('$', Scheme, iterations.ToString(CultureInfo.InvariantCulture),
                           Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    /// <summary>Checks a password against a stored hash in time that does not depend on where they differ.</summary>
    /// <param name="password">What the user typed.</param>
    /// <param name="stored">The stored hash; null or malformed never matches.</param>
    public static bool Verify(string? password, string? stored)
    {
        if (password is null || stored is null)
        {
            return false;
        }

        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) || iterations < 1)
        {
            return false;
        }

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
