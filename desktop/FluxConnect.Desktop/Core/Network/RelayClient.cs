using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluxConnect.Desktop.Core.Network;

/// <summary>
/// Relay sunucuya WebSocket bağlantısı kurar ve mesajları yönlendirir.
/// </summary>
public class RelayClient : IDisposable
{
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private bool _disposed;
    // WebSocket aynı anda yalnızca bir SendAsync destekler — sıraya al
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // ---- Olaylar ----
    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action<string>? OnError;
    public event Action<JsonObject>? OnMessageReceived;

    public bool IsConnected =>
        _ws?.State == WebSocketState.Open;

    // ----------------------------------------------------------------
    // Bağlan
    // ----------------------------------------------------------------
    public async Task ConnectAsync(string relayUrl, CancellationToken cancellationToken = default)
    {
        if (IsConnected) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ws = new ClientWebSocket();

        try
        {
            await _ws.ConnectAsync(new Uri(relayUrl), _cts.Token);
            OnConnected?.Invoke();
            _ = ReceiveLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            // Kullanıcıyı korkutmamak için standart soket hatası yerine daha dostça bir mesaj
            OnError?.Invoke($"Sadece Yerel Ağ (LAN) Modu - İnternet Relay Sunucusuna ulaşılamadı.");
            throw;
        }
    }

    // ----------------------------------------------------------------
    // Mesaj Gönder
    // ----------------------------------------------------------------
    public async Task SendAsync(object message, int timeoutMs = -1, CancellationToken ct = default)
    {
        if (_ws?.State != WebSocketState.Open)
            return; // Hata fırlatmak yerine dönelim ki sürekli kare atan yerlerde kilitlenmesin.

        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);

        // Frame düşürme mantığı (Eğer timeout 0 verildiyse ve meşgulse beklemeden çıkar)
        bool acquired = await _sendLock.WaitAsync(timeoutMs, ct);
        if (!acquired) return; // Ağ meşgul, önceki verinin gitmesini bekliyor, bu veriyi at (drop).

        try
        {
            await _ws.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                ct);
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText("flux_debug.txt",
                $"[{DateTime.Now:HH:mm:ss}] [RelayClient] Gönderme hatası: {ex.Message}\n");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // ----------------------------------------------------------------
    // Kayıt ol
    // ----------------------------------------------------------------
    public Task RegisterAsync(string machineId, string displayName, string hardwareId) =>
        SendAsync(new { type = "register", id = machineId, display_name = displayName, hardware_id = hardwareId });

    // ----------------------------------------------------------------
    // Bağlantı isteği gönder
    // ----------------------------------------------------------------
    public Task RequestConnectAsync(string targetId) =>
        SendAsync(new { type = "connect_request", target_id = targetId });

    // ----------------------------------------------------------------
    // Bağlantı yanıtı gönder
    // ----------------------------------------------------------------
    public Task RespondToConnectionAsync(string sessionId, bool accepted) =>
        SendAsync(new { type = "connect_response", session_id = sessionId, accepted });

    // ----------------------------------------------------------------
    // E2EE şifreli veri gönder
    // ----------------------------------------------------------------
    // Eğer webcam veya ekran karesi gibi yoğun bir veriyse, timeoutMs=0 verilir.
    // Ses veya diğer veriler için timeout -1.
    public async Task SendRelayDataAsync(string sessionId, string targetId, string data)
    {
        int timeout = 1000;
        if (data.StartsWith("CAM:") || 
            (!data.StartsWith("MIC:") && !data.StartsWith("SYS:") && !data.StartsWith("INP:") && !data.StartsWith("INF:") && !data.StartsWith("CMD:") && !data.StartsWith("FIL:") && !data.StartsWith("FS:")))
        {
            timeout = 0; // Havadaki görüntü meşgulse bekleme, drop et!
        }
        else if (data.StartsWith("MIC:") || data.StartsWith("SYS:"))
        {
            timeout = 150; // Seste birikme olmasın
        }
        
        await SendAsync(new { type = "relay", session_id = sessionId, target_id = targetId, data = data }, timeout);
    }

    // ----------------------------------------------------------------
    // Ping
    // ----------------------------------------------------------------
    public Task PingAsync() => SendAsync(new { type = "ping" });

    // ----------------------------------------------------------------
    // Presence (Çevrimiçi Durum Takibi)
    // ----------------------------------------------------------------
    public Task SubscribePresenceAsync(string[] ids) =>
        SendAsync(new { type = "presence_subscribe", ids });

    public Task QueryPresenceAsync(string[] ids) =>
        SendAsync(new { type = "presence_query", ids });

    // ----------------------------------------------------------------
    // Alma döngüsü
    // ----------------------------------------------------------------
    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[1024 * 1024]; // 1 MB tampon boyutu (Ekran görüntüleri için büyük olmalı)

        try
        {
            while (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new System.IO.MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Kapatılıyor", ct);
                        OnDisconnected?.Invoke();
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                // Tek bir tam mesaj ms içinde birikti
                var jsonBytes = ms.ToArray();
                if (jsonBytes.Length == 0) continue;

                var json = Encoding.UTF8.GetString(jsonBytes);
                try
                {
                    var node = JsonNode.Parse(json);
                    if (node is JsonObject obj)
                    {
                        OnMessageReceived?.Invoke(obj);
                    }
                }
                catch (Exception parseEx)
                {
                    OnError?.Invoke($"JSON Parse hatası ({jsonBytes.Length} byte): {parseEx.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Beklenen iptal
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Alma döngüsü hatası: {ex.Message}");
        }
        finally
        {
            OnDisconnected?.Invoke();
        }
    }

    // ----------------------------------------------------------------
    // Bağlantıyı kes
    // ----------------------------------------------------------------
    public async Task DisconnectAsync()
    {
        _cts?.Cancel();
        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                await _ws.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Kullanıcı kapattı",
                    CancellationToken.None);
            }
            catch { /* Sessizce kapat */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _ws?.Dispose();
        _sendLock.Dispose();
    }
}
