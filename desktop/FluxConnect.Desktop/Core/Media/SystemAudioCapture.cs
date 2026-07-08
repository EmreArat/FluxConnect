using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.IO;
using System.IO.Compression;

namespace FluxConnect.Desktop.Core.Media;

/// <summary>
/// WASAPI Loopback ile sistem sesini yakalar (bilgisayardan çıkan her ses).
/// Yakalanan ses parçaları OnAudioChunk olayı ile dışarıya verilir.
/// </summary>
public class SystemAudioCapture : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private WaveFormat? _captureFormat;
    private bool _isCapturing;

    /// <summary>GZip + Base64 sıkıştırılmış ses verisi</summary>
    public event Action<string>? OnAudioChunk;

    public bool IsCapturing => _isCapturing;

    public void Start()
    {
        if (_isCapturing) return;

        _capture = new WasapiLoopbackCapture();
        _captureFormat = _capture.WaveFormat;

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += (_, _) => _isCapturing = false;

        _capture.StartRecording();
        _isCapturing = true;
    }

    public void Stop()
    {
        if (!_isCapturing) return;
        _capture?.StopRecording();
        _isCapturing = false;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        try
        {
            byte[] dataToSend;

            // Sistem sesi genellikle 32-bit float, 48kHz, stereo.
            // Bant genişliğinden tasarruf için 16kHz mono 16-bit'e dönüştür.
            if (_captureFormat != null &&
                (_captureFormat.SampleRate != 16000 || _captureFormat.Channels != 1))
            {
                dataToSend = Resample(e.Buffer, e.BytesRecorded, _captureFormat);
            }
            else
            {
                dataToSend = new byte[e.BytesRecorded];
                Array.Copy(e.Buffer, dataToSend, e.BytesRecorded);
            }

            // GZip ile sıkıştır
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Fastest))
            {
                gz.Write(dataToSend, 0, dataToSend.Length);
            }

            var base64 = Convert.ToBase64String(ms.ToArray());
            OnAudioChunk?.Invoke(base64);
        }
        catch { /* Sessizce geç */ }
    }

    /// <summary>
    /// Ses verisini 16kHz, 16-bit, mono formatına dönüştürür.
    /// </summary>
    private static byte[] Resample(byte[] buffer, int count, WaveFormat sourceFormat)
    {
        // Kaynak format float ise, önce 16-bit PCM'e dönüştür
        using var sourceStream = new RawSourceWaveStream(
            new MemoryStream(buffer, 0, count), sourceFormat);

        var targetFormat = new WaveFormat(16000, 16, 1);

        using var resampler = new MediaFoundationResampler(sourceStream, targetFormat);
        resampler.ResamplerQuality = 30; // Düşük kalite = daha hızlı

        using var outMs = new MemoryStream();
        var outBuffer = new byte[4096];
        int bytesRead;

        while ((bytesRead = resampler.Read(outBuffer, 0, outBuffer.Length)) > 0)
        {
            outMs.Write(outBuffer, 0, bytesRead);
        }

        return outMs.ToArray();
    }

    public void Dispose()
    {
        Stop();
        _capture?.Dispose();
    }
}
