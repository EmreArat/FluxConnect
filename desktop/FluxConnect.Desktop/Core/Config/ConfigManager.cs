using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using FluxConnect.Desktop.Core.Hardware;

namespace FluxConnect.Desktop.Core.Config;

public class BrandConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "FluxConnect";

    [JsonPropertyName("primary_color")]
    public string PrimaryColor { get; set; } = "#1A6FD4";

    [JsonPropertyName("logo_path")]
    public string? LogoPath { get; set; }
}

public class SavedContact
{
    /// <summary>Relay için 9 haneli ID, LAN için hw:XXXXXXXXXXXX formatında donanım kimliği.</summary>
    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>LAN bağlantıları için sabit makine kimliği (Windows MachineGuid).</summary>
    [JsonPropertyName("hardware_id")]
    public string? HardwareId { get; set; }

    /// <summary>LAN hedefinin son bilinen IP adresi — bağlantı kurarken kullanılır.</summary>
    [JsonPropertyName("last_known_ip")]
    public string? LastKnownIp { get; set; }

    [JsonPropertyName("is_favorite")]
    public bool IsFavorite { get; set; }

    [JsonPropertyName("last_connected")]
    public DateTime? LastConnected { get; set; }

    public bool IsRelayContact =>
        Address.Length == 9 && Address.All(char.IsDigit);

    public bool IsLanContact =>
        !string.IsNullOrEmpty(HardwareId) ||
        Address.StartsWith("hw:", StringComparison.OrdinalIgnoreCase);
}

public class AppConfig
{
    [JsonPropertyName("machine_id")]
    public string MachineId { get; set; } = string.Empty;

    /// <summary>Bu bilgisayarın sabit kimliği (Windows MachineGuid).</summary>
    [JsonPropertyName("hardware_id")]
    public string HardwareId { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = Environment.MachineName;

    /// <summary>
    /// Bağlantı şifresi — SHA-256 hash olarak saklanır. Boşsa şifre gerekmez.
    /// </summary>
    [JsonPropertyName("session_password_hash")]
    public string? SessionPasswordHash { get; set; }

    [JsonPropertyName("auto_accept")]
    public bool AutoAccept { get; set; } = false;

    [JsonPropertyName("recording_path")]
    public string RecordingPath { get; set; } = Path.Combine(
        AppContext.BaseDirectory, "Recordings");

    [JsonPropertyName("relay_url")]
    public string RelayUrl { get; set; } = "ws://localhost:8765";

    [JsonPropertyName("brand")]
    public BrandConfig Brand { get; set; } = new();

    [JsonPropertyName("contacts")]
    public List<SavedContact> Contacts { get; set; } = new();
}

public static class ConfigManager
{
    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluxConnect");

    private static readonly string ConfigPath =
        Path.Combine(ConfigDir, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static AppConfig Load()
    {
        if (!File.Exists(ConfigPath))
        {
            var fresh = CreateFreshConfig();
            Save(fresh);
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions)
                          ?? CreateAndSaveDefault();
            EnsureHardwareId(config);
            MigrateLanContacts(config);
            return config;
        }
        catch
        {
            return CreateAndSaveDefault();
        }
    }

    public static void Save(AppConfig config)
    {
        if (!Directory.Exists(ConfigDir))
        {
            Directory.CreateDirectory(ConfigDir);
        }
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }

    private static AppConfig CreateAndSaveDefault()
    {
        var config = CreateFreshConfig();
        Save(config);
        return config;
    }

    private static AppConfig CreateFreshConfig() => new()
    {
        MachineId = GenerateMachineId(),
        HardwareId = HardwareIdProvider.GetHardwareId(),
    };

    private static void EnsureHardwareId(AppConfig config)
    {
        var current = HardwareIdProvider.GetHardwareId();
        // Boş veya eski MAC tabanlı (12 hane) kimlik varsa MachineGuid ile güncelle
        if (string.IsNullOrEmpty(config.HardwareId) || config.HardwareId.Length == 12)
        {
            config.HardwareId = current;
            Save(config);
        }
    }

    /// <summary>Eski IP tabanlı LAN kayıtlarını donanım kimliği formatına taşır (mümkünse).</summary>
    private static void MigrateLanContacts(AppConfig config)
    {
        var changed = false;
        foreach (var contact in config.Contacts)
        {
            if (contact.IsRelayContact) continue;
            if (!string.IsNullOrEmpty(contact.HardwareId)) continue;

            // Eski kayıt: address alanında IP var
            if (System.Net.IPAddress.TryParse(contact.Address, out _))
            {
                contact.LastKnownIp ??= contact.Address;
                changed = true;
            }
        }

        if (changed) Save(config);
    }

    /// <summary>
    /// Kriptografik olarak güvenli 9 haneli ID üretir.
    /// </summary>
    private static string GenerateMachineId()
    {
        using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        var bytes = new byte[4];
        rng.GetBytes(bytes);
        var value = Math.Abs(BitConverter.ToInt32(bytes, 0)) % 900_000_000 + 100_000_000;
        return value.ToString();
    }
}
