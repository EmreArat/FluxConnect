using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FluxConnect.Desktop.Core.Config;
using FluxConnect.Desktop.Core.Session;
using FluxConnect.Desktop.Core.Network;
using FluxConnect.Desktop.Core.Media;
using FluxConnect.Desktop.Core.Hardware;
using FluxConnect.Desktop.UI.Helpers;
using FluxConnect.Desktop.UI.Dialogs;
using System.IO;
// WinForms + WPF birlikte: çakışmayı gidermek için alias
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;

namespace FluxConnect.Desktop.UI;

public partial class MainWindow : Window
{
    private readonly SessionManager _session = App.Session;
    private readonly Dictionary<string, bool> _presenceCache = new();
    private MediaManager? _targetMedia;
    private FloatingWebcamWindow? _floatingWebcam;
    private FluxConnect.Desktop.Core.Network.FileTransferManager? _targetFileManager;
    private FluxConnect.Desktop.Core.Network.FileSystemManager? _targetFsManager;
    private FloatingTransferWindow? _targetTransferWindow;
    private string? _pendingHardwareConnect;

    public MainWindow()
    {
        InitializeComponent();
        BindSessionEvents();
        InitializeUI();
        _ = ConnectToRelayAsync();
    }

    // ----------------------------------------------------------------
    // UI Başlangıç
    // ----------------------------------------------------------------
    private void InitializeUI()
    {
        var config = App.Config;
        // ID'yi "XXX XXX XXX" formatında göster
        TxtMachineId.Text = FormatId(config.MachineId);
        TxtDisplayName.Text = config.DisplayName;
        Title = $"FluxConnect — {config.MachineId}";

        // TxtLocalIp'ye port numarasını dahil etmeden sadece IP'yi yazdırıyoruz
        TxtLocalIp.Text = $"Sizin IP adresiniz: {App.LanServer.GetLocalIpAddress()}";

        // Kişi listesini oluştur
        BuildContactsList();
    }

    // ----------------------------------------------------------------
    // Relay'e Bağlan
    // ----------------------------------------------------------------
    private async Task ConnectToRelayAsync()
    {
        SetStatus(false, "Bağlanıyor...");
        try
        {
            await _session.StartAsync();
        }
        catch (Exception ex)
        {
            // Eğer Relay internet uzaktaki sunucusuna bağlanılamazsa 
            // kullanıcıyı korkutmamak için durumu kibarca bildirip sadece LAN modunda olduğunu vurgulayalım
            SetStatus(false, $"Sadece Yerel Ağ (LAN) Modu - Relay yok");
        }
    }

    // ----------------------------------------------------------------
    // Oturum Olaylarını Bağla
    // ----------------------------------------------------------------
    private void BindSessionEvents()
    {
        _session.OnRelayConnected += () =>
            Dispatcher.Invoke(() =>
            {
                SetStatus(true, "Bağlı — Hazır");

                var contactIds = App.Config.Contacts
                    .Where(c => c.Address.Length == 9 && c.Address.All(char.IsDigit))
                    .Select(c => c.Address).ToArray();
                
                if (contactIds.Length > 0)
                {
                    _ = _session.SubscribePresenceAsync(contactIds);
                }
            });

        _session.OnRelayDisconnected += () =>
            Dispatcher.Invoke(() =>
            {
                SetStatus(false, "Bağlantı kesildi");
                _ = ReconnectAsync();
            });

        _session.OnRelayError += msg =>
            Dispatcher.Invoke(() =>
            {
                if (_pendingHardwareConnect != null &&
                    msg.Contains("çevrimdışı", StringComparison.OrdinalIgnoreCase))
                {
                    var hw = _pendingHardwareConnect;
                    _pendingHardwareConnect = null;
                    _ = FallbackLanConnectAsync(hw);
                    return;
                }
                ShowConnectMessage(msg, isError: true);
                BtnConnect.IsEnabled = true;
            });

        _session.OnIncomingRequest += (fromId, fromName, sessionId, requiresPassword) =>
            Dispatcher.Invoke(() => ShowIncomingRequest(fromId, fromName, sessionId, requiresPassword));

        _session.OnSessionStarted += session =>
            Dispatcher.Invoke(() =>
            {
                _pendingHardwareConnect = null;
                OnSessionStarted(session);
            });

        _session.OnSessionRejected += (sessionId, reason) =>
            Dispatcher.Invoke(() =>
            {
                if (TargetControlPanel.Visibility == Visibility.Visible)
                {
                    TargetControlPanel.Visibility = Visibility.Collapsed;
                    _targetMedia?.Dispose();
                    _targetMedia = null;
                    _floatingWebcam?.Close();
                    _floatingWebcam = null;
                }

                var msg = reason switch
                {
                    "wrong_password" => "Yanlış şifre.",
                    "timeout"        => "Bağlantı isteği zaman aşımına uğradı.",
                    "locked"         => "Çok fazla yanlış deneme. Lütfen bekleyin.",
                    _                => $"Bağlantı sonlandı: {reason}",
                };
                ShowConnectMessage(msg, isError: true);
                BtnConnect.IsEnabled = true;
            });

        _session.OnRelayData += (sessionId, data) =>
            Dispatcher.Invoke(() =>
            {
                // Sadece Target (ev sahibi) rolündeyken gelen medya verilerini işle.
                // Requester rolündeyken gelen veriler ViewerWindow tarafından işleniyor.
                if (_session.CurrentSession?.Role == SessionRole.Target &&
                    _session.CurrentSession.SessionId == sessionId)
                {
                    if (data == "CMD:OFF_CAM")
                    {
                        _targetMedia?.StopWebcam();
                        UpdateButtonState(BtnTargetWebcam, false, "Webcam (Açık)", "Webcam (Kapalı)");
                        if (_floatingWebcam?.IsVisible == true)
                        {
                            _floatingWebcam.Hide();
                        }
                        return;
                    }

                    if (data.StartsWith("FS:"))
                    {
                        _targetFsManager?.HandleIncomingCommand(data);
                        return;
                    }

                    if (data.StartsWith("FIL:"))
                    {
                        _targetFileManager?.HandleIncomingMessage(data);
                        return;
                    }

                    if (data.StartsWith("MIC:") || data.StartsWith("SYS:") || data.StartsWith("CAM:"))
                    {
                        _targetMedia?.HandleIncomingMedia(data);
                    }
                }
            });

        _session.OnPresenceUpdate += (id, online, displayName) =>
            Dispatcher.Invoke(() =>
            {
                _presenceCache[id] = online;
                BuildContactsList();
            });
    }

    // ----------------------------------------------------------------
    // Gelen Bağlantı İsteği
    // ----------------------------------------------------------------
    private void ShowIncomingRequest(string fromId, string fromName, string sessionId, bool requiresPassword)
    {
        var dialog = new AcceptConnectionDialog(fromId, fromName, sessionId, requiresPassword)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            if (fromId == "LAN")
                _ = _session.LanAcceptAsync();
            else
                _ = _session.AcceptAsync(sessionId, fromId, fromName);
        }
        else
        {
            if (fromId == "LAN")
                _ = _session.LanRejectAsync();
            else
                _ = _session.RejectAsync(sessionId);
        }
    }

    // ----------------------------------------------------------------
    // Oturum Başladı → Viewer Aç
    // ----------------------------------------------------------------
    private void OnSessionStarted(ActiveSession session)
    {
        ShowConnectMessage($"Bağlandı: {session.PeerDisplayName}", isError: false);
        
        // Sadece bağlanan kişi (İstekte bulunan) izleyici (Viewer) penceresini açar.
        if (session.Role == SessionRole.Requester)
        {
            var viewer = new ViewerWindow(session);
            viewer.Show();
        }
        else if (session.Role == SessionRole.Target)
        {
            TargetControlPanel.Visibility = Visibility.Visible;
            TxtConnectedPeer.Text = $"Bağlanan: {session.PeerDisplayName}";
            
            _targetMedia = new MediaManager();
            _targetMedia.OnMediaData += (prefix, data) => SendTargetRawData(prefix + data);

            _targetFileManager = new FluxConnect.Desktop.Core.Network.FileTransferManager();
            _targetFileManager.OnDataToSend += SendTargetRawData;
            _targetFileManager.OnProgress += (title, progress) =>
            {
                Dispatcher.Invoke(() =>
                {
                    _targetTransferWindow ??= new FloatingTransferWindow();
                    _targetTransferWindow.UpdateProgress(title, "", progress);
                });
            };
            _targetFileManager.OnSendCompleted += (name) => _targetTransferWindow?.Finish("Gönderildi: " + name);
            _targetFileManager.OnReceiveCompleted += (name) => _targetTransferWindow?.Finish("Alındı: " + name);
            _targetFileManager.OnError += (err) => _targetTransferWindow?.Error(err);

            _targetFsManager = new FluxConnect.Desktop.Core.Network.FileSystemManager();
            _targetFsManager.OnDataToSend += SendTargetRawData;
            _targetFsManager.OnFileRequested += async (remoteFilePath, localDestPath) =>
            {
                if (_targetFileManager != null)
                {
                    await _targetFileManager.SendFileAsync(remoteFilePath, localDestPath);
                }
            };

            // Floating webcam penceresini oluştur (henüz gösterme)
            _floatingWebcam = new FloatingWebcamWindow(session.PeerDisplayName);

            // Karşı tarafın "Ben kapattım" demesini ilet
            _floatingWebcam.OnUserClosed += () => SendTargetRawData("CMD:OFF_CAM");

            _targetMedia.OnRemoteWebcamFrame += (base64Jpeg) =>
            {
                Dispatcher.Invoke(() =>
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

                        // İlk kare gelince floating window'u göster
                        if (_floatingWebcam != null)
                        {
                            if (!_floatingWebcam.IsVisible)
                                _floatingWebcam.Show();
                            _floatingWebcam.UpdateFrame(bmp);
                        }
                    }
                    catch { }
                });
            };

            UpdateButtonState(BtnTargetMic, false, "Mikrofon (Açık)", "Mikrofon (Kapalı)");
            UpdateButtonState(BtnTargetSysAudio, false, "Sistem Sesi (Açık)", "Sistem Sesi (Kapalı)");
            UpdateButtonState(BtnTargetWebcam, false, "Webcam (Açık)", "Webcam (Kapalı)");
        }
    }

    private void SendTargetRawData(string prefixedData)
    {
        var ses = _session.CurrentSession;
        if (ses == null || ses.Role != SessionRole.Target) return;
        
        try
        {
            if (ses.IsLanMode)
                // Target LAN modunda: karşı tarafa App.LanServer üzerinden gönder
                _ = App.LanServer.SendRelayDataAsync(prefixedData);
            else if (App.Relay.IsConnected && !string.IsNullOrEmpty(ses.PeerId))
                _ = App.Relay.SendRelayDataAsync(ses.SessionId, ses.PeerId, prefixedData);
        }
        catch { }
    }

    // ----------------------------------------------------------------
    // Butonlar
    // ----------------------------------------------------------------
    private async void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        var input = TxtTargetId.Text.Trim();
        if (string.IsNullOrWhiteSpace(input)) return;

        BtnConnect.IsEnabled = false;

        // "XXX XXX XXX" veya "XXXXXXXXX" formatındaki ID mi?
        var cleanInput = input.Replace(" ", "");
        if (cleanInput.Length == 9 && cleanInput.All(char.IsDigit))
        {
            // 9 rakamdan oluşuyorsa doğrudan İnternet (Relay) bağlantısı dene
            await ConnectRelayAsync(cleanInput);
        }
        else if (HardwareIdProvider.IsHardwareAddress(cleanInput))
        {
            await ConnectByHardwareIdAsync(cleanInput);
        }
        else
        {
            // IP olarak değerlendir ve Local bağlantı (LAN) dene
            await ConnectLanAsync(input);
        }
    }

    private async Task ConnectRelayAsync(string targetId)
    {
        ShowConnectMessage("İnternet üzerinden bağlantı isteği gönderildi...", isError: false);

        try
        {
            await _session.RequestConnectionAsync(targetId);
        }
        catch (Exception ex)
        {
            ShowConnectMessage($"Relay hatası: {ex.Message}", isError: true);
            BtnConnect.IsEnabled = true;
        }
    }

    private async Task ConnectByHardwareIdAsync(string rawInput)
    {
        var hardwareId = HardwareIdProvider.ExtractHardwareId(rawInput);
        var contact = App.Config.Contacts.FirstOrDefault(c =>
            c.HardwareId == hardwareId ||
            c.Address.Equals(HardwareIdProvider.FormatAddress(hardwareId), StringComparison.OrdinalIgnoreCase));

        // 1) Relay üzerinden dene (farklı ağlar / internet)
        if (App.Relay.IsConnected)
        {
            ShowConnectMessage("İnternet üzerinden bilgisayar aranıyor...", isError: false);
            try
            {
                _pendingHardwareConnect = hardwareId;
                await _session.RequestConnectionByHardwareIdAsync(hardwareId);
                return;
            }
            catch (Exception ex)
            {
                _pendingHardwareConnect = null;
                ShowConnectMessage($"Relay bağlantısı başarısız: {ex.Message}", isError: false);
            }
        }

        await FallbackLanConnectAsync(hardwareId, contact);
    }

    private async Task FallbackLanConnectAsync(string hardwareId, SavedContact? contact = null)
    {
        contact ??= App.Config.Contacts.FirstOrDefault(c =>
            c.HardwareId == hardwareId ||
            c.Address.Equals(HardwareIdProvider.FormatAddress(hardwareId), StringComparison.OrdinalIgnoreCase));

        ShowConnectMessage("Yerel ağda bilgisayar aranıyor...", isError: false);

        var discovered = await LanDiscovery.FindAsync(hardwareId);
        var ip = discovered?.Ip ?? contact?.LastKnownIp;

        if (string.IsNullOrEmpty(ip))
        {
            ShowConnectMessage(
                App.Relay.IsConnected
                    ? "Bilgisayar çevrimdışı. Hedef makinede FluxConnect açık olmalı ve relay'e bağlı olmalı."
                    : "Bilgisayar bulunamadı. Aynı ağda olduklarınızdan veya internet relay bağlantısından emin olun.",
                isError: true);
            BtnConnect.IsEnabled = true;
            return;
        }

        if (discovered != null)
            ShowConnectMessage($"Yerel ağda bulundu: {ip}", isError: false);

        await ConnectLanAsync(ip, hardwareId);
    }

    private async Task ConnectLanAsync(string rawInput, string? knownHardwareId = null)
    {
        string ip = rawInput;
        int port = 9090;

        ShowConnectMessage($"{ip} adresine LAN üzerinden bağlanılıyor...", isError: false);

        try
        {
            var client = new DirectClient();

            await client.ConnectAsync(ip, port);

            await client.SendConnectionRequest(
                App.Config.DisplayName,
                "",
                App.Config.HardwareId,
                App.Config.MachineId);

            client.OnMessageReceived += (msg) =>
            {
                Dispatcher.Invoke(() =>
                {
                    var type = msg["type"]?.GetValue<string>() ?? "";
                    switch (type)
                    {
                        case "connect_accepted":
                            var peerName = msg["display_name"]?.GetValue<string>() ?? "Bilinmiyor";
                            var peerHardwareId = msg["hardware_id"]?.GetValue<string>() ?? knownHardwareId ?? "";
                            ShowConnectMessage($"LAN bağlantısı kabul edildi: {peerName}", isError: false);

                            if (!string.IsNullOrEmpty(peerHardwareId))
                            {
                                _session.SaveContact(
                                    HardwareIdProvider.FormatAddress(peerHardwareId),
                                    peerName,
                                    hardwareId: peerHardwareId,
                                    lastKnownIp: ip);
                            }
                            else
                            {
                                _session.SaveContact(ip, peerName, lastKnownIp: ip);
                            }
                            BuildContactsList();

                            var session = new ActiveSession
                            {
                                SessionId = "lan_" + Guid.NewGuid().ToString("N")[..8],
                                PeerId = !string.IsNullOrEmpty(peerHardwareId)
                                    ? HardwareIdProvider.FormatAddress(peerHardwareId)
                                    : ip,
                                PeerDisplayName = peerName,
                                Role = SessionRole.Requester,
                                IsLanMode = true,
                                DirectClient = client
                            };
                            var viewer = new ViewerWindow(session);
                            viewer.Show();
                            break;

                        case "connect_rejected":
                            ShowConnectMessage("Bağlantı reddedildi.", isError: true);
                            BtnConnect.IsEnabled = true;
                            client.Dispose();
                            break;
                    }
                });
            };

            client.OnDisconnected += () =>
            {
                Dispatcher.Invoke(() =>
                {
                    ShowConnectMessage("LAN bağlantısı kesildi.", isError: true);
                    BtnConnect.IsEnabled = true;
                });
            };
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrEmpty(knownHardwareId))
            {
                ShowConnectMessage("Bağlantı başarısız, yerel ağda yeniden aranıyor...", isError: false);
                var discovered = await LanDiscovery.FindAsync(knownHardwareId);
                if (discovered != null && discovered.Ip != ip)
                {
                    await ConnectLanAsync(discovered.Ip, knownHardwareId);
                    return;
                }
            }

            ShowConnectMessage($"LAN hatası (Hedef kapalı, IP yanlış VEYA Güvenlik Duvarı engelliyor!): {ex.Message}", isError: true);
            BtnConnect.IsEnabled = true;
        }
    }

    private void BtnCopyId_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(App.Config.MachineId);
        BtnCopyId.Content = "✅  Kopyalandı!";
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        timer.Tick += (_, _) =>
        {
            BtnCopyId.Content = "📋  ID'yi Kopyala";
            timer.Stop();
        };
        timer.Start();
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        // TODO: SettingsWindow (Faz 1 devamı)
        MessageBox.Show("Ayarlar yakında eklenecek.", "FluxConnect", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ----------------------------------------------------------------
    // TextBox Canlı Kontrol
    // ----------------------------------------------------------------
    private void TxtTargetId_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var text = TxtTargetId.Text.Trim();
        BtnConnect.IsEnabled = !string.IsNullOrWhiteSpace(text);
    }

    private void TxtTargetId_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && BtnConnect.IsEnabled)
            BtnConnect_Click(sender, new RoutedEventArgs());
    }

    // ----------------------------------------------------------------
    // Yeniden Bağlanma
    // ----------------------------------------------------------------
    private async Task ReconnectAsync()
    {
        await Task.Delay(5000);
        await ConnectToRelayAsync();
    }

    // ----------------------------------------------------------------
    // Yardımcılar
    // ----------------------------------------------------------------
    private void SetStatus(bool connected, string text)
    {
        StatusDot.Fill = (Brush)FindResource(connected ? "AccentBrush" : "DangerBrush");
        TxtStatus.Text = text;
    }

    private void ShowConnectMessage(string text, bool isError)
    {
        TxtConnectMessage.Text = text;
        TxtConnectMessage.Foreground = (Brush)FindResource(isError ? "DangerBrush" : "AccentBrush");
        TxtConnectMessage.Visibility = Visibility.Visible;
    }

    private static string FormatId(string id)
    {
        if (id.Length != 9) return id;
        return $"{id[..3]} {id[3..6]} {id[6..]}";
    }

    private static string GetContactConnectValue(SavedContact contact)
    {
        if (contact.IsRelayContact)
            return contact.Address;

        if (!string.IsNullOrEmpty(contact.HardwareId))
            return HardwareIdProvider.FormatAddress(contact.HardwareId);

        if (contact.Address.StartsWith("hw:", StringComparison.OrdinalIgnoreCase))
            return contact.Address;

        return contact.LastKnownIp ?? contact.Address;
    }

    // ----------------------------------------------------------------
    // Kişi Listesi
    // ----------------------------------------------------------------
    private void BuildContactsList()
    {
        // ContactsList'teki TxtNoContacts haricindeki elemanları temizle
        for (int i = ContactsList.Children.Count - 1; i >= 0; i--)
        {
            if (ContactsList.Children[i] != TxtNoContacts)
                ContactsList.Children.RemoveAt(i);
        }

        var contacts = App.Config.Contacts
            .OrderByDescending(c => c.IsFavorite)
            .ThenByDescending(c => c.LastConnected ?? DateTime.MinValue)
            .ToList();

        if (contacts.Count == 0)
        {
            TxtNoContacts.Visibility = Visibility.Visible;
            return;
        }

        TxtNoContacts.Visibility = Visibility.Collapsed;

        var contactIds = contacts
            .Where(c => c.Address.Length == 9 && c.Address.All(char.IsDigit))
            .Select(c => c.Address).ToArray();
        
        if (contactIds.Length > 0 && App.Relay != null && App.Relay.IsConnected)
        {
            _ = App.Session.SubscribePresenceAsync(contactIds);
        }

        foreach (var contact in contacts)
        {
            var row = CreateContactRow(contact);
            ContactsList.Children.Add(row);
        }
    }

    private static string FormatLastConnected(DateTime? lastConnected)
    {
        if (lastConnected == null)
            return "Henüz bağlanılmadı";

        var dt = lastConnected.Value;
        var diff = DateTime.Now - dt;

        if (diff.TotalMinutes < 1) return "Az önce";
        if (diff.TotalHours < 1) return $"{(int)diff.TotalMinutes} dk önce";
        if (diff.TotalDays < 1) return $"{(int)diff.TotalHours} saat önce";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays} gün önce";
        return dt.ToString("dd.MM.yyyy HH:mm");
    }

    private static Button CreateContactActionButton(string content, string tooltip, double fontSize = 12)
    {
        var btn = new Button
        {
            Content = content,
            FontSize = fontSize,
            ToolTip = tooltip,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            Padding = new Thickness(2, 0, 2, 0),
            MinWidth = 22,
            MinHeight = 22,
            VerticalAlignment = VerticalAlignment.Center
        };
        btn.PreviewMouseLeftButtonDown += (_, e) => e.Handled = true;
        return btn;
    }

    private void RemoveContact(SavedContact contact)
    {
        var displayName = string.IsNullOrWhiteSpace(contact.DisplayName)
            ? contact.Address
            : contact.DisplayName;

        if (MessageBox.Show(
                $"'{displayName}' son bağlantılar listesinden kaldırılsın mı?",
                "Bağlantıyı Kaldır",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        App.Config.Contacts.RemoveAll(c =>
            c.Address == contact.Address &&
            string.Equals(c.HardwareId ?? "", contact.HardwareId ?? "", StringComparison.OrdinalIgnoreCase));

        ConfigManager.Save(App.Config);
        BuildContactsList();
    }

    private Border CreateContactRow(SavedContact contact)
    {
        var isOnline = _presenceCache.TryGetValue(contact.Address, out var online) && online;
        var isId = contact.IsRelayContact;
        var isLan = contact.IsLanContact || System.Net.IPAddress.TryParse(contact.Address, out _);

        var subtitle = isId
            ? FormatId(contact.Address)
            : !string.IsNullOrEmpty(contact.HardwareId)
                ? $"GUID: {HardwareIdProvider.FormatDisplay(contact.HardwareId)}" +
                  (string.IsNullOrEmpty(contact.LastKnownIp) ? "" : $" · {contact.LastKnownIp}")
                : contact.LastKnownIp ?? contact.Address;

        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = (Brush)FindResource(isOnline ? "AccentBrush" : "OfflineBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = isId
                ? (isOnline ? "Çevrimiçi" : "Çevrimdışı")
                : isLan ? "LAN — makine kimliği ile tanınır" : "LAN (durum bilinmiyor)"
        };
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        var nameBlock = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
        var displayText = string.IsNullOrEmpty(contact.DisplayName) ? contact.Address : contact.DisplayName;

        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameLabel = new TextBlock
        {
            Text = displayText,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(nameLabel, 0);
        titleRow.Children.Add(nameLabel);

        var timeLabel = new TextBlock
        {
            Text = FormatLastConnected(contact.LastConnected),
            FontSize = 9,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(timeLabel, 1);
        titleRow.Children.Add(timeLabel);

        var metaLabel = new TextBlock
        {
            Text = subtitle,
            FontSize = 9,
            Foreground = (Brush)FindResource("TextMutedBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 1, 0, 0)
        };

        nameBlock.Children.Add(titleRow);
        nameBlock.Children.Add(metaLabel);
        Grid.SetColumn(nameBlock, 1);
        grid.Children.Add(nameBlock);

        var buttonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        };
        buttonsPanel.PreviewMouseLeftButtonDown += (_, e) => e.Handled = true;

        var capturedContact = contact;

        var starBtn = CreateContactActionButton(
            contact.IsFavorite ? "★" : "☆",
            contact.IsFavorite ? "Favorilerden çıkar" : "Favorilere ekle",
            fontSize: 14);
        starBtn.Foreground = contact.IsFavorite
            ? (Brush)FindResource("FavoriteBrush")
            : (Brush)FindResource("TextMutedBrush");
        starBtn.Click += (_, _) =>
        {
            capturedContact.IsFavorite = !capturedContact.IsFavorite;
            ConfigManager.Save(App.Config);
            BuildContactsList();
        };
        buttonsPanel.Children.Add(starBtn);

        var editBtn = CreateContactActionButton("✏", "Adı değiştir");
        editBtn.Foreground = (Brush)FindResource("TextSecondaryBrush");
        editBtn.Click += (_, _) =>
        {
            var dlg = new EditContactDialog(capturedContact.DisplayName) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                capturedContact.DisplayName = dlg.ResultDisplayName;
                ConfigManager.Save(App.Config);
                BuildContactsList();
            }
        };
        buttonsPanel.Children.Add(editBtn);

        var deleteBtn = CreateContactActionButton("🗑", "Listeden kaldır");
        deleteBtn.Foreground = (Brush)FindResource("TextMutedBrush");
        deleteBtn.Click += (_, _) => RemoveContact(capturedContact);
        buttonsPanel.Children.Add(deleteBtn);

        Grid.SetColumn(buttonsPanel, 2);
        grid.Children.Add(buttonsPanel);

        var border = new Border
        {
            Child = grid,
            Padding = new Thickness(6, 4, 4, 4),
            Margin = new Thickness(0, 0, 0, 2),
            CornerRadius = new CornerRadius(6),
            Background = System.Windows.Media.Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        border.MouseEnter += (_, _) =>
            border.Background = (Brush)FindResource("HoverOverlayBrush");
        border.MouseLeave += (_, _) =>
            border.Background = System.Windows.Media.Brushes.Transparent;

        border.MouseLeftButtonDown += (_, _) =>
        {
            TxtTargetId.Text = GetContactConnectValue(contact);
        };

        border.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                TxtTargetId.Text = GetContactConnectValue(contact);
                BtnConnect_Click(border, new RoutedEventArgs());
            }
        };

        return border;
    }

    // ----------------------------------------------------------------
    // Hedef (Target) Özel Eventleri
    // ----------------------------------------------------------------

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

    private void BtnTargetMic_Click(object sender, RoutedEventArgs e)
    {
        if (_targetMedia == null) return;
        try
        {
            _targetMedia.ToggleMicrophone();
            UpdateButtonState(BtnTargetMic, _targetMedia.IsMicActive, "Mikrofon (Açık)", "Mikrofon (Kapalı)");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Hata: {ex.Message}");
        }
    }

    private void BtnTargetSysAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_targetMedia == null) return;
        try
        {
            _targetMedia.ToggleSystemAudio();
            UpdateButtonState(BtnTargetSysAudio, _targetMedia.IsSysAudioActive, "Sistem Sesi (Açık)", "Sistem Sesi (Kapalı)");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Hata: {ex.Message}");
        }
    }

    private async void BtnTargetWebcam_Click(object sender, RoutedEventArgs e)
    {
        if (_targetMedia == null) return;
        try
        {
            if (_targetMedia.IsWebcamActive)
            {
                _targetMedia.ToggleWebcam();
                UpdateButtonState(BtnTargetWebcam, false, "Webcam (Açık)", "Webcam (Kapalı)");
                return;
            }

            if (!await WebcamDownloadUiHelper.EnsureOpenCvWithUiAsync(BtnTargetWebcam, this))
                return;

            _targetMedia.ToggleWebcam();
            UpdateButtonState(BtnTargetWebcam, _targetMedia.IsWebcamActive, "Webcam (Açık)", "Webcam (Kapalı)");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Hata: {ex.Message}");
        }
    }

    private async void BtnTargetEndSession_Click(object sender, RoutedEventArgs e)
    {
        TargetControlPanel.Visibility = Visibility.Collapsed;
        _targetMedia?.Dispose();
        _targetMedia = null;
        _floatingWebcam?.Close();
        _floatingWebcam = null;
        _targetTransferWindow?.Close();
        _targetTransferWindow = null;
        _targetFileManager?.Dispose();
        _targetFileManager = null;
        _targetFsManager = null; // No Dispose needed, just GC

        await App.Session.EndCurrentSessionAsync();
    }

    private void BtnTargetSendFile_Click(object sender, RoutedEventArgs e)
    {
        if (_targetFileManager == null || _targetFsManager == null) return;
        var fw = new FileExplorerWindow(_targetFsManager, _targetFileManager) { Owner = this };
        fw.Show();
    }
}
