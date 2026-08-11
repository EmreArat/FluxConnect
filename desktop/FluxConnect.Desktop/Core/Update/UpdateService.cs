using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluxConnect.Desktop.Core.Update;

public sealed class UpdateInfo
{
    public string Version { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
    public string ReleaseNotes { get; init; } = string.Empty;
    public string? Sha256 { get; init; }
}

public static class UpdateService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { { "User-Agent", "FluxConnect-Updater" } }
    };

    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public static async Task<UpdateInfo?> CheckForUpdateAsync(string githubRepo, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(githubRepo))
            return null;

        var url = $"https://api.github.com/repos/{githubRepo.Trim()}/releases/latest";
        using var response = await Http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var release = await JsonSerializer.DeserializeAsync<GithubRelease>(stream, cancellationToken: ct);
        if (release == null)
            return null;

        var asset = release.Assets?.FirstOrDefault(a =>
            a.Name.Equals("FluxConnect.exe", StringComparison.OrdinalIgnoreCase))
            ?? release.Assets?.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

        if (asset == null || string.IsNullOrWhiteSpace(release.TagName))
            return null;

        var latest = release.TagName.TrimStart('v', 'V');
        if (!IsNewer(latest, CurrentVersion))
            return null;

        return new UpdateInfo
        {
            Version = latest,
            DownloadUrl = asset.BrowserDownloadUrl,
            ReleaseNotes = release.Body ?? string.Empty,
            Sha256 = null
        };
    }

    public static async Task<string> DownloadUpdateAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FluxConnect", "Updates");
        Directory.CreateDirectory(dir);

        var targetPath = Path.Combine(dir, $"FluxConnect-{info.Version}.exe");

        using var response = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1;
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(targetPath);

        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            readTotal += read;
            if (total > 0)
                progress?.Report(readTotal / (double)total);
        }

        if (!string.IsNullOrEmpty(info.Sha256))
        {
            var fileBytes = await File.ReadAllBytesAsync(targetPath, ct);
            var hash = Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant();
            if (!hash.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(targetPath);
                throw new InvalidOperationException("İndirilen dosyanın bütünlük doğrulaması başarısız.");
            }
        }

        return targetPath;
    }

    public static void ApplyUpdateAndRestart(string downloadedExePath)
    {
        var currentExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Geçerli uygulama yolu bulunamadı.");

        var batchPath = Path.Combine(
            Path.GetTempPath(),
            $"fluxconnect-update-{Guid.NewGuid():N}.cmd");

        var batch = $"""
            @echo off
            timeout /t 2 /nobreak > nul
            copy /Y "{downloadedExePath}" "{currentExe}"
            start "" "{currentExe}"
            del "%~f0"
            """;

        File.WriteAllText(batchPath, batch);
        Process.Start(new ProcessStartInfo
        {
            FileName = batchPath,
            CreateNoWindow = true,
            UseShellExecute = false
        });

        System.Windows.Application.Current.Shutdown();
    }

    private static bool IsNewer(string latest, string current)
    {
        if (!Version.TryParse(NormalizeVersion(latest), out var lv))
            return false;
        if (!Version.TryParse(NormalizeVersion(current), out var cv))
            return true;
        return lv > cv;
    }

    private static string NormalizeVersion(string version)
    {
        var parts = version.Split('.');
        return parts.Length switch
        {
            1 => $"{parts[0]}.0.0",
            2 => $"{parts[0]}.{parts[1]}.0",
            _ => version
        };
    }

    private sealed class GithubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("assets")]
        public List<GithubAsset>? Assets { get; set; }
    }

    private sealed class GithubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;
    }
}
