using System.Text.Json.Nodes;
using FluxConnect.Desktop.Core.Config;
using FluxConnect.Desktop.Core.Network;
using FluxConnect.Desktop.Core.Capture;
using FluxConnect.Desktop.Core.Input; // Giriş komutları için
using FluxConnect.Desktop.Core.Hardware;
using FluxConnect.Desktop.Core.Security;

namespace FluxConnect.Desktop.Core.Session;

public enum SessionRole { Requester, Target }

public class ActiveSession
{
    public string SessionId { get; init; } = string.Empty;
    public string PeerId { get; init; } = string.Empty;
    public string PeerDisplayName { get; init; } = string.Empty;
    public SessionRole Role { get; init; }
    public DateTime StartedAt { get; } = DateTime.Now;

    // LAN modu
    public bool IsLanMode { get; init; }
    public DirectClient? DirectClient { get; init; }
}

/// <summary>
/// Oturum yaşam döngüsünü yönetir. Relay mesajlarını yorumlar,
/// UI katmanına olaylar ile bildirim gönderir.
/// </summary>
public class SessionManager : IDisposable
{
    private readonly RelayClient _relay;
    private readonly AppConfig _config;
    private ScreenSender? _screenSender;

    // ---- Olaylar (UI tarafından dinlenir) ----
    public event Action? OnRelayConnected;
    public event Action? OnRelayDisconnected;
    public event Action<string>? OnRelayError;

    /// <summary>Gelen bağlantı isteği — (fromId, fromDisplayName, sessionId, requiresPassword)</summary>
    public event Action<string, string, string, bool>? OnIncomingRequest;

    /// <summary>Bağlantı kabul edildi</summary>
    public event Action<ActiveSession>? OnSessionStarted;

    /// <summary>Bağlantı reddedildi / zaman aşımı / kilitlendi</summary>
    public event Action<string, string>? OnSessionRejected; // (sessionId, reason)

    /// <summary>E2EE şifreli ham veri alındı</summary>
    public event Action<string, string>? OnRelayData; // (sessionId, base64Data)

    /// <summary>Çevrimiçi durum değişikliği (id, online, displayName)</summary>
    public event Action<string, bool, string?>? OnPresenceUpdate;

    /// <summary>İstek gönderildi — (sessionId, targetHasPassword)</summary>
    public event Action<string, bool>? OnConnectPending;

    /// <summary>Şifre doğrulama sonucu — (sessionId, success)</summary>
    public event Action<string, bool>? OnPasswordResult;

    public ActiveSession? CurrentSession { get; private set; }

    private readonly Dictionary<string, (string FromId, string FromName)> _pendingIncoming = new();
    private int _suppressDisconnectNotify;

    public SessionManager(RelayClient relay, AppConfig config)
    {
        _relay = relay;
        _config = config;

        _relay.OnConnected += () =>
        {
            OnRelayConnected?.Invoke();
            // Relay'e bağlandıktan sonra kişi listesindeki ID'lere abone ol
            _ = SubscribeToContactsAsync();
        };
        _relay.OnDisconnected += () =>
        {
            if (Volatile.Read(ref _suppressDisconnectNotify) > 0)
                return;
            OnRelayDisconnected?.Invoke();
        };
        _relay.OnError += msg => OnRelayError?.Invoke(msg);
        _relay.OnMessageReceived += HandleMessage;
        _relay.OnRelayFrameReceived += HandleRelayFrame;
    }

    private void HandleRelayFrame(string sessionId, string fromId, RelayFrame frame)
    {
        if (!E2EFrame.TryUnwrap(frame, out frame))
            return;

        var data = RelayFrameCodec.ToLegacyString(frame);
        if (string.IsNullOrEmpty(data))
            return;

        if (data == "CMD:END_SESSION")
        {
            StopScreenSender();
            CurrentSession = null;
            OnSessionRejected?.Invoke(sessionId, "Karşı taraf bağlantıyı sonlandırdı.");
            return;
        }

        if (CurrentSession?.Role == SessionRole.Target && data.StartsWith("INP:"))
            InputReceiver.Handle(data[4..]);

        OnRelayData?.Invoke(sessionId, data);
    }

    /// <summary>Config'deki kişilerin ID'lerine presence aboneliği başlat</summary>
    public async Task SubscribeToContactsAsync()
    {
        var relayIds = _config.Contacts
            .Where(c => c.Address.Length == 9 && c.Address.All(char.IsDigit))
            .Select(c => c.Address)
            .ToArray();

        if (relayIds.Length > 0 && _relay.IsConnected)
        {
            try { await _relay.SubscribePresenceAsync(relayIds); }
            catch { /* Sessizce geç */ }
        }
    }

    /// <summary>Bağlantı sonrası kişiyi config'e kaydet</summary>
    public void SaveContact(string address, string displayName, string? hardwareId = null, string? lastKnownIp = null)
    {
        SavedContact? existing = null;

        if (!string.IsNullOrEmpty(hardwareId))
            existing = _config.Contacts.FirstOrDefault(c => c.HardwareId == hardwareId);

        if (existing == null && !string.IsNullOrEmpty(lastKnownIp))
            existing = _config.Contacts.FirstOrDefault(c =>
                c.LastKnownIp == lastKnownIp || c.Address == lastKnownIp);

        if (existing == null)
            existing = _config.Contacts.FirstOrDefault(c => c.Address == address);

        // hw:XXX ile kayıtlı eski kişiyi, aynı donanımın 9 haneli ID'siyle eşleştir
        if (existing == null && IsNineDigitMachineId(address) && !string.IsNullOrEmpty(hardwareId))
        {
            existing = _config.Contacts.FirstOrDefault(c =>
                c.Address.Equals(HardwareIdProvider.FormatAddress(hardwareId), StringComparison.OrdinalIgnoreCase));
        }

        if (existing != null)
        {
            existing.LastConnected = DateTime.Now;
            if (string.IsNullOrEmpty(existing.DisplayName))
                existing.DisplayName = displayName;

            if (!string.IsNullOrEmpty(hardwareId))
                existing.HardwareId = hardwareId;

            if (!string.IsNullOrEmpty(lastKnownIp))
                existing.LastKnownIp = lastKnownIp;

            // 9 haneli Makine ID asla hw:GUID ile ezilmesin — presence buna bağlı
            if (IsNineDigitMachineId(address))
                existing.Address = address;
            else if (!existing.IsRelayContact)
            {
                if (!string.IsNullOrEmpty(hardwareId))
                    existing.Address = HardwareIdProvider.FormatAddress(hardwareId);
                else
                    existing.Address = address;
            }
        }
        else
        {
            var contact = new SavedContact
            {
                Address = IsNineDigitMachineId(address)
                    ? address
                    : (!string.IsNullOrEmpty(hardwareId)
                        ? HardwareIdProvider.FormatAddress(hardwareId)
                        : address),
                DisplayName = displayName,
                HardwareId = hardwareId,
                LastKnownIp = lastKnownIp,
                LastConnected = DateTime.Now
            };
            _config.Contacts.Add(contact);
        }

        ConfigManager.Save(_config);
    }

    private static bool IsNineDigitMachineId(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length == 9 && value.All(char.IsDigit);

    /// <summary>LAN sunucusu event'lerini bağlar (App.xaml.cs'den çağrılır)</summary>
    public void BindLanServer(LocalServer server)
    {
        server.OnConnectionRequest += (peerName, _, _, _) =>
        {
            _pendingIncoming["lan_session"] = ("LAN", peerName);
            var hasPassword = !string.IsNullOrEmpty(_config.SessionPasswordHash);
            OnIncomingRequest?.Invoke("LAN", peerName, "lan_session", hasPassword);
        };

        server.OnPasswordAttempt += async (passwordHash) =>
        {
            if (PasswordHelper.VerifyHash(passwordHash, _config.SessionPasswordHash))
                await AcceptLanWithPasswordAsync();
        };

        server.OnClientDisconnected += () =>
        {
            if (CurrentSession?.IsLanMode == true && CurrentSession.Role == SessionRole.Target)
            {
                var sid = CurrentSession.SessionId;
                CurrentSession = null;
                StopScreenSender();
                OnSessionRejected?.Invoke(sid, "İstemci bağlantıyı kesti.");
            }
        };

        server.OnDataReceived += (data) =>
        {
            if (data == "CMD:END_SESSION")
            {
                if (CurrentSession?.Role == SessionRole.Target)
                {
                    var sid = CurrentSession.SessionId;
                    CurrentSession = null;
                    StopScreenSender();
                    OnSessionRejected?.Invoke(sid, "Karşı taraf bağlantıyı sonlandırdı.");
                }
                return;
            }

            // Giriş komutu mu? (INP:)
            if (data.StartsWith("INP:"))
            {
                Input.InputReceiver.Handle(data[4..]);
            }
            // Tüm relay verilerini yönlendir ki diğer işlemler de yakalayabilsin
            OnRelayData?.Invoke("lan_session", data);
        };
    }

    /// <summary>LAN bağlantısını kabul et ve ekran paylaşımını başlat</summary>
    public async Task LanAcceptAsync()
    {
        await App.LanServer.RespondToConnection(true, _config.DisplayName, _config.HardwareId, _config.MachineId);

        CurrentSession = new ActiveSession
        {
            SessionId = "lan_session",
            PeerId = "LAN",
            PeerDisplayName = "LAN İstemci",
            Role = SessionRole.Target,
            IsLanMode = true
        };
        StartE2E(CurrentSession);

        // Ekran paylaşımını başlat — LAN modunda LocalServer üzerinden gönder
        try
        {
            _screenSender?.Dispose();
            _screenSender = new ScreenSender("lan_session", "LAN");
            
            // ScreenSender'a LAN gönderme yolunu bağla
            // (Normal relay yerine LocalServer.SendRelayDataAsync kullanılacak)
            _screenSender.Start();
            System.IO.File.AppendAllText("flux_debug.txt", $"[{DateTime.Now:HH:mm:ss}] [SessionManager] LAN ScreenSender başlatıldı.\n");
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText("flux_debug.txt", $"[{DateTime.Now:HH:mm:ss}] [SessionManager] LAN ScreenSender hatası: {ex}\n");
        }

        OnSessionStarted?.Invoke(CurrentSession);
    }

    /// <summary>LAN bağlantısını reddet</summary>
    public async Task LanRejectAsync()
    {
        await App.LanServer.RespondToConnection(false, _config.DisplayName, _config.HardwareId, _config.MachineId);
    }

    // ----------------------------------------------------------------
    // Relay'e Bağlan & Kayıt Ol
    // ----------------------------------------------------------------
    public async Task<bool> StartAsync()
    {
        if (string.IsNullOrWhiteSpace(_config.RelayUrl))
            return false;

        await _relay.ConnectAsync(_config.RelayUrl, _config.RelayCertFingerprint);
        await RegisterOnRelayAsync();
        return true;
    }

    public async Task ReconnectRelayAsync()
    {
        Interlocked.Increment(ref _suppressDisconnectNotify);
        try
        {
            try { await _relay.DisconnectAsync(); }
            catch { /* Yeniden bağlanırken eski soket kapanmayabilir */ }

            try
            {
                if (!await StartAsync())
                    OnRelayDisconnected?.Invoke();
            }
            catch
            {
                OnRelayDisconnected?.Invoke();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _suppressDisconnectNotify);
        }
    }

    public async Task RefreshRegistrationAsync()
    {
        if (_relay.IsConnected)
            await RegisterOnRelayAsync();
    }

    private Task RegisterOnRelayAsync()
    {
        var hasPassword = !string.IsNullOrEmpty(_config.SessionPasswordHash);
        return _relay.RegisterAsync(_config.MachineId, _config.DisplayName, _config.HardwareId, hasPassword);
    }

    public Task SendPasswordAttemptAsync(string sessionId, string passwordHash) =>
        _relay.SendPasswordAttemptAsync(sessionId, passwordHash);

    public Task SendPasswordVerifyResultAsync(string sessionId, bool success) =>
        _relay.SendPasswordVerifyResultAsync(sessionId, success);

    public async Task SendLanPasswordAttemptAsync(DirectClient client, string passwordHash) =>
        await client.SendPasswordAttemptAsync(passwordHash);

    public bool TryVerifySessionPassword(string? passwordHash) =>
        PasswordHelper.VerifyHash(passwordHash, _config.SessionPasswordHash);

    public Task SubscribePresenceAsync(string[] ids)
    {
        if (_relay.IsConnected)
            return _relay.SubscribePresenceAsync(ids);
        return Task.CompletedTask;
    }

    // ----------------------------------------------------------------
    // Bağlantı İsteği Gönder
    // ----------------------------------------------------------------
    public async Task RequestConnectionByHardwareIdAsync(string hardwareId)
    {
        await _relay.RequestConnectAsync(HardwareIdProvider.FormatAddress(hardwareId));
    }

    public async Task RequestConnectionAsync(string targetId)
    {
        await _relay.RequestConnectAsync(targetId);
    }

    // ----------------------------------------------------------------
    // Bağlantı İsteğini Yanıtla
    // ----------------------------------------------------------------
    public async Task AcceptAsync(string sessionId, string peerId, string peerDisplayName)
    {
        await _relay.RespondToConnectionAsync(sessionId, accepted: true);
        CurrentSession = new ActiveSession
        {
            SessionId = sessionId,
            PeerId = peerId,
            PeerDisplayName = peerDisplayName,
            Role = SessionRole.Target,
        };
        StartE2E(CurrentSession);
        
        // Ekran paylaşımını başlat
        try
        {
            _screenSender?.Dispose();
            _screenSender = new ScreenSender(sessionId, peerId);
            _screenSender.Start();
            System.IO.File.AppendAllText("flux_debug.txt", $"[{DateTime.Now:HH:mm:ss}] [SessionManager] ScreenSender başlatıldı.\n");
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText("flux_debug.txt", $"[{DateTime.Now:HH:mm:ss}] [SessionManager] ScreenSender başlatılırken hata: {ex.ToString()}\n");
        }

        OnSessionStarted?.Invoke(CurrentSession);
    }

    public async Task RejectAsync(string sessionId)
    {
        await _relay.RespondToConnectionAsync(sessionId, accepted: false);
        StopScreenSender();
    }

    /// <summary>Aktif ekranı değiştirir (sadece Target rolünde etkili).</summary>
    public void SwitchScreen(int screenIndex)
    {
        _screenSender?.SwitchScreen(screenIndex);
    }

    /// <summary>Hedef makinenin ekran listesini döndürür.</summary>
    public (int Count, string[] Names) GetScreenInfo()
    {
        return ScreenSender.GetScreenInfo();
    }

    /// <summary>Her iki taraf da (Target veya Viewer) oturumu sonlandırmak istediğinde çağrılır.</summary>
    public async Task EndCurrentSessionAsync()
    {
        var session = CurrentSession;
        if (session == null) return;

        try
        {
            if (session.IsLanMode)
            {
                if (session.DirectClient != null)
                {
                    await session.DirectClient.SendRelayDataAsync("CMD:END_SESSION");
                    await session.DirectClient.DisconnectAsync();
                }
                else if (session.Role == SessionRole.Target)
                {
                    await App.LanServer.SendRelayDataAsync("CMD:END_SESSION");
                }
            }
            else if (_relay.IsConnected && !string.IsNullOrEmpty(session.PeerId))
            {
                await _relay.SendRelayDataAsync(session.SessionId, session.PeerId, "CMD:END_SESSION");
            }
        }
        catch { /* Sessizce geç */ }

        StopScreenSender();
        CurrentSession = null;
        OnSessionRejected?.Invoke(session.SessionId, "Bağlantı kesildi.");
    }

    // ----------------------------------------------------------------
    // Mesaj Yönlendirici
    // ----------------------------------------------------------------
    private void HandleMessage(JsonObject msg)
    {
        var type = msg["type"]?.GetValue<string>();

        switch (type)
        {
            case "registered":
                // Başarıyla kaydedildi — UI bunu OnRelayConnected'tan zaten bilir
                break;

            case "incoming_request":
            {
                var fromId = msg["from_id"]?.GetValue<string>() ?? "";
                var fromName = msg["from_display_name"]?.GetValue<string>() ?? "";
                var sessionId = msg["session_id"]?.GetValue<string>() ?? "";
                var requiresPassword = msg["requires_password"]?.GetValue<bool>() ?? false;
                _pendingIncoming[sessionId] = (fromId, fromName);
                OnIncomingRequest?.Invoke(fromId, fromName, sessionId, requiresPassword);
                break;
            }

            case "connect_pending":
            {
                var sessionId = msg["session_id"]?.GetValue<string>() ?? "";
                var targetHasPassword = msg["target_has_password"]?.GetValue<bool>() ?? false;
                OnConnectPending?.Invoke(sessionId, targetHasPassword);
                break;
            }

            case "password_result":
            {
                var sessionId = msg["session_id"]?.GetValue<string>() ?? "";
                var success = msg["success"]?.GetValue<bool>() ?? false;
                OnPasswordResult?.Invoke(sessionId, success);
                break;
            }

            case "connect_accepted":
            {
                var sessionId = msg["session_id"]?.GetValue<string>() ?? "";
                
                // Hedef taraf (Target) isek ve kendi oluşturduğumuz oturumun id'siyle eşleşiyorsa, 
                // bu isteği kendisi başlattığı için gelen mesajı yoksay.
                if (CurrentSession != null && CurrentSession.Role == SessionRole.Target && CurrentSession.SessionId == sessionId)
                {
                    break;
                }

                var peerId = msg["peer_id"]?.GetValue<string>() ?? "";
                var peerName = msg["peer_display_name"]?.GetValue<string>() ?? "";
                var peerHardwareId = msg["peer_hardware_id"]?.GetValue<string>();
                CurrentSession = new ActiveSession
                {
                    SessionId = sessionId,
                    PeerId = peerId,
                    PeerDisplayName = peerName,
                    Role = SessionRole.Requester,
                };
                StartE2E(CurrentSession);

                // Başarılı bağlantı: kişiyi kaydet (relay ID + MachineGuid)
                if (!string.IsNullOrEmpty(peerHardwareId))
                    SaveContact(peerId, peerName, hardwareId: peerHardwareId);
                else
                    SaveContact(peerId, peerName);

                OnSessionStarted?.Invoke(CurrentSession);
                break;
            }

            case "connect_rejected":
            {
                var sessionId = msg["session_id"]?.GetValue<string>() ?? "";
                var reason = msg["reason"]?.GetValue<string>() ?? "rejected";
                StopScreenSender();
                OnSessionRejected?.Invoke(sessionId, reason);
                break;
            }

            case "relay":
            {
                var sessionId = msg["session_id"]?.GetValue<string>() ?? "";
                var fromId = msg["from_id"]?.GetValue<string>() ?? "";
                var data = msg["data"]?.GetValue<string>() ?? "";

                if (TryHandlePasswordCheckRelay(sessionId, fromId, data))
                    break;
                
                // Karşı taraf bağlantıyı kestiyse
                if (data == "CMD:END_SESSION")
                {
                    StopScreenSender();
                    CurrentSession = null;
                    OnSessionRejected?.Invoke(sessionId, "Karşı taraf bağlantıyı sonlandırdı.");
                    break;
                }

                // Hedef (Target) isek gelen veri giriş komutu olabilir ("INP:" öneki ile)
                if (CurrentSession?.Role == SessionRole.Target && data.StartsWith("INP:"))
                {
                    InputReceiver.Handle(data[4..]); // "İNP:" önekini çıkar
                }
                
                // Medya verileri (MIC:, SYS:, CAM:) ve ekran kareleri dahil her şeyi pasla
                // Not: Hem Target (medya) hem de Requester (ekran ve medya) veriler alabilir.
                OnRelayData?.Invoke(sessionId, data);
                
                break;
            }

            case "error":
            {
                var errorMsg = msg["message"]?.GetValue<string>() ?? "Bilinmeyen hata";
                OnRelayError?.Invoke(errorMsg);
                break;
            }

            case "pong":
                // Canlılık yanıtı — şimdilik loglanmıyor
                break;

            case "presence_update":
            {
                var id = msg["id"]?.GetValue<string>() ?? "";
                var online = msg["online"]?.GetValue<bool>() ?? false;
                var displayName = msg["display_name"]?.GetValue<string>();
                OnPresenceUpdate?.Invoke(id, online, displayName);
                break;
            }

            case "presence_list":
            {
                var statuses = msg["statuses"];
                if (statuses is System.Text.Json.Nodes.JsonArray arr)
                {
                    foreach (var item in arr)
                    {
                        if (item == null) continue;
                        var id = item["id"]?.GetValue<string>() ?? "";
                        var online = item["online"]?.GetValue<bool>() ?? false;
                        var displayName = item["display_name"]?.GetValue<string>();
                        OnPresenceUpdate?.Invoke(id, online, displayName);
                    }
                }
                break;
            }
        }
    }

    private async Task AcceptLanWithPasswordAsync()
    {
        if (CurrentSession?.Role == SessionRole.Target)
            return;
        await LanAcceptAsync();
    }

    private bool TryHandlePasswordCheckRelay(string sessionId, string fromId, string data)
    {
        if (!data.StartsWith('{')) return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.TryGetProperty("__internal", out var internalProp) &&
                internalProp.GetString() == "password_check" &&
                root.TryGetProperty("hash", out var hashProp))
            {
                var hash = hashProp.GetString();
                if (PasswordHelper.VerifyHash(hash, _config.SessionPasswordHash))
                {
                    var fromName = _pendingIncoming.TryGetValue(sessionId, out var p) ? p.FromName : fromId;
                    _ = AcceptAsync(sessionId, fromId, fromName);
                }
                else
                {
                    _ = SendPasswordVerifyResultAsync(sessionId, false);
                }
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>Oturum E2E el sıkışmasını başlatır (her iki uç da çağırır).</summary>
    public void StartE2E(ActiveSession session)
    {
        CurrentSession = session;
        var channel = E2EContext.Replace();
        var pk = channel.PublicKey.ToArray();
        if (session.IsLanMode)
        {
            if (session.DirectClient != null)
                _ = session.DirectClient.SendRelayFrameAsync(RelayFrameType.Handshake, pk);
            else
                _ = App.LanServer.SendRelayFrameAsync(RelayFrameType.Handshake, pk);
            return;
        }

        _ = _relay.SendRelayFrameAsync(session.SessionId, session.PeerId, RelayFrameType.Handshake, pk);
    }

    private void StopScreenSender()
    {
        if (_screenSender != null)
        {
            _screenSender.Stop();
            _screenSender.Dispose();
            _screenSender = null;
        }
        E2EContext.Clear();
    }

    public void Dispose()
    {
        StopScreenSender();
        _relay.Dispose();
    }
}
