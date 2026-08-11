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
    private readonly RelaySendPipeline _sendPipeline;

    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action<string>? OnError;
    public event Action<JsonObject>? OnMessageReceived;
    public event Action<string, string, RelayFrame>? OnRelayFrameReceived;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public RelayClient()
    {
        _sendPipeline = new RelaySendPipeline(SendJsonDirectAsync, SendBinaryDirectAsync);
    }

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
        catch (Exception)
        {
            OnError?.Invoke("Sadece Yerel Ağ (LAN) Modu - İnternet Relay Sunucusuna ulaşılamadı.");
            throw;
        }
    }

    public Task SendAsync(object message, int timeoutMs = -1, CancellationToken ct = default)
    {
        if (_ws?.State != WebSocketState.Open)
            return Task.CompletedTask;

        var json = JsonSerializer.Serialize(message);
        _sendPipeline.EnqueueRealtime(json);
        return Task.CompletedTask;
    }

    public Task RegisterAsync(string machineId, string displayName, string hardwareId, bool hasSessionPassword) =>
        SendAsync(new { type = "register", id = machineId, display_name = displayName, hardware_id = hardwareId, has_session_password = hasSessionPassword });

    public Task RequestConnectAsync(string targetId) =>
        SendAsync(new { type = "connect_request", target_id = targetId });

    public Task RespondToConnectionAsync(string sessionId, bool accepted) =>
        SendAsync(new { type = "connect_response", session_id = sessionId, accepted });

    public Task SendPasswordAttemptAsync(string sessionId, string passwordHash) =>
        SendAsync(new { type = "password_attempt", session_id = sessionId, password_hash = passwordHash });

    public Task SendPasswordVerifyResultAsync(string sessionId, bool success) =>
        SendAsync(new { type = "password_verify_result", session_id = sessionId, success });

    public Task SendRelayDataAsync(string sessionId, string targetId, string data)
    {
        if (_ws?.State != WebSocketState.Open)
            return Task.CompletedTask;

        if (RelayFrameCodec.TryPackFromLegacy(data, out var frameBytes))
        {
            var type = RelayFrameCodec.ClassifyLegacy(data) ?? RelayFrameType.LegacyText;
            var wire = RelayWireCodec.PackInternet(sessionId, targetId, frameBytes);
            _sendPipeline.EnqueueRelayWire(type, wire);
            return Task.CompletedTask;
        }

        var json = JsonSerializer.Serialize(new { type = "relay", session_id = sessionId, target_id = targetId, data });
        _sendPipeline.EnqueueRealtime(json);
        return Task.CompletedTask;
    }

    public Task SendRelayFrameAsync(string sessionId, string targetId, RelayFrameType type, ReadOnlySpan<byte> payload)
    {
        if (_ws?.State != WebSocketState.Open)
            return Task.CompletedTask;

        var frameBytes = RelayFrameCodec.Pack(type, payload);
        var wire = RelayWireCodec.PackInternet(sessionId, targetId, frameBytes);
        _sendPipeline.EnqueueRelayWire(type, wire);
        return Task.CompletedTask;
    }

    public Task PingAsync() => SendAsync(new { type = "ping" });

    public Task SubscribePresenceAsync(string[] ids) =>
        SendAsync(new { type = "presence_subscribe", ids });

    public Task QueryPresenceAsync(string[] ids) =>
        SendAsync(new { type = "presence_query", ids });

    private async Task SendJsonDirectAsync(string json)
    {
        if (_ws?.State != WebSocketState.Open) return;

        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText("flux_debug.txt",
                $"[{DateTime.Now:HH:mm:ss}] [RelayClient] JSON gönderme hatası: {ex.Message}\n");
        }
    }

    private async Task SendBinaryDirectAsync(byte[] data)
    {
        if (_ws?.State != WebSocketState.Open) return;

        try
        {
            await _ws.SendAsync(data, WebSocketMessageType.Binary, true, _cts?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText("flux_debug.txt",
                $"[{DateTime.Now:HH:mm:ss}] [RelayClient] Binary gönderme hatası: {ex.Message}\n");
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[1024 * 1024];

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

                var messageBytes = ms.ToArray();
                if (messageBytes.Length == 0) continue;

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    if (RelayWireCodec.TryUnpackInternet(messageBytes, out var sessionId, out var fromId, out var frame))
                        OnRelayFrameReceived?.Invoke(sessionId, fromId, frame);
                    continue;
                }

                var json = Encoding.UTF8.GetString(messageBytes);
                try
                {
                    if (JsonNode.Parse(json) is JsonObject obj)
                        OnMessageReceived?.Invoke(obj);
                }
                catch (Exception parseEx)
                {
                    OnError?.Invoke($"JSON Parse hatası ({messageBytes.Length} byte): {parseEx.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OnError?.Invoke($"Alma döngüsü hatası: {ex.Message}");
        }
        finally
        {
            OnDisconnected?.Invoke();
        }
    }

    public async Task DisconnectAsync()
    {
        _cts?.Cancel();
        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Kullanıcı kapattı", CancellationToken.None);
            }
            catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _ws?.Dispose();
        _sendPipeline.Dispose();
    }
}
