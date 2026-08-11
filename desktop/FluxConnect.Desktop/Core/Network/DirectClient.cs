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
/// LAN modunda hedef bilgisayarın gömülü sunucusuna doğrudan bağlanan istemci.
/// </summary>
public class DirectClient : IDisposable
{
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private readonly RelaySendPipeline _sendPipeline;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public DirectClient()
    {
        _sendPipeline = new RelaySendPipeline(SendJsonDirectAsync, SendBinaryDirectAsync);
    }

    public event Action<JsonNode>? OnMessageReceived;
    public event Action? OnDisconnected;

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

            _ = Task.Run(() => ReceiveLoop(_cts.Token));
        }
        catch (Exception ex)
        {
            File.AppendAllText("flux_debug.txt",
                $"[{DateTime.Now:HH:mm:ss}] [DirectClient] Bağlantı hatası: {ex.Message}\n");
            throw;
        }
    }

    public async Task SendConnectionRequest(string displayName, string passwordHash, string hardwareId, string machineId)
    {
        var msg = new JsonObject
        {
            ["type"] = "connect_request",
            ["display_name"] = displayName,
            ["password_hash"] = passwordHash,
            ["hardware_id"] = hardwareId,
            ["machine_id"] = machineId
        };
        await SendAsync(msg.ToJsonString());
    }

    public Task SendPasswordAttemptAsync(string passwordHash)
    {
        var msg = new JsonObject
        {
            ["type"] = "password_attempt",
            ["password_hash"] = passwordHash
        };
        return SendAsync(msg.ToJsonString());
    }

    public Task SendRelayDataAsync(string data)
    {
        if (_ws?.State != WebSocketState.Open)
            return Task.CompletedTask;

        if (RelayFrameCodec.TryPackFromLegacy(data, out var frameBytes))
        {
            var type = RelayFrameCodec.ClassifyLegacy(data) ?? RelayFrameType.LegacyText;
            var wire = RelayWireCodec.PackLan(frameBytes);
            _sendPipeline.EnqueueRelayWire(type, wire);
            return Task.CompletedTask;
        }

        var msg = new JsonObject { ["type"] = "relay", ["data"] = data };
        _sendPipeline.EnqueueRelayPayload(data, msg.ToJsonString());
        return Task.CompletedTask;
    }

    public Task SendRelayFrameAsync(RelayFrameType type, ReadOnlySpan<byte> payload)
    {
        if (_ws?.State != WebSocketState.Open)
            return Task.CompletedTask;

        var frameBytes = RelayFrameCodec.Pack(type, payload);
        var wire = RelayWireCodec.PackLan(frameBytes);
        _sendPipeline.EnqueueRelayWire(type, wire);
        return Task.CompletedTask;
    }

    public Task SendAsync(string json, int timeoutMs = -1)
    {
        if (_ws?.State != WebSocketState.Open)
            return Task.CompletedTask;

        _sendPipeline.EnqueueRealtime(json);
        return Task.CompletedTask;
    }

    private async Task SendJsonDirectAsync(string json)
    {
        if (_ws?.State != WebSocketState.Open) return;

        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true,
                _cts?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            File.AppendAllText("flux_debug.txt",
                $"[{DateTime.Now:HH:mm:ss}] [DirectClient] Gönderme hatası: {ex.Message}\n");
        }
    }

    private async Task SendBinaryDirectAsync(byte[] data)
    {
        if (_ws?.State != WebSocketState.Open) return;

        try
        {
            await _ws.SendAsync(data, WebSocketMessageType.Binary, true,
                _cts?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            File.AppendAllText("flux_debug.txt",
                $"[{DateTime.Now:HH:mm:ss}] [DirectClient] Binary gönderme hatası: {ex.Message}\n");
        }
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[1024 * 1024];

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

                var messageBytes = ms.ToArray();

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    if (RelayWireCodec.TryUnpackLan(messageBytes, out var frame))
                    {
                        var legacy = RelayFrameCodec.ToLegacyString(frame);
                        var synthetic = new JsonObject { ["type"] = "relay", ["data"] = legacy };
                        OnMessageReceived?.Invoke(synthetic);
                    }
                    continue;
                }

                var text = Encoding.UTF8.GetString(messageBytes);
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
        _sendPipeline.Dispose();
    }
}
