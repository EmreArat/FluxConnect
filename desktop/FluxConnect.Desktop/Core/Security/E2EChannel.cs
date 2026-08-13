using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FluxConnect.Desktop.Core.Network;

namespace FluxConnect.Desktop.Core.Security;

/// <summary>
/// Oturum başına ephemeral ECDH (P-256) + AES-256-GCM.
/// Relay ve LAN aynı anahtarı kullanır; sunucu paylaşılan sırrı göremez.
/// </summary>
public sealed class E2EChannel : IDisposable
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int PrefixSize = 4;
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("FluxConnect-E2E-v1");

    private readonly ECDiffieHellman _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _publicSpki;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _sendLock = new();

    private AesGcm? _sendGcm;
    private AesGcm? _recvGcm;
    private byte[]? _sendNoncePrefix;
    private long _sendCounter;
    private bool _disposed;

    public bool IsReady => _ready.Task.IsCompletedSuccessfully && _ready.Task.Result;
    public ReadOnlyMemory<byte> PublicKey => _publicSpki;

    public E2EChannel()
    {
        _publicSpki = _ecdh.PublicKey.ExportSubjectPublicKeyInfo();
    }

    public bool AcceptPeerPublicKey(ReadOnlySpan<byte> peerSpki)
    {
        if (IsReady || _disposed || peerSpki.Length < 32)
            return IsReady;

        try
        {
            using var peer = ECDiffieHellman.Create();
            peer.ImportSubjectPublicKeyInfo(peerSpki, out _);
            var secret = _ecdh.DeriveRawSecretAgreement(peer.PublicKey);

            var weAreLow = Compare(_publicSpki, peerSpki) < 0;
            var sendInfo = Encoding.UTF8.GetBytes(weAreLow ? "send-low" : "send-high");
            var recvInfo = Encoding.UTF8.GetBytes(weAreLow ? "send-high" : "send-low");

            var sendOkm = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, KeySize + PrefixSize, Salt, sendInfo);
            var recvOkm = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, KeySize + PrefixSize, Salt, recvInfo);
            CryptographicOperations.ZeroMemory(secret);

            _sendGcm = new AesGcm(sendOkm.AsSpan(0, KeySize), TagSize);
            _recvGcm = new AesGcm(recvOkm.AsSpan(0, KeySize), TagSize);
            _sendNoncePrefix = sendOkm[KeySize..(KeySize + PrefixSize)];
            CryptographicOperations.ZeroMemory(sendOkm);
            CryptographicOperations.ZeroMemory(recvOkm);
            _ready.TrySetResult(true);
            return true;
        }
        catch
        {
            _ready.TrySetResult(false);
            return false;
        }
    }

    public Task<bool> WaitReadyAsync(TimeSpan timeout) =>
        WaitReadyAsync(timeout, CancellationToken.None);

    public async Task<bool> WaitReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await _ready.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public byte[] Seal(ReadOnlySpan<byte> plaintext)
    {
        if (_sendGcm == null || _sendNoncePrefix == null)
            throw new InvalidOperationException("E2E kanalı hazır değil.");

        var nonce = new byte[NonceSize];
        _sendNoncePrefix.CopyTo(nonce.AsSpan(0, PrefixSize));
        long counter;
        lock (_sendLock)
            counter = ++_sendCounter;
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(PrefixSize), (ulong)counter);

        var output = new byte[NonceSize + plaintext.Length + TagSize];
        nonce.AsSpan().CopyTo(output.AsSpan());
        _sendGcm.Encrypt(
            nonce,
            plaintext,
            output.AsSpan(NonceSize, plaintext.Length),
            output.AsSpan(NonceSize + plaintext.Length, TagSize));
        return output;
    }

    public bool TryOpen(ReadOnlySpan<byte> sealedData, out byte[] plaintext)
    {
        plaintext = [];
        if (_recvGcm == null || sealedData.Length < NonceSize + TagSize)
            return false;

        var nonce = sealedData[..NonceSize];
        var cipherLen = sealedData.Length - NonceSize - TagSize;
        var cipher = sealedData.Slice(NonceSize, cipherLen);
        var tag = sealedData.Slice(NonceSize + cipherLen, TagSize);
        var output = new byte[cipherLen];
        try
        {
            _recvGcm.Decrypt(nonce, cipher, tag, output);
            plaintext = output;
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ready.TrySetResult(false);
        _sendGcm?.Dispose();
        _recvGcm?.Dispose();
        _ecdh.Dispose();
    }

    private static int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var len = Math.Min(a.Length, b.Length);
        var cmp = a[..len].SequenceCompareTo(b[..len]);
        return cmp != 0 ? cmp : a.Length.CompareTo(b.Length);
    }
}

/// <summary>Tek aktif oturumun E2E kanalı (uygulama aynı anda bir oturum tutar).</summary>
public static class E2EContext
{
    private static readonly object Gate = new();
    private static E2EChannel? _current;
    private static byte[]? _pendingPeerKey;

    public static E2EChannel? Current
    {
        get { lock (Gate) return _current; }
    }

    public static bool IsReady => Current?.IsReady == true;

    public static E2EChannel Replace()
    {
        lock (Gate)
        {
            _current?.Dispose();
            _current = new E2EChannel();
            if (_pendingPeerKey != null)
            {
                _current.AcceptPeerPublicKey(_pendingPeerKey);
                _pendingPeerKey = null;
            }
            return _current;
        }
    }

    public static void StashPeerKey(ReadOnlySpan<byte> peerSpki)
    {
        lock (Gate)
        {
            if (_current != null)
                _current.AcceptPeerPublicKey(peerSpki);
            else
                _pendingPeerKey = peerSpki.ToArray();
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            _current?.Dispose();
            _current = null;
            _pendingPeerKey = null;
        }
    }

    public static async Task<bool> WaitReadyAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return Current?.IsReady == true;

            var channel = Current;
            if (channel == null)
            {
                await Task.Delay(50);
                continue;
            }

            if (channel.IsReady) return true;
            return await channel.WaitReadyAsync(remaining);
        }
    }
}

/// <summary>Handshake dışındaki frame'leri AES-GCM ile sarar / açar.</summary>
public static class E2EFrame
{
    public static byte[] Protect(RelayFrameType type, ReadOnlySpan<byte> payload, out RelayFrameType wireType)
    {
        wireType = type;
        if (type == RelayFrameType.Handshake)
            return RelayFrameCodec.Pack(type, payload);

        var channel = E2EContext.Current;
        if (channel is { IsReady: true })
        {
            var inner = new byte[1 + payload.Length];
            inner[0] = (byte)type;
            payload.CopyTo(inner.AsSpan(1));
            wireType = RelayFrameType.Encrypted;
            return RelayFrameCodec.Pack(RelayFrameType.Encrypted, channel.Seal(inner));
        }

        // Kanal var ama el sıkışması bitmedi: düz metin sızmasın.
        if (channel != null)
            return [];

        return RelayFrameCodec.Pack(type, payload);
    }

    /// <returns>false: handshake tüketildi veya paket düşürüldü.</returns>
    public static bool TryUnwrap(RelayFrame incoming, out RelayFrame inner)
    {
        inner = incoming;
        if (incoming.Type == RelayFrameType.Handshake)
        {
            E2EContext.StashPeerKey(incoming.Payload.Span);
            return false;
        }

        if (incoming.Type != RelayFrameType.Encrypted)
        {
            if (E2EContext.IsReady)
                return false;
            return true;
        }

        var channel = E2EContext.Current;
        if (channel == null || !channel.TryOpen(incoming.Payload.Span, out var opened) || opened.Length < 1)
            return false;

        inner = new RelayFrame
        {
            Type = (RelayFrameType)opened[0],
            Payload = opened.AsMemory(1)
        };
        return true;
    }
}
