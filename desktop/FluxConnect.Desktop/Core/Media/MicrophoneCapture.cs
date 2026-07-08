using NAudio.Wave;
using System.IO;
using System.IO.Compression;

namespace FluxConnect.Desktop.Core.Media;

/// <summary>
/// WASAPI ile mikrofon sesini yakalar.
/// Yakalanan ses parçaları OnAudioChunk olayı ile dışarıya verilir.
/// </summary>
public class MicrophoneCapture : IDisposable
{
    private WaveInEvent? _waveIn;
    private bool _isCapturing;

    /// <summary>GZip + Base64 sıkıştırılmış ses verisi</summary>
    public event Action<string>? OnAudioChunk;

    public bool IsCapturing => _isCapturing;

    public void Start()
    {
        if (_isCapturing) return;

        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 16, 1), // 16 kHz, 16-bit, Mono
            BufferMilliseconds = 100 // 100ms'lik parçalar
        };

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += (_, _) => _isCapturing = false;

        _waveIn.StartRecording();
        _isCapturing = true;
    }

    public void Stop()
    {
        if (!_isCapturing) return;
        _waveIn?.StopRecording();
        _isCapturing = false;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        try
        {
            // GZip ile sıkıştır
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Fastest))
            {
                gz.Write(e.Buffer, 0, e.BytesRecorded);
            }

            var base64 = Convert.ToBase64String(ms.ToArray());
            OnAudioChunk?.Invoke(base64);
        }
        catch { /* Sessizce geç */ }
    }

    public void Dispose()
    {
        Stop();
        _waveIn?.Dispose();
    }
}
