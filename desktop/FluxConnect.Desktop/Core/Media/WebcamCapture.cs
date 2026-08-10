using FluxConnect.Desktop.Core.Capture;
using OpenCvSharp;

namespace FluxConnect.Desktop.Core.Media;

/// <summary>
/// Webcam görüntüsünü yakalar ve JPEG olarak dışarı verir.
/// </summary>
public class WebcamCapture : IDisposable
{
    private VideoCapture? _capture;
    private CancellationTokenSource? _cts;
    private bool _isCapturing;
    private const int BaseFps = 8;

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
        using var mat = new Mat();

        try
        {
            while (!ct.IsCancellationRequested && _capture != null && _capture.IsOpened())
            {
                var webcamQuality = App.StreamQuality.Snapshot().WebcamQuality;
                var intervalMs = Math.Max(1000 / BaseFps, App.StreamQuality.GetMinFrameIntervalMs());

                if (_capture.Read(mat) && !mat.Empty())
                {
                    try
                    {
                        Cv2.ImEncode(".jpg", mat, out var buf,
                            new ImageEncodingParam(ImwriteFlags.JpegQuality, webcamQuality));
                        OnFrameChunk?.Invoke(Convert.ToBase64String(buf));
                    }
                    catch { }
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
