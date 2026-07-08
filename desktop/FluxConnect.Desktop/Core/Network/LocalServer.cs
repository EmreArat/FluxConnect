using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace FluxConnect.Desktop.Core.Network;

/// <summary>
/// Her FluxConnect instance'ına gömülü mini WebSocket sunucusu.
/// LAN modunda, uzak bilgisayar doğrudan bu sunucuya bağlanır.
/// Relay sunucusuna gerek kalmaz.
/// </summary>
public class LocalServer : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private WebSocket? _activeClient;
    private readonly int _port;
    private bool _disposed;
    // Aynı anda tek gönderim için kilit
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public int Port => _port;
    public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;
    public bool HasClient => _activeClient?.State == WebSocketState.Open;

    /// <summary>Gelen bağlantı isteği: (peerName, password, hardwareId, machineId) → UI'a yönlendirilir</summary>
    public event Action<string, string, string, string>? OnConnectionRequest;

    /// <summary>Gelen relay verisi (ekran/input): (data)</summary>
    public event Action<string>? OnDataReceived;

    /// <summary>İstemci bağlantısı kesildi</summary>
    public event Action? OnClientDisconnected;

    public LocalServer(int port = 9090)
    {
        _port = port;
    }

    public string GetLocalIpAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530); // Aslında bağlanmaz, sadece route tablosuna bakar
            var endpoint = socket.LocalEndPoint as IPEndPoint;
            return endpoint?.Address.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    public void Start()
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();

        File.AppendAllText("flux_debug.txt",
            $"[{DateTime.Now:HH:mm:ss}] [LocalServer] Başlatıldı: Port {_port}, IP: {GetLocalIpAddress()}\n");

        Task.Run(() => AcceptLoop(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _activeClient?.Dispose();
        _activeClient = null;
        _listener?.Stop();
        _listener = null;
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var tcpClient = await _listener!.AcceptTcpClientAsync(ct);
                _ = HandleClient(tcpClient, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                File.AppendAllText("flux_debug.txt",
                    $"[{DateTime.Now:HH:mm:ss}] [LocalServer] Accept hatası: {ex.Message}\n");
            }
        }
    }

    private async Task HandleClient(TcpClient tcpClient, CancellationToken ct)
    {
        try
        {
            var stream = tcpClient.GetStream();

            // 1. HTTP WebSocket Upgrade isteğini oku
            var request = await ReadHttpRequest(stream, ct);
            if (request == null || !request.Contains("Upgrade: websocket", StringComparison.OrdinalIgnoreCase))
            {
                tcpClient.Close();
                return;
            }

            // 2. WebSocket Handshake yanıtı gönder
            var wsKey = ExtractWebSocketKey(request);
            if (wsKey == null) { tcpClient.Close(); return; }

            var acceptKey = ComputeAcceptKey(wsKey);
            var response = "HTTP/1.1 101 Switching Protocols\r\n"
                         + "Upgrade: websocket\r\n"
                         + "Connection: Upgrade\r\n"
                         + $"Sec-WebSocket-Accept: {acceptKey}\r\n\r\n";

            await stream.WriteAsync(Encoding.UTF8.GetBytes(response), ct);

            // 3. WebSocket oluştur
            var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
            {
                IsServer = true,
                KeepAliveInterval = TimeSpan.FromSeconds(30)
            });

            // Önceki istemciyi kapat
            _activeClient?.Dispose();
            _activeClient = ws;

            File.AppendAllText("flux_debug.txt",
                $"[{DateTime.Now:HH:mm:ss}] [LocalServer] İstemci bağlandı: {tcpClient.Client.RemoteEndPoint}\n");

            // 4. Mesaj döngüsü
            await ReceiveLoop(ws, ct);
        }
        catch (Exception ex)
        {
            File.AppendAllText("flux_debug.txt",
                $"[{DateTime.Now:HH:mm:ss}] [LocalServer] İstemci hatası: {ex.Message}\n");
        }
        finally
        {
            tcpClient.Close();
            OnClientDisconnected?.Invoke();
        }
    }

    private async Task ReceiveLoop(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[1024 * 1024]; // 1MB

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            try
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, ct);
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                var text = Encoding.UTF8.GetString(ms.ToArray());
                HandleMessage(text);
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException) { break; }
            catch (Exception ex)
            {
                File.AppendAllText("flux_debug.txt",
                    $"[{DateTime.Now:HH:mm:ss}] [LocalServer] Mesaj hatası: {ex.Message}\n");
            }
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            var msg = JsonNode.Parse(json);
            if (msg == null) return;

            var type = msg["type"]?.GetValue<string>() ?? "";

            switch (type)
            {
                case "connect_request":
                    var peerName = msg["display_name"]?.GetValue<string>() ?? "Bilinmiyor";
                    var password = msg["password"]?.GetValue<string>() ?? "";
                    var hardwareId = msg["hardware_id"]?.GetValue<string>() ?? "";
                    var machineId = msg["machine_id"]?.GetValue<string>() ?? "";
                    OnConnectionRequest?.Invoke(peerName, password, hardwareId, machineId);
                    break;

                case "relay":
                    var data = msg["data"]?.GetValue<string>() ?? "";
                    OnDataReceived?.Invoke(data);
                    break;
            }
        }
        catch (Exception ex)
        {
            File.AppendAllText("flux_debug.txt",
                $"[{DateTime.Now:HH:mm:ss}] [LocalServer] Parse hatası: {ex.Message}\n");
        }
    }

    /// <summary>LAN istemcisine mesaj gönder</summary>
    public async Task SendAsync(string json, int timeoutMs = -1)
    {
        if (_activeClient?.State != WebSocketState.Open) return;

        bool acquired = await _sendLock.WaitAsync(timeoutMs);
        if (!acquired) return; // Ağ meşgul, çerçeveyi düşür.

        try
        {
            if (_activeClient?.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(json);
            await _activeClient.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            File.AppendAllText("flux_debug.txt",
                $"[{DateTime.Now:HH:mm:ss}] [LocalServer] Gönderme hatası: {ex.Message}\n");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Bağlantı kabul/ret yanıtı gönder</summary>
    public async Task RespondToConnection(bool accepted, string displayName, string hardwareId, string machineId)
    {
        var msg = new JsonObject
        {
            ["type"] = accepted ? "connect_accepted" : "connect_rejected",
            ["display_name"] = displayName,
            ["hardware_id"] = hardwareId,
            ["machine_id"] = machineId
        };
        await SendAsync(msg.ToJsonString());
    }

    /// <summary>Relay verisi gönder (ekran karesi)</summary>
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
            timeout = 150; // Ses paketlerinin ThreadPool kuyruğunda yığılıp gecikme yapmasını önle
        }
        var msg = new JsonObject
        {
            ["type"] = "relay",
            ["data"] = data
        };
        await SendAsync(msg.ToJsonString(), timeout);
    }

    // ---- HTTP / WebSocket Yardımcıları ----

    private static async Task<string?> ReadHttpRequest(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var sb = new StringBuilder();
        stream.ReadTimeout = 5000;

        try
        {
            int bytesRead = await stream.ReadAsync(buffer, ct);
            if (bytesRead == 0) return null;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
            return sb.ToString();
        }
        catch { return null; }
    }

    private static string? ExtractWebSocketKey(string request)
    {
        foreach (var line in request.Split('\n'))
        {
            if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                return line.Split(':')[1].Trim();
        }
        return null;
    }

    private static string ComputeAcceptKey(string key)
    {
        var combined = key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(combined));
        return Convert.ToBase64String(hash);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _sendLock.Dispose();
    }
}
