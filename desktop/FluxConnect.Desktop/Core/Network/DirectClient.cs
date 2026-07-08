using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace FluxConnect.Desktop.Core.Network;

/// <summary>
/// LAN modunda, hedef bilgisayarın gömülü sunucusuna doğrudan bağlanan istemci.
/// RelayClient'ın LAN karşılığı — Relay sunucusuna bağlanmak yerine
/// doğrudan hedefin IP'sine WebSocket ile bağlanır.
/// </summary>
public class DirectClient : IDisposable
{
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private bool _disposed;
    // Aynı anda tek gönderim için kilit
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    /// <summary>Sunucudan gelen mesaj (JSON)</summary>
    public event Action<JsonNode>? OnMessageReceived;

    /// <summary>Bağlantı kesildi</summary>
    public event Action? OnDisconnected;

    /// <summary>LAN üzerinden hedef bilgisayara doğrudan bağlan</summary>
    public async Task ConnectAsync(string ipAddress, int port = 9090)
    {
        _cts = new CancellationTokenSource();
        _ws = new ClientWebSocket();
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        var uri = new Uri($"ws://{ipAddress}:{port}");

        try
        {
            await _ws.ConnectAsync(uri, _cts.Token);

            File.AppendAllText("flux_debug.txt",
                $"[{DateTime.Now:HH:mm:ss}] [DirectClient] Bağlandı: {uri}\n");

            // Alım döngüsünü başlat
            _ = Task.Run(() => ReceiveLoop(_cts.Token));
        }
        catch (Exception ex)
        {
            File.AppendAllText("flux_debug.txt",
                $"[{DateTime.Now:HH:mm:ss}] [DirectClient] Bağlantı hatası: {ex.Message}\n");
            throw;
        }
    }

    /// <summary>Bağlantı isteği gönder</summary>
    public async Task SendConnectionRequest(string displayName, string password, string hardwareId, string machineId)
    {
        var msg = new JsonObject
        {
            ["type"] = "connect_request",
            ["display_name"] = displayName,
            ["password"] = password,
            ["hardware_id"] = hardwareId,
            ["machine_id"] = machineId
        };
        await SendAsync(msg.ToJsonString());
    }

    /// <summary>Relay verisi gönder (input komutları)</summary>
    public async Task SendRelayDataAsync(string data)
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
        var msg = new JsonObject
        {
            ["type"] = "relay",
            ["data"] = data
        };
        await SendAsync(msg.ToJsonString(), timeout);
    }

    public async Task SendAsync(string json, int timeoutMs = -1)
    {
        if (_ws?.State != WebSocketState.Open) return;

        bool acquired = await _sendLock.WaitAsync(timeoutMs);
        if (!acquired) return; // Ağ meşgul, çerçeveyi (frame) at (drop)

        try
        {
            if (_ws?.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(json);
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true,
                _cts?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            File.AppendAllText("flux_debug.txt",
                $"[{DateTime.Now:HH:mm:ss}] [DirectClient] Gönderme hatası: {ex.Message}\n");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[1024 * 1024]; // 1MB

        try
        {
            while (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct);
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                var text = Encoding.UTF8.GetString(ms.ToArray());

                try
                {
                    var msg = JsonNode.Parse(text);
                    if (msg != null)
                        OnMessageReceived?.Invoke(msg);
                }
                catch (JsonException ex)
                {
                    File.AppendAllText("flux_debug.txt",
                        $"[{DateTime.Now:HH:mm:ss}] [DirectClient] JSON hatası: {ex.Message}\n");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception ex)
        {
            File.AppendAllText("flux_debug.txt",
                $"[{DateTime.Now:HH:mm:ss}] [DirectClient] ReceiveLoop hatası: {ex.Message}\n");
        }
        finally
        {
            OnDisconnected?.Invoke();
        }
    }

    public async Task DisconnectAsync()
    {
        try
        {
            if (_ws?.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Kapatılıyor", CancellationToken.None);
        }
        catch { }
        finally
        {
            _cts?.Cancel();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _ws?.Dispose();
        _sendLock.Dispose();
    }
}
