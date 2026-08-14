using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace FluxConnect.Desktop.Core.Security;

/// <summary>
/// Relay sunucusunun TLS sertifikasını SHA-256 parmak izi ile doğrular.
/// Kendinden imzalı sertifika kullanan sunuculara güvenli bağlanmayı sağlar.
/// </summary>
public static class CertificatePinning
{
    private const int FingerprintLength = 64; // SHA-256 = 32 bayt = 64 hex hane

    /// <summary>
    /// Kullanıcının girdiği parmak izini ayraçlardan arındırıp doğrular.
    /// Boş giriş geçerlidir ve pinleme kapalı anlamına gelir.
    /// </summary>
    public static bool TryNormalize(string? input, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;

        var raw = (input ?? string.Empty).Trim();
        if (raw.Length == 0)
            return true;

        var builder = new StringBuilder(FingerprintLength);
        foreach (var c in raw)
        {
            if (c is ':' or '-' or ' ') continue;
            if (!Uri.IsHexDigit(c))
            {
                error = "Parmak izi yalnızca 0-9 ve A-F karakterlerinden oluşmalıdır.";
                return false;
            }
            builder.Append(char.ToUpperInvariant(c));
        }

        if (builder.Length != FingerprintLength)
        {
            error = $"Parmak izi {FingerprintLength} haneli olmalıdır (SHA-256). Girilen: {builder.Length} hane.";
            return false;
        }

        normalized = builder.ToString();
        return true;
    }

    /// <summary>Parmak izini okunabilir biçimde (iki haneli gruplar, arada iki nokta) döndürür.</summary>
    public static string ToDisplay(string normalized)
    {
        if (string.IsNullOrEmpty(normalized)) return string.Empty;

        var builder = new StringBuilder(normalized.Length + normalized.Length / 2);
        for (int i = 0; i < normalized.Length; i += 2)
        {
            if (i > 0) builder.Append(':');
            builder.Append(normalized, i, Math.Min(2, normalized.Length - i));
        }
        return builder.ToString();
    }

    /// <summary>Sertifikanın SHA-256 parmak izini hex olarak hesaplar.</summary>
    public static string ComputeFingerprint(X509Certificate certificate)
    {
        var hash = SHA256.HashData(certificate.GetRawCertData());
        return Convert.ToHexString(hash);
    }
}
