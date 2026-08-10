using FluxConnect.Desktop.Core.Native;

namespace FluxConnect.Desktop.Core.Media;

/// <summary>
/// Tüm medya kaynaklarını (mic, sistem sesi, webcam) yönetir.
/// Giden verileri relay/LAN üzerinden gönderir, gelen verileri oynatır.
/// </summary>
public class MediaManager : IDisposable
{
    private MicrophoneCapture? _mic;
    private SystemAudioCapture? _sysAudio;
    private WebcamCapture? _webcam;

    private AudioPlayer? _micPlayer;
    private AudioPlayer? _sysAudioPlayer;
    private readonly object _audioPlayerLock = new();

    /// <summary>Relay/LAN'a gönderilecek veri (prefix, base64data)</summary>
    public event Action<string, string>? OnMediaData;

    /// <summary>Karşıdan gelen webcam karesi (base64 JPEG)</summary>
    public event Action<string>? OnRemoteWebcamFrame;

    // ---- Durum Bilgisi ----
    public bool IsMicActive => _mic?.IsCapturing ?? false;
    public bool IsSysAudioActive => _sysAudio?.IsCapturing ?? false;
    public bool IsWebcamActive => _webcam?.IsCapturing ?? false;

    // ================================================================
    // Giden Akışlar (Bu taraftan karşıya)
    // ================================================================

    public void ToggleMicrophone()
    {
        if (_mic?.IsCapturing == true)
        {
            _mic.Stop();
            _mic.Dispose();
            _mic = null;
        }
        else
        {
            _mic = new MicrophoneCapture();
            _mic.OnAudioChunk += chunk => OnMediaData?.Invoke("MIC:", chunk);
            _mic.Start();
        }
    }

    public void ToggleSystemAudio()
    {
        if (_sysAudio?.IsCapturing == true)
        {
            _sysAudio.Stop();
            _sysAudio.Dispose();
            _sysAudio = null;
        }
        else
        {
            _sysAudio = new SystemAudioCapture();
            _sysAudio.OnAudioChunk += chunk => OnMediaData?.Invoke("SYS:", chunk);
            _sysAudio.Start();
        }
    }

    public void ToggleWebcam()
    {
        if (_webcam?.IsCapturing == true)
        {
            _webcam.Stop();
            _webcam.Dispose();
            _webcam = null;
        }
        else
        {
            OpenCvNativeManager.RegisterSearchPath();
            _webcam = new WebcamCapture();
            _webcam.OnFrameChunk += chunk => OnMediaData?.Invoke("CAM:", chunk);
            _webcam.Start();
        }
    }

    public void StopWebcam()
    {
        if (_webcam?.IsCapturing == true)
        {
            _webcam.Stop();
            _webcam.Dispose();
            _webcam = null;
        }
    }

    // ================================================================
    // Gelen Akışlar (Karşıdan bu tarafa)
    // ================================================================

    /// <summary>
    /// Relay/LAN'dan gelen medya verisini işler.
    /// data parametresi prefix'i içermelidir (MIC:, SYS:, CAM:)
    /// </summary>
    public void HandleIncomingMedia(string prefixedData)
    {
        if (prefixedData.StartsWith("MIC:") || prefixedData.StartsWith("SYS:"))
        {
            _ = Task.Run(() => ProcessIncomingAudio(prefixedData));
            return;
        }

        if (prefixedData.StartsWith("CAM:"))
            OnRemoteWebcamFrame?.Invoke(prefixedData[4..]);
    }

    private void ProcessIncomingAudio(string prefixedData)
    {
        if (prefixedData.StartsWith("MIC:"))
        {
            lock (_audioPlayerLock)
            {
                _micPlayer ??= CreateAndStartPlayer();
                _micPlayer.Feed(prefixedData[4..]);
            }
        }
        else if (prefixedData.StartsWith("SYS:"))
        {
            lock (_audioPlayerLock)
            {
                _sysAudioPlayer ??= CreateAndStartPlayer();
                _sysAudioPlayer.Feed(prefixedData[4..]);
            }
        }
    }

    private static AudioPlayer CreateAndStartPlayer()
    {
        var player = new AudioPlayer();
        player.Start();
        return player;
    }

    public void Dispose()
    {
        _mic?.Dispose();
        _sysAudio?.Dispose();
        _webcam?.Dispose();
        _micPlayer?.Dispose();
        _sysAudioPlayer?.Dispose();
    }
}
