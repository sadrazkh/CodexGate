using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace CodexGate.Security;

/// <summary>PBKDF2-SHA256 password hashes: "pbkdf2-sha256$iterations$salt$hash" (base64).</summary>
public static class PasswordHasher
{
    private const string Scheme = "pbkdf2-sha256";
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>A real hash of a random password, verified when a username is unknown so timing does not tell.</summary>
    public static readonly string DummyHash = Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)));

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], out var iterations) || iterations < 1)
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
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static bool NeedsRehash(string stored)
    {
        var parts = stored.Split('$');
        return parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], out var i) || i < Iterations;
    }

    /// <summary>A readable random password for new accounts (no look-alike characters).</summary>
    public static string Generate(int length = 14)
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return RandomNumberGenerator.GetString(alphabet, length);
    }
}

/// <summary>Gateway API keys: "cgk_" + 32 random bytes; only their SHA-256 is stored.</summary>
public static class ApiKeys
{
    public const string Prefix = "cgk_";

    public static string Generate()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Prefix + Base64Url.EncodeToString(bytes);
    }

    public static byte[] HashOf(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    /// <summary>What the panel shows of a key: enough to tell keys apart, useless to an attacker.</summary>
    public static string DisplayPrefix(string key) => key[..Math.Min(key.Length, Prefix.Length + 6)];

    public static bool LooksLikeKey(string? value) =>
        value is not null && value.StartsWith(Prefix, StringComparison.Ordinal) && value.Length is > 20 and < 100;

    public static string NewStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
}
