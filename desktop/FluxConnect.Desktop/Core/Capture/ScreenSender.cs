using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FluxConnect.Desktop.Core.Capture;

/// <summary>
/// Ekranı yakalar ve Base64 JPEG olarak Relay üzerinden gönderir.
/// Önce DXGI dener; başarısız olursa GDI (BitBlt) yedek motoruna geçer.
/// Ekran değiştirildiğinde güvenilirlik için her zaman GDI kullanılır.
/// </summary>
public class ScreenSender : IDisposable
{
    private readonly string _sessionId;
    private readonly string _targetId;

    private DesktopCapture? _dxgiCapture;
    private GdiCapture? _gdiCapture;

    private DateTime _lastFrameTime = DateTime.MinValue;
    private const int MaxFps = 10;
    private readonly int _minFrameIntervalMs = 1000 / MaxFps;

    // Ekran geçişi sırasında eski karelerin gönderilmesini engelleyen sayaç.
    // Her SwitchScreen çağrısında artırılır. Kare gönderilmeden önce
    // karenin ait olduğu epoch ile mevcut epoch karşılaştırılır.
    private volatile int _epoch;

    public ScreenSender(string sessionId, string targetId)
    {
        _sessionId = sessionId;
        _targetId = targetId;
    }

    public void Start()
    {
        _epoch = 0;

        _ = SendScreenInfoAsync();

        // Önce DXGI dene
        try
        {
            _dxgiCapture = new DesktopCapture();
            _dxgiCapture.OnFrameCaptured += OnDxgiFrame;
            _dxgiCapture.Initialize();
            _dxgiCapture.Start(MaxFps);
            File.AppendAllText("flux_debug.txt", $"[{DateTime.Now:HH:mm:ss}] [ScreenSender] DXGI motoru aktif ({_dxgiCapture.Width}x{_dxgiCapture.Height})\n");
        }
        catch (Exception dxgiEx)
        {
            File.AppendAllText("flux_debug.txt", $"[{DateTime.Now:HH:mm:ss}] [ScreenSender] DXGI başarısız, GDI'ya geçiliyor. Sebep: {dxgiEx.Message}\n");
            StartGdi(0);
        }
    }

    private void StartGdi(int screenIndex)
    {
        // Önce DXGI'yı tamamen temizle
        if (_dxgiCapture != null)
        {
            _dxgiCapture.OnFrameCaptured -= OnDxgiFrame; // Event bağlantısını kes
            _dxgiCapture.Stop();
            _dxgiCapture.Dispose();
            _dxgiCapture = null;
        }

        // Varsa eski GDI'yı temizle
        if (_gdiCapture != null)
        {
            _gdiCapture.OnFrameCaptured -= OnGdiFrame;
            _gdiCapture.Stop();
            _gdiCapture.Dispose();
            _gdiCapture = null;
        }

        // Yeni GDI başlat
        _gdiCapture = new GdiCapture();
        _gdiCapture.OnFrameCaptured += OnGdiFrame;
        _gdiCapture.Initialize(screenIndex);
        _gdiCapture.Start(MaxFps);
        File.AppendAllText("flux_debug.txt", $"[{DateTime.Now:HH:mm:ss}] [ScreenSender] GDI motoru aktif - Ekran {screenIndex} ({_gdiCapture.Width}x{_gdiCapture.Height})\n");
    }

    public void Stop()
    {
        Interlocked.Increment(ref _epoch); // Havadaki tüm kareleri geçersiz kıl

        if (_dxgiCapture != null)
        {
            _dxgiCapture.OnFrameCaptured -= OnDxgiFrame;
            _dxgiCapture.Stop();
        }
        if (_gdiCapture != null)
        {
            _gdiCapture.OnFrameCaptured -= OnGdiFrame;
            _gdiCapture.Stop();
        }
    }

    /// <summary>Hedef makinedeki ekran bilgilerini döndürür.</summary>
    public static (int Count, string[] Names) GetScreenInfo()
    {
        var names = GdiCapture.GetScreenNames();
        return (names.Length, names);
    }

    /// <summary>İzleyicinin isteği üzerine aktif ekranı değiştirir.</summary>
    public void SwitchScreen(int screenIndex)
    {
        File.AppendAllText("flux_debug.txt", $"[{DateTime.Now:HH:mm:ss}] [ScreenSender] SwitchScreen({screenIndex}) çağrıldı\n");

        // 1. Epoch'u artır → havadaki eski kareler artık gönderilmeyecek
        Interlocked.Increment(ref _epoch);

        // 2. GDI zaten aktifse sadece ekranı değiştir
        if (_gdiCapture != null)
        {
            _gdiCapture.SwitchScreen(screenIndex);
        }
        else
        {
            // DXGI aktifti → GDI'ya geç
            StartGdi(screenIndex);
        }

        File.AppendAllText("flux_debug.txt", $"[{DateTime.Now:HH:mm:ss}] [ScreenSender] Ekran {screenIndex} aktif (epoch={_epoch})\n");
    }

    private async Task SendScreenInfoAsync()
    {
        try
        {
            // Ekran listesini al ve base64 + json olarak INF: önekiyle gönder
            var (count, names) = GetScreenInfo();
            var payload = new { type = "screen_info", count = count, names = names };
            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
            await SendAsync("INF:" + base64);
        }
        catch (Exception ex)
        {
            File.AppendAllText("flux_debug.txt", $"[{DateTime.Now:HH:mm:ss}] [ScreenSender] Ekran listesi gönderme hatası: {ex.Message}\n");
        }
    }

    // ---- DXGI Yolu ----
    private void OnDxgiFrame(byte[] rawData, int width, int height, int rowPitch)
    {
        if (!ShouldSendFrame()) return;

        int frameEpoch = _epoch; // Bu karenin epoch'unu yakala

        Task.Run(async () =>
        {
            // Kare havadayken ekran değiştirildiyse gönderme
            if (frameEpoch != _epoch) return;

            try
            {
                string base64 = string.Empty;

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    if (frameEpoch != _epoch) return; // Bir kez daha kontrol et
                    var bmp = BitmapSource.Create(width, height, 96, 96,
                        PixelFormats.Bgra32, null, rawData, rowPitch);
                    base64 = EncodeToJpegBase64(bmp);
                });

                if (frameEpoch != _epoch) return;
                await SendAsync(base64);
            }
            catch (Exception ex)
            {
                File.AppendAllText("flux_debug.txt", $"[{DateTime.Now:HH:mm:ss}] [ScreenSender-DXGI] Hata: {ex.Message}\n");
            }
        });
    }

    // ---- GDI Yolu ----
    private void OnGdiFrame(byte[] jpegBytes, int width, int height)
    {
        if (!ShouldSendFrame()) return;

        int frameEpoch = _epoch;
        var base64 = Convert.ToBase64String(jpegBytes);

        Task.Run(async () =>
        {
            if (frameEpoch != _epoch) return; // Eski epoch'a ait kareyi gönderme
            await SendAsync(base64);
        });
    }

    private bool ShouldSendFrame()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastFrameTime).TotalMilliseconds < _minFrameIntervalMs)
            return false;
        _lastFrameTime = now;
        return true;
    }

    private static string EncodeToJpegBase64(BitmapSource bmp)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = 45 };
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return Convert.ToBase64String(ms.ToArray());
    }

    private async Task SendAsync(string base64)
    {
        if (string.IsNullOrEmpty(base64)) return;

        try
        {
            if (_sessionId.StartsWith("lan_"))
            {
                // LAN modu: LocalServer üzerinden gönder
                if (App.LanServer?.HasClient == true)
                    await App.LanServer.SendRelayDataAsync(base64);
            }
            else
            {
                // İnternet modu: Relay üzerinden gönder
                if (!App.Relay.IsConnected) return;
                await App.Relay.SendRelayDataAsync(_sessionId, _targetId, base64);
            }
        }
        catch (Exception ex)
        {
            File.AppendAllText("flux_debug.txt", $"[{DateTime.Now:HH:mm:ss}] [ScreenSender] Gönderme hatası: {ex.Message}\n");
        }
    }

    public void Dispose()
    {
        Stop();
        _dxgiCapture?.Dispose();
        _gdiCapture?.Dispose();
    }
}
