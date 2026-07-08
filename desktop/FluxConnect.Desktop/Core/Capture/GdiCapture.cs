using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FluxConnect.Desktop.Core.Capture;

/// <summary>
/// Windows GDI BitBlt kullanarak ekranı yakalar.
/// DXGI kullanılamadığında (sanal makine, RDP, çift GPU sorunları) devreye girer.
/// </summary>
public class GdiCapture : IDisposable
{
    private CancellationTokenSource? _captureCts;
    private bool _disposed;
    
    // Ekran sınırları — SwitchScreen ile thread-safe güncellenir
    private System.Drawing.Rectangle _screenBounds;
    private volatile int _currentScreenIndex;
    private readonly object _boundsLock = new();

    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsCapturing => _captureCts != null && !_captureCts.IsCancellationRequested;

    /// <summary>Yakalanan her kare: (jpegBytes, width, height)</summary>
    public event Action<byte[], int, int>? OnFrameCaptured;

    public void Initialize(int screenIndex = 0)
    {
        _currentScreenIndex = screenIndex;
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (screenIndex >= screens.Length) screenIndex = 0;
        var screen = screens[screenIndex];
        _screenBounds = screen.Bounds;
        Width = _screenBounds.Width;
        Height = _screenBounds.Height;
    }

    /// <summary>
    /// Çalışırken veya dururken ekranı değiştirir.
    /// </summary>
    public void SwitchScreen(int screenIndex)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (screenIndex < 0 || screenIndex >= screens.Length) screenIndex = 0;
        _currentScreenIndex = screenIndex;
        var screen = screens[screenIndex];
        lock (_boundsLock)
        {
            _screenBounds = screen.Bounds;
        }
        Width = screen.Bounds.Width;
        Height = screen.Bounds.Height;
    }

    /// <summary>Bağlı monitör sayısını döndürür.</summary>
    public static int GetScreenCount() => System.Windows.Forms.Screen.AllScreens.Length;

    /// <summary>Ekran isimlerini döndürür ("Ekran 1 (1920x1080) [Ana]" ...)</summary>
    public static string[] GetScreenNames()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var names = new string[screens.Length];
        for (int i = 0; i < screens.Length; i++)
        {
            var b = screens[i].Bounds;
            names[i] = $"Ekran {i + 1}  ({b.Width}x{b.Height}){(screens[i].Primary ? " [Ana]" : "")}";
        }
        return names;
    }

    public void Start(int targetFps = 15)
    {
        if (IsCapturing) return;
        _captureCts = new CancellationTokenSource();
        int intervalMs = 1000 / targetFps;

        Task.Run(async () =>
        {
            var ct = _captureCts.Token;
            while (!ct.IsCancellationRequested)
            {
                var start = DateTime.UtcNow;
                try
                {
                    CaptureFrame();
                }
                catch (Exception ex)
                {
                    File.AppendAllText("flux_debug.txt", $"[{DateTime.Now:HH:mm:ss}] [GdiCapture] Hata: {ex.Message}\n");
                }

                var elapsed = (int)(DateTime.UtcNow - start).TotalMilliseconds;
                var wait = intervalMs - elapsed;
                if (wait > 0)
                    await Task.Delay(wait, ct).ConfigureAwait(false);
            }
        }, _captureCts.Token);
    }

    public void Stop()
    {
        _captureCts?.Cancel();
        _captureCts?.Dispose();
        _captureCts = null;
    }

    private void CaptureFrame()
    {
        // _screenBounds'u lock ile al
        System.Drawing.Rectangle bounds;
        lock (_boundsLock) { bounds = _screenBounds; }
        if (bounds.Width == 0 || bounds.Height == 0) return;

        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        }

        using var ms = new MemoryStream();
        var jpegParams = new EncoderParameters(1);
        jpegParams.Param[0] = new EncoderParameter(Encoder.Quality, 60L);
        var jpegCodec = GetJpegCodec();
        if (jpegCodec == null) return;

        bitmap.Save(ms, jpegCodec, jpegParams);
        var jpegBytes = ms.ToArray();
        OnFrameCaptured?.Invoke(jpegBytes, bounds.Width, bounds.Height);
    }

    private static ImageCodecInfo? GetJpegCodec()
    {
        foreach (var codec in ImageCodecInfo.GetImageEncoders())
            if (codec.MimeType == "image/jpeg") return codec;
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
