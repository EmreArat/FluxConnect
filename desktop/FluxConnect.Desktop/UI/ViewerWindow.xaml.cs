using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using FluxConnect.Desktop.Core.Session;
using FluxConnect.Desktop.Core.Media;
using FluxConnect.Desktop.UI.Helpers;
using Brush = System.Windows.Media.Brush;

namespace FluxConnect.Desktop.UI;

public partial class ViewerWindow : Window
{
    private readonly ActiveSession _session;
    private readonly MediaManager _media = new();
    private FloatingWebcamWindow? _floatingWebcam;
    private readonly FluxConnect.Desktop.Core.Network.FileTransferManager _fileManager = new();
    private readonly FluxConnect.Desktop.Core.Network.FileSystemManager _fsManager = new();
    private FloatingTransferWindow? _transferWindow;
    private bool _isClosing = false;
    private bool _forceClose = false;
    private int _isProcessingFrame = 0;
    private int _isProcessingWebcam = 0;

    // Uzak giriş: fare RemoteScreen üzerindeyken iletilir; toolbar/dropdown etkileşiminde bastırılır.
    private bool _remoteInputAllowed;
    private DateTime _inputBlockedUntil = DateTime.MinValue;
    private double _lastMouseX = -1;
    private double _lastMouseY = -1;
    private const double MouseMoveThreshold = 0.001;

    public ViewerWindow(ActiveSession session)
    {
        InitializeComponent();
        _session = session;
        
        // Floating pencereyi burada oluştur, ama sadece veri gelince Show yap
        _floatingWebcam = new FloatingWebcamWindow(session.PeerDisplayName);

        // Kapatmaya basınca karşı tarafın kamerasını kapatmasını iste
        _floatingWebcam.OnUserClosed += () => SendRawData("CMD:OFF_CAM");

        _fileManager.OnDataToSend += SendRawData;
        _fileManager.OnProgress += (title, progress) =>
        {
            Dispatcher.Invoke(() =>
            {
                _transferWindow ??= new FloatingTransferWindow();
                _transferWindow.UpdateProgress(title, "", progress);
            });
        };
        _fileManager.OnSendCompleted += (name) => _transferWindow?.Finish("Gönderildi: " + name);
        _fileManager.OnReceiveCompleted += (name) => _transferWindow?.Finish("Alındı: " + name);
        _fileManager.OnError += (err) => _transferWindow?.Error(err);

        _fsManager.OnDataToSend += SendRawData;
        _fsManager.OnFileRequested += async (remoteFilePath, localDestPath) =>
        {
            await _fileManager.SendFileAsync(remoteFilePath, localDestPath);
        };

        InitializeSession();
        BindInputEvents();
        BindMediaEvents();

        Closing += (_, e) =>
        {
            if (_forceClose) return;
            e.Cancel = true;
            MessageBox.Show("Lütfen pencereyi kapatmak için sağ üstteki 'Bağlantıyı Kes' butonunu kullanın.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
    }

    private void InitializeSession()
    {
        TxtPeerName.Text = _session.PeerDisplayName;

        if (_session.IsLanMode && _session.DirectClient != null)
        {
            // LAN modu: DirectClient üzerinden veri al
            _session.DirectClient.OnMessageReceived += OnLanMessageReceived;
            _session.DirectClient.OnDisconnected += OnLanDisconnected;
        }
        else
        {
            // İnternet modu: Relay üzerinden veri al
            App.Session.OnRelayData += OnDataReceived;
            App.Session.OnSessionRejected += OnSessionClosed;
        }

        InitScreenSelector();
    }

    private void InitScreenSelector()
    {
        // İlk açılışta menüyü gizle. Veri gelince açılacak.
        ScreenSelectorPanel.Visibility = Visibility.Collapsed;

        CmbScreen.PreviewMouseDown += (_, _) => BlockRemoteInput(500);
        CmbScreen.DropDownOpened += (_, _) => BlockRemoteInput(500);
        CmbScreen.DropDownClosed += (_, _) => BlockRemoteInput(300);
    }

    private void UpdateScreenSelector(string[] names)
    {
        if (names == null || names.Length <= 1)
        {
            ScreenSelectorPanel.Visibility = Visibility.Collapsed;
            return;
        }

        CmbScreen.SelectionChanged -= CmbScreen_SelectionChanged;
        CmbScreen.Items.Clear();
        foreach (var name in names)
            CmbScreen.Items.Add(name);

        CmbScreen.SelectedIndex = 0;
        CmbScreen.SelectionChanged += CmbScreen_SelectionChanged;
        ScreenSelectorPanel.Visibility = Visibility.Visible;
    }

    // ----------------------------------------------------------------
    // Gelen Ekran Karesi
    // ----------------------------------------------------------------
    private void OnDataReceived(string sessionId, string base64Data)
    {
        if (sessionId != _session.SessionId) return;

        bool isScreenFrame = !(base64Data.StartsWith("FS:") || base64Data.StartsWith("FIL:") || 
                               base64Data.StartsWith("CMD:") || base64Data.StartsWith("MIC:") || 
                               base64Data.StartsWith("SYS:") || base64Data.StartsWith("CAM:") || 
                               base64Data.StartsWith("INF:"));

        if (isScreenFrame)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _isProcessingFrame, 1, 0) == 1)
            {
                return; // Yeni kare geldiğinde eskisi hala işleniyorsa, kuyruğu şişirmemek için bu kareyi atla
            }

            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (OverlayPanel.Visibility == Visibility.Visible)
                        OverlayPanel.Visibility = Visibility.Collapsed;

                    byte[] jpegBytes = Convert.FromBase64String(base64Data);
                    using var ms = new MemoryStream(jpegBytes);
                    var image = new System.Windows.Media.Imaging.BitmapImage();
                    image.BeginInit();
                    image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    image.StreamSource = ms;
                    image.EndInit();
                    image.Freeze();

                    RemoteScreen.Source = image;
                }
                catch (Exception ex)
                {
                    File.AppendAllText("flux_debug.txt",
                        $"[{DateTime.Now:HH:mm:ss}] [Viewer] Kare çözme hatası: {ex.Message}\n");
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _isProcessingFrame, 0);
                }
            });
            return;
        }

        if (base64Data.StartsWith("MIC:") || base64Data.StartsWith("SYS:") || base64Data.StartsWith("CAM:"))
        {
            _media.HandleIncomingMedia(base64Data);
            return;
        }

        Dispatcher.Invoke(() =>
        {
            // FS: Dosya yöneticisi komutları
            if (base64Data.StartsWith("FS:"))
            {
                _fsManager.HandleIncomingCommand(base64Data);
                return;
            }

            // Dosya transfer komutları (FIL:)
            if (base64Data.StartsWith("FIL:"))
            {
                _fileManager.HandleIncomingMessage(base64Data);
                return;
            }

            // Kamera kapatma komutu
            if (base64Data == "CMD:OFF_CAM")
            {
                _media.StopWebcam();
                UpdateButtonState(BtnWebcam, false, "Webcam (Açık)", "Webcam (Kapalı)");
                if (_floatingWebcam?.IsVisible == true)
                {
                    _floatingWebcam.Hide();
                }
                return;
            }

            // Ekran bilgisi (Metadata) geldiyse
            if (base64Data.StartsWith("INF:"))
            {
                try
                {
                    var jsonBase64 = base64Data[4..];
                    var jsonBytes = Convert.FromBase64String(jsonBase64);
                    var json = System.Text.Encoding.UTF8.GetString(jsonBytes);
                    
                    var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("names", out var namesArray))
                    {
                        var namesList = new System.Collections.Generic.List<string>();
                        foreach (var el in namesArray.EnumerateArray())
                            namesList.Add(el.GetString() ?? "");
                        
                        UpdateScreenSelector(namesList.ToArray());
                    }
                }
                catch (Exception ex)
                {
                    File.AppendAllText("flux_debug.txt",
                        $"[{DateTime.Now:HH:mm:ss}] [Viewer] Ekran verisi çözme hatası: {ex.Message}\n");
                }
                return;
            }
        });
    }

    // ---- LAN Modu: DirectClient'tan gelen mesajlar ----
    private void OnLanMessageReceived(System.Text.Json.Nodes.JsonNode msg)
    {
        var type = msg["type"]?.GetValue<string>() ?? "";
        if (type == "relay")
        {
            var data = msg["data"]?.GetValue<string>() ?? "";
            if (data == "CMD:END_SESSION")
            {
                Dispatcher.Invoke(() =>
                {
                    OnSessionClosed(_session.SessionId, "Karşı taraf bağlantıyı sonlandırdı.");
                });
                return;
            }
            OnDataReceived(_session.SessionId, data);
        }
    }

    private void OnLanDisconnected()
    {
        Dispatcher.Invoke(() => _ = EndViewerAsync(notifyPeer: false, "LAN bağlantısı kesildi."));
    }

    // ----------------------------------------------------------------
    // Giriş Olayları (Fare + Klavye) → Relay'e Gönder
    // ----------------------------------------------------------------
    private bool CanSendRemoteInput() =>
        _remoteInputAllowed && DateTime.UtcNow >= _inputBlockedUntil;

    private void BlockRemoteInput(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        if (until > _inputBlockedUntil)
            _inputBlockedUntil = until;
    }

    private void BindInputEvents()
    {
        RemoteScreen.MouseEnter += (_, _) => _remoteInputAllowed = true;

        RemoteScreen.MouseLeave += (_, _) =>
        {
            _remoteInputAllowed = false;
            _lastMouseX = -1;
            _lastMouseY = -1;
        };

        RemoteScreen.MouseMove += (_, e) =>
        {
            if (!CanSendRemoteInput()) return;

            var pos = e.GetPosition(RemoteScreen);
            var w = RemoteScreen.ActualWidth;
            var h = RemoteScreen.ActualHeight;
            if (w <= 0 || h <= 0) return;

            var x = pos.X / w;
            var y = pos.Y / h;
            if (Math.Abs(x - _lastMouseX) < MouseMoveThreshold &&
                Math.Abs(y - _lastMouseY) < MouseMoveThreshold)
                return;

            _lastMouseX = x;
            _lastMouseY = y;
            SendInput(new { t = "mm", x, y });
        };

        RemoteScreen.MouseDown += (_, e) =>
        {
            if (!CanSendRemoteInput()) return;

            RemoteScreen.CaptureMouse();
            var btn = ButtonName(e.ChangedButton);
            SendInput(new { t = "mc", b = btn, d = true });
        };

        RemoteScreen.MouseUp += (_, e) =>
        {
            RemoteScreen.ReleaseMouseCapture();
            if (!CanSendRemoteInput()) return;

            var btn = ButtonName(e.ChangedButton);
            SendInput(new { t = "mc", b = btn, d = false });
        };

        RemoteScreen.MouseWheel += (_, e) =>
        {
            if (!CanSendRemoteInput()) return;
            SendInput(new { t = "mw", d = e.Delta > 0 ? 1 : -1 });
        };

        // Klavye odağı almak için
        this.Focusable = true;
        this.Focus();

        this.PreviewKeyDown += (_, e) =>
        {
            // Eğer odaklı bir TextBox vs. değilse klavye tuşunu karşıya gönder
            if (e.OriginalSource is System.Windows.Controls.TextBox) return;

            // Farenin uzak ekran üzerinde olup olmadığını kontrol edebiliriz
            // ama Viewer penceresindeyken tuşların iletilmesi genellikle yeterlidir.
            e.Handled = true;
            SendInput(new { t = "kd", k = KeyInterop.VirtualKeyFromKey(e.Key) });
        };

        this.PreviewKeyUp += (_, e) =>
        {
            if (e.OriginalSource is System.Windows.Controls.TextBox) return;
            e.Handled = true;
            SendInput(new { t = "ku", k = KeyInterop.VirtualKeyFromKey(e.Key) });
        };
    }

    private static string ButtonName(MouseButton btn) => btn switch
    {
        MouseButton.Left => "L",
        MouseButton.Right => "R",
        MouseButton.Middle => "M",
        _ => "L"
    };

    private void CmbScreen_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var idx = CmbScreen.SelectedIndex;
        if (idx < 0) return;

        BlockRemoteInput(300);
        SendInput(new { t = "scr", i = idx });
    }

    private void SendInput(object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload);
            var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
            SendRawData("INP:" + base64);
        }
        catch { /* Sessizce geç */ }
    }

    /// <summary>Prefix'li veriyi relay/LAN üzerinden gönderir.</summary>
    private void SendRawData(string prefixedData)
    {
        try
        {
            if (_session.IsLanMode && _session.DirectClient != null)
            {
                _ = _session.DirectClient.SendRelayDataAsync(prefixedData);
            }
            else
            {
                if (!App.Relay.IsConnected || _session.PeerId == null) return;
                _ = App.Relay.SendRelayDataAsync(_session.SessionId, _session.PeerId, prefixedData);
            }
        }
        catch { /* Sessizce geç */ }
    }

    // ----------------------------------------------------------------
    // Medya Kontrolleri
    // ----------------------------------------------------------------
    private void BindMediaEvents()
    {
        _media.OnMediaData += (prefix, data) =>
        {
            SendRawData(prefix + data);
        };

        _media.OnRemoteWebcamFrame += (base64Jpeg) =>
        {
            if (System.Threading.Interlocked.CompareExchange(ref _isProcessingWebcam, 1, 0) == 1)
            {
                return; // Eğer webcam karesi işleniyorsa UI kilitlenmesin diye yenisini atla
            }

            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    byte[] jpegBytes = Convert.FromBase64String(base64Jpeg);
                    using var ms = new MemoryStream(jpegBytes);
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();

                    if (_floatingWebcam != null)
                    {
                        if (!_floatingWebcam.IsVisible)
                            _floatingWebcam.Show();
                        
                        _floatingWebcam.UpdateFrame(bmp);
                    }
                }
                catch { }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _isProcessingWebcam, 0);
                }
            });
        };
    }

    private void UpdateButtonState(System.Windows.Controls.Button btn, bool isActive, string tooltipOn, string tooltipOff)
    {
        btn.Opacity = 1.0;
        btn.ToolTip = isActive ? tooltipOn : tooltipOff;
        btn.Background = isActive
            ? (Brush)FindResource("AccentSoftBrush")
            : System.Windows.Media.Brushes.Transparent;
        btn.BorderBrush = isActive
            ? (Brush)FindResource("AccentBrush")
            : (Brush)FindResource("BorderBrush");
    }

    private void BtnMic_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _media.ToggleMicrophone();
            UpdateButtonState(BtnMic, _media.IsMicActive, "Mikrofon (Açık)", "Mikrofon (Kapalı)");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Mikrofon açılamadı: {ex.Message}", "Hata",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnSysAudio_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _media.ToggleSystemAudio();
            UpdateButtonState(BtnSysAudio, _media.IsSysAudioActive, "Sistem Sesi (Açık)", "Sistem Sesi (Kapalı)");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Sistem sesi açılamadı: {ex.Message}", "Hata",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void BtnWebcam_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_media.IsWebcamActive)
            {
                _media.ToggleWebcam();
                UpdateButtonState(BtnWebcam, false, "Webcam (Açık)", "Webcam (Kapalı)");
                return;
            }

            if (!await WebcamDownloadUiHelper.EnsureOpenCvWithUiAsync(BtnWebcam, this))
                return;

            _media.ToggleWebcam();
            UpdateButtonState(BtnWebcam, _media.IsWebcamActive, "Webcam (Açık)", "Webcam (Kapalı)");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Webcam açılamadı: {ex.Message}", "Hata",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnSendFile_Click(object sender, RoutedEventArgs e)
    {
        var fw = new FileExplorerWindow(_fsManager, _fileManager) { Owner = this };
        fw.Show();
    }

    // ----------------------------------------------------------------
    // Oturum Kapatma
    // ----------------------------------------------------------------
    private void OnSessionClosed(string sessionId, string reason)
    {
        if (sessionId != _session.SessionId) return;
        Dispatcher.Invoke(() => _ = EndViewerAsync(notifyPeer: false, $"Oturum kapatıldı: {reason}"));
    }

    private async void BtnEndSession_Click(object sender, RoutedEventArgs e)
    {
        await EndViewerAsync(notifyPeer: true);
    }

    private async Task EndViewerAsync(bool notifyPeer, string? message = null)
    {
        if (_isClosing) return;
        _isClosing = true;
        _forceClose = true;

        if (_session.IsLanMode && _session.DirectClient != null)
        {
            _session.DirectClient.OnDisconnected -= OnLanDisconnected;
            _session.DirectClient.OnMessageReceived -= OnLanMessageReceived;
        }
        else
        {
            App.Session.OnSessionRejected -= OnSessionClosed;
            App.Session.OnRelayData -= OnDataReceived;
        }

        _floatingWebcam?.ForceClose();
        _floatingWebcam = null;
        _transferWindow?.Close();
        _transferWindow = null;
        _media.Dispose();
        _fileManager.Dispose();

        if (notifyPeer)
        {
            try { await App.Session.EndCurrentSessionAsync(); }
            catch { /* Ana pencere açık kalsın */ }
        }

        if (!string.IsNullOrEmpty(message))
            MessageBox.Show(message, "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);

        App.MainWindowInstance?.RestoreAfterSession();
        Close();
    }
}
