using Microsoft.Win32;

namespace FluxConnect.Desktop.Core.Hardware;

/// <summary>
/// Bilgisayar için sabit kimlik sağlar — Windows MachineGuid.
/// Ağ kartı değişse bile format atılana kadar aynı kalır.
/// </summary>
public static class HardwareIdProvider
{
    private const string HwPrefix = "hw:";

    /// <summary>32 haneli büyük harf hex formatında MachineGuid döndürür.</summary>
    public static string GetHardwareId()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var guid = key?.GetValue("MachineGuid") as string;
            if (!string.IsNullOrEmpty(guid))
                return NormalizeGuid(guid);
        }
        catch { /* kayıt defteri okunamazsa yedek üret */ }

        return Guid.NewGuid().ToString("N").ToUpperInvariant();
    }

    public static string FormatAddress(string hardwareId) => $"{HwPrefix}{NormalizeGuid(hardwareId)}";

    public static bool IsHardwareAddress(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        var trimmed = input.Trim();
        if (trimmed.StartsWith(HwPrefix, StringComparison.OrdinalIgnoreCase))
            return IsValidHardwareId(trimmed[HwPrefix.Length..]);
        return IsValidHardwareId(trimmed);
    }

    public static string ExtractHardwareId(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.StartsWith(HwPrefix, StringComparison.OrdinalIgnoreCase))
            return NormalizeGuid(trimmed[HwPrefix.Length..]);
        return NormalizeGuid(trimmed);
    }

    public static string FormatDisplay(string hardwareId)
    {
        var normalized = NormalizeGuid(hardwareId);
        if (normalized.Length != 32) return hardwareId;
        return $"{normalized[..8]}-{normalized[8..12]}-{normalized[12..16]}-{normalized[16..20]}-{normalized[20..]}";
    }

    private static string NormalizeGuid(string value) =>
        new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    private static bool IsValidHardwareId(string value)
    {
        var normalized = NormalizeGuid(value);
        return normalized.Length == 32 || normalized.Length == 12; // 12: eski MAC kayıtları
    }
}
