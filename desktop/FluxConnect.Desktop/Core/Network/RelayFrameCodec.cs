using System.Buffers.Binary;
using System.Text;

namespace FluxConnect.Desktop.Core.Network;

/// <summary>Binary relay payload türleri (FC 10 frame içi).</summary>
public enum RelayFrameType : byte
{
    Screen = 1,
    Webcam = 2,
    Microphone = 3,
    SystemAudio = 4,
    Input = 5,
    Info = 6,
    Command = 7,
    LegacyText = 8,
    Handshake = 9,
    Encrypted = 10,
}

/// <summary>Decode edilmiş relay frame.</summary>
public readonly struct RelayFrame
{
    public RelayFrameType Type { get; init; }
    public ReadOnlyMemory<byte> Payload { get; init; }
}

/// <summary>
/// Legacy prefix protokolü ile binary frame arasında dönüşüm.
/// </summary>
public static class RelayFrameCodec
{
    private const byte FrameMagic0 = 0xFC;
    private const byte FrameMagic1 = 0x10;
    private const int HeaderSize = 7;

    public static bool IsBulk(RelayFrameType type) =>
        type is RelayFrameType.Screen or RelayFrameType.Webcam;

    public static bool TryPackFromLegacy(string data, out byte[] frameBytes)
    {
        frameBytes = Array.Empty<byte>();

        if (string.IsNullOrEmpty(data))
            return false;

        RelayFrameType type;
        byte[] payload;

        if (data.StartsWith("CAM:"))
        {
            type = RelayFrameType.Webcam;
            payload = Convert.FromBase64String(data[4..]);
        }
        else if (data.StartsWith("MIC:"))
        {
            type = RelayFrameType.Microphone;
            payload = Convert.FromBase64String(data[4..]);
        }
        else if (data.StartsWith("SYS:"))
        {
            type = RelayFrameType.SystemAudio;
            payload = Convert.FromBase64String(data[4..]);
        }
        else if (data.StartsWith("INP:"))
        {
            type = RelayFrameType.Input;
            payload = Convert.FromBase64String(data[4..]);
        }
        else if (data.StartsWith("INF:"))
        {
            type = RelayFrameType.Info;
            payload = Convert.FromBase64String(data[4..]);
        }
        else if (data.StartsWith("CMD:"))
        {
            type = RelayFrameType.Command;
            payload = Encoding.UTF8.GetBytes(data[4..]);
        }
        else if (data.StartsWith("FIL:") || data.StartsWith("FS:"))
        {
            type = RelayFrameType.LegacyText;
            payload = Encoding.UTF8.GetBytes(data);
        }
        else if (IsLikelyBase64Screen(data))
        {
            type = RelayFrameType.Screen;
            payload = Convert.FromBase64String(data);
        }
        else
        {
            return false;
        }

        frameBytes = Pack(type, payload);
        return true;
    }

    public static byte[] Pack(RelayFrameType type, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[HeaderSize + payload.Length];
        frame[0] = FrameMagic0;
        frame[1] = FrameMagic1;
        frame[2] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(3), (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    public static bool TryUnpack(ReadOnlySpan<byte> data, out RelayFrame frame)
    {
        frame = default;
        if (data.Length < HeaderSize || data[0] != FrameMagic0 || data[1] != FrameMagic1)
            return false;

        var type = (RelayFrameType)data[2];
        var length = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(3, 4));
        if (data.Length < HeaderSize + length)
            return false;

        frame = new RelayFrame
        {
            Type = type,
            Payload = data.Slice(HeaderSize, (int)length).ToArray()
        };
        return true;
    }

    public static string ToLegacyString(RelayFrame frame)
    {
        var payload = frame.Payload.Span;
        return frame.Type switch
        {
            RelayFrameType.Screen => Convert.ToBase64String(payload),
            RelayFrameType.Webcam => "CAM:" + Convert.ToBase64String(payload),
            RelayFrameType.Microphone => "MIC:" + Convert.ToBase64String(payload),
            RelayFrameType.SystemAudio => "SYS:" + Convert.ToBase64String(payload),
            RelayFrameType.Input => "INP:" + Convert.ToBase64String(payload),
            RelayFrameType.Info => "INF:" + Convert.ToBase64String(payload),
            RelayFrameType.Command => "CMD:" + Encoding.UTF8.GetString(payload),
            RelayFrameType.LegacyText => Encoding.UTF8.GetString(payload),
            RelayFrameType.Handshake => "E2E:HELLO:" + Convert.ToBase64String(payload),
            RelayFrameType.Encrypted => string.Empty,
            _ => Encoding.UTF8.GetString(payload),
        };
    }

    public static RelayFrameType? ClassifyLegacy(string data)
    {
        if (TryPackFromLegacy(data, out _))
        {
            if (data.StartsWith("CAM:")) return RelayFrameType.Webcam;
            if (data.StartsWith("MIC:")) return RelayFrameType.Microphone;
            if (data.StartsWith("SYS:")) return RelayFrameType.SystemAudio;
            if (data.StartsWith("INP:")) return RelayFrameType.Input;
            if (data.StartsWith("INF:")) return RelayFrameType.Info;
            if (data.StartsWith("CMD:")) return RelayFrameType.Command;
            if (data.StartsWith("FIL:") || data.StartsWith("FS:")) return RelayFrameType.LegacyText;
            return RelayFrameType.Screen;
        }
        return null;
    }

    private static bool IsLikelyBase64Screen(string data)
    {
        if (data.Length < 16 || data.StartsWith("CMD:") || data.StartsWith("FIL:") || data.StartsWith("FS:"))
            return false;

        foreach (var c in data)
        {
            if (char.IsLetterOrDigit(c) || c is '+' or '/' or '=')
                continue;
            return false;
        }
        return true;
    }
}

/// <summary>LAN / internet WebSocket binary zarfı.</summary>
public static class RelayWireCodec
{
    public const byte WireMagic0 = 0xFC;
    public const byte WireLanV1 = 0x01;
    public const byte WireRelayV1 = 0x02;

    public static byte[] PackLan(ReadOnlySpan<byte> frameBytes)
    {
        var wire = new byte[2 + frameBytes.Length];
        wire[0] = WireMagic0;
        wire[1] = WireLanV1;
        frameBytes.CopyTo(wire.AsSpan(2));
        return wire;
    }

    public static byte[] PackInternet(string sessionId, string targetId, ReadOnlySpan<byte> frameBytes)
    {
        var sessionBytes = Encoding.UTF8.GetBytes(sessionId);
        var targetBytes = Encoding.UTF8.GetBytes(targetId);
        var wire = new byte[2 + 4 + sessionBytes.Length + targetBytes.Length + frameBytes.Length];
        var offset = 0;
        wire[offset++] = WireMagic0;
        wire[offset++] = WireRelayV1;
        BinaryPrimitives.WriteUInt16LittleEndian(wire.AsSpan(offset), (ushort)sessionBytes.Length);
        offset += 2;
        sessionBytes.CopyTo(wire.AsSpan(offset));
        offset += sessionBytes.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(wire.AsSpan(offset), (ushort)targetBytes.Length);
        offset += 2;
        targetBytes.CopyTo(wire.AsSpan(offset));
        offset += targetBytes.Length;
        frameBytes.CopyTo(wire.AsSpan(offset));
        return wire;
    }

    public static byte[] PackInternetFromServer(string sessionId, string fromId, ReadOnlySpan<byte> frameBytes)
        => PackInternet(sessionId, fromId, frameBytes);

    public static bool TryUnpackLan(ReadOnlySpan<byte> wire, out RelayFrame frame)
    {
        frame = default;
        if (wire.Length < 2 || wire[0] != WireMagic0 || wire[1] != WireLanV1)
            return false;
        return RelayFrameCodec.TryUnpack(wire.Slice(2), out frame);
    }

    public static bool TryUnpackInternet(ReadOnlySpan<byte> wire, out string sessionId, out string peerId, out RelayFrame frame)
    {
        sessionId = string.Empty;
        peerId = string.Empty;
        frame = default;

        if (wire.Length < 6 || wire[0] != WireMagic0 || wire[1] != WireRelayV1)
            return false;

        var offset = 2;
        var sessionLen = BinaryPrimitives.ReadUInt16LittleEndian(wire.Slice(offset, 2));
        offset += 2;
        if (wire.Length < offset + sessionLen + 2)
            return false;

        sessionId = Encoding.UTF8.GetString(wire.Slice(offset, sessionLen));
        offset += sessionLen;

        var peerLen = BinaryPrimitives.ReadUInt16LittleEndian(wire.Slice(offset, 2));
        offset += 2;
        if (wire.Length < offset + peerLen)
            return false;

        peerId = Encoding.UTF8.GetString(wire.Slice(offset, peerLen));
        offset += peerLen;

        return RelayFrameCodec.TryUnpack(wire.Slice(offset), out frame);
    }
}
