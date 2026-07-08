using NAudio.Wave;
using System.IO;
using System.IO.Compression;

namespace FluxConnect.Desktop.Core.Media;

/// <summary>
/// Gelen ses parçalarını hoparlörde çalar.
/// GZip + Base64 kodlanmış 16kHz/16-bit/Mono PCM verisi alır.
/// </summary>
public class AudioPlayer : IDisposable
{
    private BufferedWaveProvider? _buffer;
    private WaveOutEvent? _waveOut;
    private bool _isPlaying;

    public bool IsPlaying => _isPlaying;

    public void Start()
    {
        if (_isPlaying) return;

        var format = new WaveFormat(16000, 16, 1);
        _buffer = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromSeconds(5),
            DiscardOnBufferOverflow = true // Gecikme yerine eski veriyi at
        };

        _waveOut = new WaveOutEvent()
        {
            DesiredLatency = 150 // Düşük gecikme, iç bufferı minimumda tutar
        };
        _waveOut.Init(_buffer);
        _waveOut.Play();
        _isPlaying = true;
    }

    public void Stop()
    {
        if (!_isPlaying) return;
        _waveOut?.Stop();
        _isPlaying = false;
    }

    /// <summary>
    /// Gelen GZip+Base64 ses verisini çözer ve buffer'a ekler.
    /// </summary>
    public void Feed(string base64GzipData)
    {
        if (!_isPlaying || _buffer == null) return;

        try
        {
            var gzipBytes = Convert.FromBase64String(base64GzipData);
            using var gzMs = new MemoryStream(gzipBytes);
            using var gz = new GZipStream(gzMs, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            gz.CopyTo(outMs);

            var pcm = outMs.ToArray();

            // Eğer çok uzun bir sıra oluşmuşsa gecikmeyi önlemek için kuyruğu temizle (300ms üzeri birikme)
            if (_buffer.BufferedDuration.TotalMilliseconds > 300)
            {
                _buffer.ClearBuffer();
            }

            _buffer.AddSamples(pcm, 0, pcm.Length);
        }
        catch { /* Bozuk veri gelirse sessizce geç */ }
    }

    public void Dispose()
    {
        Stop();
        _waveOut?.Dispose();
    }
}
