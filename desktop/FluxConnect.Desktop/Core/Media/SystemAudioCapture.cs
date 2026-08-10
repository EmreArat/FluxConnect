using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.IO;
using System.IO.Compression;

namespace FluxConnect.Desktop.Core.Media;

/// <summary>
/// WASAPI Loopback ile sistem sesini yakalar.
/// </summary>
public class SystemAudioCapture : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private WaveFormat? _captureFormat;
    private bool _isCapturing;
    private BufferedWaveProvider? _inputBuffer;
    private MediaFoundationResampler? _resampler;
    private readonly byte[] _resampleOut = new byte[8192];

    public event Action<string>? OnAudioChunk;
    public bool IsCapturing => _isCapturing;

    public void Start()
    {
        if (_isCapturing) return;

        _capture = new WasapiLoopbackCapture();
        _captureFormat = _capture.WaveFormat;

        if (_captureFormat != null &&
            (_captureFormat.SampleRate != 16000 || _captureFormat.Channels != 1))
        {
            _inputBuffer = new BufferedWaveProvider(_captureFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true
            };
            var targetFormat = new WaveFormat(16000, 16, 1);
            _resampler = new MediaFoundationResampler(_inputBuffer, targetFormat)
            {
                ResamplerQuality = 30
            };
        }

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

            if (_resampler != null && _inputBuffer != null)
            {
                _inputBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                using var outMs = new MemoryStream();
                int bytesRead;
                while ((bytesRead = _resampler.Read(_resampleOut, 0, _resampleOut.Length)) > 0)
                    outMs.Write(_resampleOut, 0, bytesRead);
                dataToSend = outMs.ToArray();
                if (dataToSend.Length == 0) return;
            }
            else
            {
                dataToSend = new byte[e.BytesRecorded];
                Array.Copy(e.Buffer, dataToSend, e.BytesRecorded);
            }

            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Fastest))
                gz.Write(dataToSend, 0, dataToSend.Length);

            var base64 = Convert.ToBase64String(ms.ToArray());
            OnAudioChunk?.Invoke(base64);
        }
        catch { }
    }

    public void Dispose()
    {
        Stop();
        _resampler?.Dispose();
        _capture?.Dispose();
    }
}
