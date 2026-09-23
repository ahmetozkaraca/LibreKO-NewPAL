using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LibreKO.Common.Domain.Services;

public static class PasswordHasher
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 100_000;
    private const char FieldSeparator = '.';
    private const int FieldCount = 3;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;
    private static readonly byte[] DecoySalt = RandomNumberGenerator.GetBytes(SaltSize);

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Derive(password, salt, Iterations, HashSize);
        return $"{Iterations}{FieldSeparator}{Convert.ToBase64String(salt)}{FieldSeparator}{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? storedHash)
    {
        if (storedHash != null && TryParse(storedHash, out var iterations, out var salt, out var hash))
            return CryptographicOperations.FixedTimeEquals(hash, Derive(password, salt, iterations, hash.Length));

        Derive(password, DecoySalt, Iterations, HashSize);
        return storedHash != null
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(storedHash));
    }

    public static bool NeedsRehash(string storedHash) =>
        !TryParse(storedHash, out var iterations, out var salt, out var hash)
        || iterations != Iterations
        || salt.Length != SaltSize
        || hash.Length != HashSize;

    private static byte[] Derive(string password, byte[] salt, int iterations, int length) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, Algorithm, length);

    private static bool TryParse(string storedHash, out int iterations, out byte[] salt, out byte[] hash)
    {
        salt = [];
        hash = [];

        var parts = storedHash.Split(FieldSeparator);
        if (parts.Length != FieldCount
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out iterations)
            || iterations <= 0)
        {
            iterations = 0;
            return false;
        }

        return TryDecode(parts[1], out salt) && salt.Length > 0
            && TryDecode(parts[2], out hash) && hash.Length > 0;
    }

    private static bool TryDecode(string text, out byte[] bytes)
    {
        var buffer = new byte[text.Length];
        if (Convert.TryFromBase64String(text, buffer, out var written))
        {
            bytes = buffer[..written];
            return true;
        }

        bytes = [];
        return false;
    }
}
