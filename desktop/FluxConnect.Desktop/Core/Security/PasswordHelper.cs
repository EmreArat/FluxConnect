using System.Security.Cryptography;
using System.Text;

namespace FluxConnect.Desktop.Core.Security;

public static class PasswordHelper
{
    public static string Hash(string password)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static bool Verify(string password, string? storedHash)
    {
        if (string.IsNullOrEmpty(storedHash) || string.IsNullOrWhiteSpace(password))
            return false;
        return Hash(password) == storedHash;
    }

    public static bool VerifyHash(string? passwordHash, string? storedHash)
    {
        if (string.IsNullOrEmpty(storedHash) || string.IsNullOrEmpty(passwordHash))
            return false;
        return passwordHash.Equals(storedHash, StringComparison.OrdinalIgnoreCase);
    }
}
