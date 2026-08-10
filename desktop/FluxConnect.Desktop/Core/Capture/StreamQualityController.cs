namespace FluxConnect.Desktop.Core.Capture;

/// <summary>
/// Ağ/encode geri bildirimine göre ekran FPS ve JPEG kalitesini ayarlar.
/// </summary>
public sealed class StreamQualityController
{
    private int _jpegQuality = 45;
    private int _maxFps = 10;
    private int _slowStreak;
    private int _fastStreak;
    private readonly object _lock = new();

    public const int MinFps = 4;
    public const int MaxFpsCap = 12;
    public const int MinQuality = 28;
    public const int MaxQuality = 55;

    public (int MaxFps, int JpegQuality, int WebcamQuality) Snapshot()
    {
        lock (_lock)
            return (_maxFps, _jpegQuality, MapWebcamQuality(_jpegQuality));
    }

    public void ReportSend(int payloadBytes, double sendDurationMs)
    {
        lock (_lock)
        {
            var slow = sendDurationMs > 180 || payloadBytes > 350_000;
            var fast = sendDurationMs < 80 && payloadBytes < 180_000;

            if (slow)
            {
                _slowStreak++;
                _fastStreak = 0;
            }
            else if (fast)
            {
                _fastStreak++;
                _slowStreak = 0;
            }
            else
            {
                _slowStreak = Math.Max(0, _slowStreak - 1);
                _fastStreak = Math.Max(0, _fastStreak - 1);
            }

            if (_slowStreak >= 3)
            {
                _slowStreak = 0;
                _maxFps = Math.Max(MinFps, _maxFps - 1);
                _jpegQuality = Math.Max(MinQuality, _jpegQuality - 4);
            }
            else if (_fastStreak >= 6)
            {
                _fastStreak = 0;
                _maxFps = Math.Min(MaxFpsCap, _maxFps + 1);
                _jpegQuality = Math.Min(MaxQuality, _jpegQuality + 2);
            }
        }
    }

    public int GetMinFrameIntervalMs()
    {
        lock (_lock)
            return Math.Max(1000 / _maxFps, 1000 / MaxFpsCap);
    }

    private static int MapWebcamQuality(int screenQuality)
        => Math.Clamp(screenQuality - 8, 25, 45);
}
