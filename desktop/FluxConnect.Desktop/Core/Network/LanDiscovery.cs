using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluxConnect.Desktop.Core.Hardware;

namespace FluxConnect.Desktop.Core.Network;

public sealed record LanDiscoveryResult(string Ip, string HardwareId, string DisplayName, string MachineId);

/// <summary>
/// Yerel ağda MachineGuid ile FluxConnect bilgisayarlarını bulur.
/// UDP 9091 portu üzerinden yayın (broadcast) kullanır.
/// </summary>
public static class LanDiscovery
{
    public const int DiscoveryPort = 9091;

    /// <summary>Hedef makine kimliğini yerel ağda arar.</summary>
    public static async Task<LanDiscoveryResult?> FindAsync(
        string targetHardwareId,
        int timeoutMs = 3000,
        CancellationToken cancellationToken = default)
    {
        var normalizedTarget = HardwareIdProvider.ExtractHardwareId(targetHardwareId);
        if (normalizedTarget.Length != 32) return null;

        using var udp = new UdpClient(0);
        udp.EnableBroadcast = true;

        var requestJson = JsonSerializer.Serialize(new
        {
            type = "discover",
            target_hardware_id = normalizedTarget
        });
        var requestBytes = Encoding.UTF8.GetBytes(requestJson);

        await BroadcastDiscoverAsync(udp, requestBytes);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);

        try
        {
            while (!timeoutCts.Token.IsCancellationRequested)
            {
                var receiveTask = udp.ReceiveAsync(timeoutCts.Token);
                var result = await receiveTask;

                var response = ParseResponse(result.Buffer, result.RemoteEndPoint);
                if (response == null) continue;

                if (HardwareIdProvider.ExtractHardwareId(response.HardwareId) == normalizedTarget)
                    return response;
            }
        }
        catch (OperationCanceledException) { }

        return null;
    }

    private static async Task BroadcastDiscoverAsync(UdpClient udp, byte[] requestBytes)
    {
        var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);
        await udp.SendAsync(requestBytes, broadcastEndpoint);

        var subnetBroadcast = GetSubnetBroadcast(GetLocalIp());
        if (subnetBroadcast != null)
            await udp.SendAsync(requestBytes, new IPEndPoint(subnetBroadcast, DiscoveryPort));
    }

    private static LanDiscoveryResult? ParseResponse(byte[] buffer, IPEndPoint remoteEndpoint)
    {
        try
        {
            var json = Encoding.UTF8.GetString(buffer);
            var msg = JsonNode.Parse(json);
            if (msg?["type"]?.GetValue<string>() != "discover_response") return null;

            var hardwareId = msg["hardware_id"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(hardwareId)) return null;

            var ip = msg["ip"]?.GetValue<string>();
            if (string.IsNullOrEmpty(ip))
                ip = remoteEndpoint.Address.ToString();

            return new LanDiscoveryResult(
                ip,
                hardwareId,
                msg["display_name"]?.GetValue<string>() ?? "",
                msg["machine_id"]?.GetValue<string>() ?? "");
        }
        catch
        {
            return null;
        }
    }

    private static string GetLocalIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            var endpoint = socket.LocalEndPoint as IPEndPoint;
            return endpoint?.Address.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    private static IPAddress? GetSubnetBroadcast(string localIp)
    {
        var parts = localIp.Split('.');
        if (parts.Length != 4) return null;
        if (!parts.All(p => int.TryParse(p, out _))) return null;
        return IPAddress.Parse($"{parts[0]}.{parts[1]}.{parts[2]}.255");
    }
}

/// <summary>Gelen keşif isteklerine yanıt verir — uygulama açıkken arka planda dinler.</summary>
public sealed class LanDiscoveryHost : IDisposable
{
    private readonly string _hardwareId;
    private readonly string _displayName;
    private readonly string _machineId;
    private readonly Func<string> _getLocalIp;

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public LanDiscoveryHost(string hardwareId, string displayName, string machineId, Func<string> getLocalIp)
    {
        _hardwareId = hardwareId;
        _displayName = displayName;
        _machineId = machineId;
        _getLocalIp = getLocalIp;
    }

    public void Start()
    {
        if (_udp != null) return;

        _cts = new CancellationTokenSource();
        _udp = new UdpClient(LanDiscovery.DiscoveryPort);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        File.AppendAllText("flux_debug.txt",
            $"[{DateTime.Now:HH:mm:ss}] [LanDiscovery] Yanıtlayıcı başlatıldı: Port {LanDiscovery.DiscoveryPort}\n");

        _ = Task.Run(() => ListenLoop(_cts.Token));
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udp != null)
        {
            try
            {
                var result = await _udp.ReceiveAsync(ct);
                var sender = result.RemoteEndPoint;
                var request = ParseRequest(result.Buffer);
                if (request == null) continue;

                if (!string.IsNullOrEmpty(request.TargetHardwareId) &&
                    HardwareIdProvider.ExtractHardwareId(request.TargetHardwareId) !=
                    HardwareIdProvider.ExtractHardwareId(_hardwareId))
                    continue;

                var response = JsonSerializer.Serialize(new
                {
                    type = "discover_response",
                    hardware_id = _hardwareId,
                    display_name = _displayName,
                    machine_id = _machineId,
                    ip = _getLocalIp()
                });

                var responseBytes = Encoding.UTF8.GetBytes(response);
                await _udp.SendAsync(responseBytes, sender);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                File.AppendAllText("flux_debug.txt",
                    $"[{DateTime.Now:HH:mm:ss}] [LanDiscovery] Dinleme hatası: {ex.Message}\n");
            }
        }
    }

    private static DiscoverRequest? ParseRequest(byte[] buffer)
    {
        try
        {
            var json = Encoding.UTF8.GetString(buffer);
            var msg = JsonNode.Parse(json);
            if (msg?["type"]?.GetValue<string>() != "discover") return null;

            return new DiscoverRequest(msg["target_hardware_id"]?.GetValue<string>() ?? "");
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _udp?.Dispose();
        _cts?.Dispose();
    }

    private sealed record DiscoverRequest(string TargetHardwareId);
}
