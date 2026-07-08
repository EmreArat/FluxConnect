using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;

namespace FluxConnect.Desktop.Core.Native;

/// <summary>
/// OpenCV native DLL'lerini ilk webcam kullanımında indirir ve
/// %LocalAppData%\FluxConnect\native\ konumunda saklar.
/// </summary>
public static class OpenCvNativeManager
{
    private const string RuntimeVersion = "4.13.0.20260226";
    private const string NuGetPackageUrl =
        $"https://www.nuget.org/api/v2/package/OpenCvSharp4.runtime.win/{RuntimeVersion}";

    private static readonly string NativeDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FluxConnect", "native");

    private static readonly string[] RequiredDlls =
    [
        "OpenCvSharpExtern.dll",
        "opencv_videoio_ffmpeg4130_64.dll"
    ];

    private static bool _searchPathRegistered;

    public static string NativeDirectory => NativeDir;

    public static bool IsInstalled =>
        RequiredDlls.All(dll => File.Exists(Path.Combine(NativeDir, dll)));

    /// <summary>Kullanıcıdan indirme onayı ister.</summary>
    public static bool RequestUserConsent(Window? owner)
    {
        var answer = MessageBox.Show(
            owner,
            "Webcam özelliği için OpenCV bileşenleri gerekli (~90 MB).\n\n" +
            $"İndirildikten sonra şurada saklanır:\n{NativeDir}\n\n" +
            "Şimdi indirilsin mi?",
            "FluxConnect — Bileşen Gerekli",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        return answer == MessageBoxResult.Yes;
    }

    /// <summary>Onay sonrası indirir ve kurar.</summary>
    public static async Task<bool> DownloadAndInstallAsync(CancellationToken cancellationToken = default)
    {
        await DownloadAndExtractAsync(cancellationToken);
        RegisterSearchPath();
        return true;
    }

    public static void RegisterSearchPath()
    {
        if (_searchPathRegistered) return;

        Directory.CreateDirectory(NativeDir);

        const int LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;
        const int LOAD_LIBRARY_SEARCH_USER_DIRS = 0x00000400;
        SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | LOAD_LIBRARY_SEARCH_USER_DIRS);
        AddDllDirectory(NativeDir);

        _searchPathRegistered = true;
        Log($"Native arama yolu kaydedildi: {NativeDir}");
    }

    private static async Task DownloadAndExtractAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(NativeDir);
        var tempZip = Path.Combine(Path.GetTempPath(), $"flux-opencv-{RuntimeVersion}.zip");

        try
        {
            Log($"NuGet paketi indiriliyor: {NuGetPackageUrl}");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            await using (var stream = await http.GetStreamAsync(NuGetPackageUrl, cancellationToken))
            await using (var file = File.Create(tempZip))
            {
                await stream.CopyToAsync(file, cancellationToken);
            }

            Log("Paket açılıyor...");
            using var zip = ZipFile.OpenRead(tempZip);
            foreach (var dll in RequiredDlls)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entryPath = $"runtimes/win-x64/native/{dll}";
                var entry = zip.GetEntry(entryPath)
                    ?? throw new FileNotFoundException($"Pakette bulunamadı: {entryPath}");

                var targetPath = Path.Combine(NativeDir, dll);
                entry.ExtractToFile(targetPath, overwrite: true);
                Log($"Çıkarıldı: {dll}");
            }
        }
        finally
        {
            if (File.Exists(tempZip))
                File.Delete(tempZip);
        }
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText("flux_debug.txt",
                $"[{DateTime.Now:HH:mm:ss}] [OpenCvNative] {message}\n");
        }
        catch { /* log yazılamazsa yoksay */ }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string newDirectory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(int directoryFlags);
}
