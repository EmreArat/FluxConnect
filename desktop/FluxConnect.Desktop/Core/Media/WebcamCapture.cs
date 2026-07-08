using OpenCvSharp;

namespace FluxConnect.Desktop.Core.Media;

/// <summary>
/// OpenCvSharp4 ile webcam görüntüsünü yakalar.
/// Yakalanan kare JPEG+Base64 olarak OnFrameChunk ile dışarıya verilir.
/// </summary>
public class WebcamCapture : IDisposable
{
    private VideoCapture? _capture;
    private CancellationTokenSource? _cts;
    private bool _isCapturing;
    private const int Fps = 8;

    /// <summary>JPEG Base64 kare verisi</summary>
    public event Action<string>? OnFrameChunk;

    public bool IsCapturing => _isCapturing;

    public void Start(int cameraIndex = 0)
    {
        if (_isCapturing) return;

        _capture = new VideoCapture(cameraIndex);
        if (!_capture.IsOpened())
        {
            _capture.Dispose();
            _capture = null;
            throw new InvalidOperationException("Webcam açılamadı.");
        }

        // Düşük çözünürlük — bant genişliği tasarrufu
        _capture.Set(VideoCaptureProperties.FrameWidth, 320);
        _capture.Set(VideoCaptureProperties.FrameHeight, 240);

        _cts = new CancellationTokenSource();
        _isCapturing = true;
        _ = CaptureLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        if (!_isCapturing) return;
        _cts?.Cancel();
        _isCapturing = false;
    }

    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        var intervalMs = 1000 / Fps;
        using var mat = new Mat();

        try
        {
            while (!ct.IsCancellationRequested && _capture != null && _capture.IsOpened())
            {
                if (_capture.Read(mat) && !mat.Empty())
                {
                    try
                    {
                        // JPEG olarak encode et (Daha fazla sıkıştırma, ağ optimizasyonu)
                        Cv2.ImEncode(".jpg", mat, out var buf,
                            new ImageEncodingParam(ImwriteFlags.JpegQuality, 35));

                        var base64 = Convert.ToBase64String(buf);
                        OnFrameChunk?.Invoke(base64);
                    }
                    catch { /* Sessizce geç */ }
                }

                await Task.Delay(intervalMs, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _capture?.Release();
            _isCapturing = false;
        }
    }

    public void Dispose()
    {
        Stop();
        _capture?.Release();
        _capture?.Dispose();
    }
}
