using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FluxConnect.Desktop.Core.Capture;

/// <summary>
/// Ekranı yakalar ve binary relay frame olarak gönderir.
/// </summary>
public class ScreenSender : IDisposable
{
    private readonly string _sessionId;
    private readonly string _targetId;
    private readonly StreamQualityController _quality;

    private DesktopCapture? _dxgiCapture;
    private GdiCapture? _gdiCapture;

    private readonly object _frameLock = new();
    private readonly object _fpsLock = new();
    private DateTime _lastFrameTime = DateTime.MinValue;

    private byte[]? _pendingDxgiRaw;
    private int _pendingDxgiW;
    private int _pendingDxgiH;
    private int _pendingDxgiPitch;
    private int _pendingDxgiEpoch;
    private byte[]? _pendingGdiJpeg;
    private int _pendingGdiEpoch;
    private int _encodeWorkerRunning;

    private volatile int _epoch;

    public ScreenSender(string sessionId, string targetId, StreamQualityController? quality = null)
    {
        _sessionId = sessionId;
        _targetId = targetId;
        _quality = quality ?? App.StreamQuality;
    }

    public void Start()
    {
        _epoch = 0;
        _ = SendScreenInfoAsync();

        var (_, _, _) = _quality.Snapshot();
        var initialFps = Math.Max(StreamQualityController.MinFps, _quality.Snapshot().MaxFps);

        try
        {
            _dxgiCapture = new DesktopCapture();
            _dxgiCapture.OnFrameCaptured += OnDxgiFrame;
            _dxgiCapture.Initialize();
            _dxgiCapture.Start(initialFps);
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
        if (_dxgiCapture != null)
        {
            _dxgiCapture.OnFrameCaptured -= OnDxgiFrame;
            _dxgiCapture.Stop();
            _dxgiCapture.Dispose();
            _dxgiCapture = null;
        }

        if (_gdiCapture != null)
        {
            _gdiCapture.OnFrameCaptured -= OnGdiFrame;
            _gdiCapture.Stop();
            _gdiCapture.Dispose();
            _gdiCapture = null;
        }

        var initialFps = _quality.Snapshot().MaxFps;
        _gdiCapture = new GdiCapture();
        _gdiCapture.OnFrameCaptured += OnGdiFrame;
        _gdiCapture.Initialize(screenIndex);
        _gdiCapture.Start(initialFps);
        File.AppendAllText("flux_debug.txt", $"[{DateTime.Now:HH:mm:ss}] [ScreenSender] GDI motoru aktif - Ekran {screenIndex} ({_gdiCapture.Width}x{_gdiCapture.Height})\n");
    }

    public void Stop()
    {
        Interlocked.Increment(ref _epoch);

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

        lock (_frameLock)
        {
            _pendingDxgiRaw = null;
            _pendingGdiJpeg = null;
        }
    }

    public static (int Count, string[] Names) GetScreenInfo()
    {
        var names = GdiCapture.GetScreenNames();
        return (names.Length, names);
    }

    public void SwitchScreen(int screenIndex)
    {
        Interlocked.Increment(ref _epoch);

        if (_gdiCapture != null)
            _gdiCapture.SwitchScreen(screenIndex);
        else
            StartGdi(screenIndex);
    }

    private async Task SendScreenInfoAsync()
    {
        try
        {
            var (count, names) = GetScreenInfo();
            var payload = new { type = "screen_info", count, names };
            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
            await SendLegacyAsync("INF:" + base64);
        }
        catch (Exception ex)
        {
            File.AppendAllText("flux_debug.txt", $"[{DateTime.Now:HH:mm:ss}] [ScreenSender] Ekran listesi gönderme hatası: {ex.Message}\n");
        }
    }

    private void OnDxgiFrame(byte[] rawData, int width, int height, int rowPitch)
    {
        if (!ShouldSendFrame()) return;

        int frameEpoch = _epoch;
        lock (_frameLock)
        {
            int size = rowPitch * height;
            if (_pendingDxgiRaw == null || _pendingDxgiRaw.Length != size)
                _pendingDxgiRaw = new byte[size];
            Buffer.BlockCopy(rawData, 0, _pendingDxgiRaw, 0, size);
            _pendingDxgiW = width;
            _pendingDxgiH = height;
            _pendingDxgiPitch = rowPitch;
            _pendingDxgiEpoch = frameEpoch;
            _pendingGdiJpeg = null;
        }

        StartEncodeWorkerIfNeeded();
    }

    private void OnGdiFrame(byte[] jpegBytes, int width, int height)
    {
        if (!ShouldSendFrame()) return;

        lock (_frameLock)
        {
            _pendingGdiJpeg = jpegBytes;
            _pendingGdiEpoch = _epoch;
            _pendingDxgiRaw = null;
        }

        StartEncodeWorkerIfNeeded();
    }

    private void StartEncodeWorkerIfNeeded()
    {
        if (Interlocked.CompareExchange(ref _encodeWorkerRunning, 1, 0) != 0)
            return;

        _ = Task.Run(EncodeWorkerLoopAsync);
    }

    private async Task EncodeWorkerLoopAsync()
    {
        try
        {
            while (true)
            {
                byte[]? raw = null;
                byte[]? gdiJpeg = null;
                int w = 0, h = 0, pitch = 0, frameEpoch = 0;

                lock (_frameLock)
                {
                    if (_pendingGdiJpeg != null)
                    {
                        gdiJpeg = _pendingGdiJpeg;
                        frameEpoch = _pendingGdiEpoch;
                        _pendingGdiJpeg = null;
                    }
                    else if (_pendingDxgiRaw != null)
                    {
                        raw = _pendingDxgiRaw;
                        _pendingDxgiRaw = null;
                        w = _pendingDxgiW;
                        h = _pendingDxgiH;
                        pitch = _pendingDxgiPitch;
                        frameEpoch = _pendingDxgiEpoch;
                    }
                    else
                    {
                        break;
                    }
                }

                if (gdiJpeg != null)
                {
                    if (frameEpoch == _epoch)
                        await SendScreenFrameAsync(gdiJpeg);
                    continue;
                }

                if (raw == null || frameEpoch != _epoch)
                    continue;

                var quality = _quality.Snapshot().JpegQuality;
                byte[] jpeg;
                try
                {
                    jpeg = JpegEncoder.EncodeBgra32(raw, w, h, pitch, quality);
                }
                catch (Exception ex)
                {
                    File.AppendAllText("flux_debug.txt", $"[{DateTime.Now:HH:mm:ss}] [ScreenSender] JPEG encode hatası: {ex.Message}\n");
                    continue;
                }

                if (frameEpoch == _epoch)
                    await SendScreenFrameAsync(jpeg);
            }
        }
        catch (Exception ex)
        {
            File.AppendAllText("flux_debug.txt", $"[{DateTime.Now:HH:mm:ss}] [ScreenSender] Encode worker hatası: {ex.Message}\n");
        }
        finally
        {
            Interlocked.Exchange(ref _encodeWorkerRunning, 0);
            lock (_frameLock)
            {
                if (_pendingDxgiRaw != null || _pendingGdiJpeg != null)
                    StartEncodeWorkerIfNeeded();
            }
        }
    }

    private bool ShouldSendFrame()
    {
        lock (_fpsLock)
        {
            var minInterval = _quality.GetMinFrameIntervalMs();
            var now = DateTime.UtcNow;
            if ((now - _lastFrameTime).TotalMilliseconds < minInterval)
                return false;
            _lastFrameTime = now;
            return true;
        }
    }

    private async Task SendScreenFrameAsync(byte[] jpegBytes)
    {
        if (jpegBytes.Length == 0) return;

        var sw = Stopwatch.StartNew();
        try
        {
            if (_sessionId.StartsWith("lan_"))
            {
                if (App.LanServer?.HasClient == true)
                    await App.LanServer.SendRelayFrameAsync(Network.RelayFrameType.Screen, jpegBytes);
            }
            else
            {
                if (!App.Relay.IsConnected) return;
                await App.Relay.SendRelayFrameAsync(_sessionId, _targetId, Network.RelayFrameType.Screen, jpegBytes);
            }
        }
        catch (Exception ex)
        {
            File.AppendAllText("flux_debug.txt", $"[{DateTime.Now:HH:mm:ss}] [ScreenSender] Gönderme hatası: {ex.Message}\n");
        }
        finally
        {
            sw.Stop();
            _quality.ReportSend(jpegBytes.Length, sw.Elapsed.TotalMilliseconds);
        }
    }

    private async Task SendLegacyAsync(string legacyData)
    {
        try
        {
            if (_sessionId.StartsWith("lan_"))
            {
                if (App.LanServer?.HasClient == true)
                    await App.LanServer.SendRelayDataAsync(legacyData);
            }
            else
            {
                if (!App.Relay.IsConnected) return;
                await App.Relay.SendRelayDataAsync(_sessionId, _targetId, legacyData);
            }
        }
        catch (Exception ex)
        {
            File.AppendAllText("flux_debug.txt", $"[{DateTime.Now:HH:mm:ss}] [ScreenSender] Legacy gönderme hatası: {ex.Message}\n");
        }
    }

    public void Dispose()
    {
        Stop();
        _dxgiCapture?.Dispose();
        _gdiCapture?.Dispose();
    }
}
