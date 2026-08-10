using System.Collections.Concurrent;

namespace FluxConnect.Desktop.Core.Network;

/// <summary>
/// Tek WebSocket üzerinde öncelikli gönderim: ses/input anında, ekran/webcam en güncel kare.
/// Binary wire mesajları ve JSON kontrol mesajlarını destekler.
/// </summary>
public sealed class RelaySendPipeline : IDisposable
{
    private readonly ConcurrentQueue<string> _realtimeJson = new();
    private readonly ConcurrentQueue<byte[]> _realtimeBinary = new();
    private readonly object _bulkLock = new();
    private byte[]? _pendingScreenWire;
    private byte[]? _pendingCamWire;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<string, Task> _sendJsonAsync;
    private readonly Func<byte[], Task> _sendBinaryAsync;
    private readonly Task _worker;
    private bool _disposed;

    public RelaySendPipeline(Func<string, Task> sendJsonAsync, Func<byte[], Task> sendBinaryAsync)
    {
        _sendJsonAsync = sendJsonAsync;
        _sendBinaryAsync = sendBinaryAsync;
        _worker = Task.Run(WorkerLoopAsync);
    }

    public void EnqueueRealtime(string json)
    {
        if (_disposed) return;
        _realtimeQueueEnqueueJson(json);
        Signal();
    }

    public void EnqueueRelayPayload(string data, string json)
    {
        if (_disposed) return;

        if (RelayFrameCodec.TryPackFromLegacy(data, out var frameBytes))
        {
            EnqueueRelayBinary(RelayFrameCodec.ClassifyLegacy(data) ?? RelayFrameType.LegacyText, frameBytes, null);
            return;
        }

        _realtimeQueueEnqueueJson(json);
        Signal();
    }

    public void EnqueueRelayBinary(RelayFrameType type, byte[] frameBytes, Func<byte[], byte[]>? wireFactory)
    {
        if (_disposed) return;

        var wire = wireFactory?.Invoke(frameBytes) ?? RelayWireCodec.PackLan(frameBytes);

        if (RelayFrameCodec.IsBulk(type))
        {
            lock (_bulkLock)
            {
                if (type == RelayFrameType.Screen)
                    _pendingScreenWire = wire;
                else
                    _pendingCamWire = wire;
            }
        }
        else
        {
            _realtimeBinary.Enqueue(wire);
        }

        Signal();
    }

    public void EnqueueRelayWire(RelayFrameType type, byte[] wireBytes)
    {
        if (_disposed) return;

        if (RelayFrameCodec.IsBulk(type))
        {
            lock (_bulkLock)
            {
                if (type == RelayFrameType.Screen)
                    _pendingScreenWire = wireBytes;
                else
                    _pendingCamWire = wireBytes;
            }
        }
        else
        {
            _realtimeBinary.Enqueue(wireBytes);
        }

        Signal();
    }

    public void ClearBulk()
    {
        lock (_bulkLock)
        {
            _pendingScreenWire = null;
            _pendingCamWire = null;
        }
    }

    private void _realtimeQueueEnqueueJson(string json) => _realtimeJson.Enqueue(json);

    private void Signal()
    {
        try { _wake.Release(); }
        catch (ObjectDisposedException) { }
        catch (SemaphoreFullException) { }
    }

    private async Task WorkerLoopAsync()
    {
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _wake.WaitAsync(ct);
                await DrainAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            while (_realtimeJson.TryDequeue(out var json))
                await _sendJsonAsync(json);

            while (_realtimeBinary.TryDequeue(out var binary))
                await _sendBinaryAsync(binary);

            byte[]? screenWire;
            byte[]? camWire;
            lock (_bulkLock)
            {
                screenWire = _pendingScreenWire;
                camWire = _pendingCamWire;
                _pendingScreenWire = null;
                _pendingCamWire = null;
            }

            if (screenWire != null)
                await _sendBinaryAsync(screenWire);
            if (camWire != null)
                await _sendBinaryAsync(camWire);

            if (_realtimeJson.IsEmpty && _realtimeBinary.IsEmpty && !HasPendingBulk())
                break;

            Signal();
        }
    }

    private bool HasPendingBulk()
    {
        lock (_bulkLock)
            return _pendingScreenWire != null || _pendingCamWire != null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _wake.Dispose();
        _cts.Dispose();
    }
}
