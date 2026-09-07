using System.Buffers.Binary;

namespace TravelEar.Core;

/// <summary>
/// Dissonance packet types, copied verbatim from Dissonance's <c>MessageTypes</c> (values confirmed
/// against the game's <c>DissonanceVoip.dll</c>).
/// </summary>
public enum DissonanceMessageType : byte
{
    ClientState = 1,
    VoiceData = 2,
    TextData = 3,
    HandshakeRequest = 4,
    HandshakeResponse = 5,
    ErrorWrongSession = 6,
    ServerRelayReliable = 7,
    ServerRelayUnreliable = 8,
    DeltaChannelState = 9,
    RemoveClient = 10,
    HandshakeP2P = 11,
}

/// <summary>Why <see cref="DissonanceFrame.TryParse"/> rejected a packet.</summary>
public enum DissonanceFrameError
{
    None = 0,
    /// <summary>Shorter than the fixed header.</summary>
    TooShort,
    /// <summary>First 16 bits are not <see cref="DissonanceFrame.Magic"/>.</summary>
    BadMagic,
    /// <summary>A valid Dissonance packet, but not <see cref="DissonanceMessageType.VoiceData"/>.</summary>
    NotVoiceData,
    /// <summary>The flags byte has its MSB clear; Dissonance always sets it.</summary>
    BadFlags,
    /// <summary>The channel list or payload runs past the end of the packet.</summary>
    Truncated,
    /// <summary>Bytes remain after the payload.</summary>
    TrailingBytes,
}

/// <summary>One entry of a VoiceData channel list: 16-bit channel bitfield then 16-bit channel id.</summary>
public readonly record struct DissonanceChannel(ushort Bitfield, ushort Id);

/// <summary>
/// A parsed Dissonance <c>VoiceData</c> packet: the exact bytes the game sends to peers
/// (Outbound Voice). Layout follows the documented network protocol
/// (https://placeholder-software.co.uk/dissonance/docs/Reference/Networking/Network-Protocol.html)
/// and Dissonance's <c>PacketWriter.WriteVoiceData</c>, big-endian throughout:
/// <code>
/// u16 magic 0x8BC7 | u8 type (=2) | u32 session | u16 sender | u8 flags (0x80 | channelSession)
/// | u16 sequence | u16 channelCount | { u16 bitfield, u16 id } * channelCount | u16 payloadLength | payload
/// </code>
/// </summary>
public readonly struct DissonanceFrame
{
    public const ushort Magic = 0x8BC7;

    /// <summary>Bytes before the channel list: magic, type, session, sender, flags, sequence, channel count.</summary>
    public const int FixedHeaderSize = 2 + 1 + 4 + 2 + 1 + 2 + 2;

    public uint SessionId { get; }
    public ushort SenderId { get; }
    /// <summary>The 7-bit wrapping counter that increments each time the sender restarts its channel session.</summary>
    public byte ChannelSession { get; }
    public ushort Sequence { get; }
    public IReadOnlyList<DissonanceChannel> Channels { get; }
    /// <summary>The Opus frame, as a slice of the packet passed to <see cref="TryParse"/> (no copy).</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    private DissonanceFrame(uint sessionId, ushort senderId, byte channelSession, ushort sequence,
        DissonanceChannel[] channels, ReadOnlyMemory<byte> payload)
    {
        SessionId = sessionId;
        SenderId = senderId;
        ChannelSession = channelSession;
        Sequence = sequence;
        Channels = channels;
        Payload = payload;
    }

    /// <summary>True if <paramref name="packet"/> starts with the Dissonance magic number.</summary>
    public static bool HasMagic(ReadOnlySpan<byte> packet) =>
        packet.Length >= 2 && BinaryPrimitives.ReadUInt16BigEndian(packet) == Magic;

    /// <summary>Reads the packet type without parsing the rest. False if too short or no magic.</summary>
    public static bool TryGetMessageType(ReadOnlySpan<byte> packet, out DissonanceMessageType type)
    {
        type = 0;
        if (packet.Length < 3 || !HasMagic(packet)) return false;
        type = (DissonanceMessageType)packet[2];
        return true;
    }

    // [impl->REQ-VOICE-OUTBOUND-TAP]
    /// <summary>
    /// Parses a complete VoiceData packet. Rejects anything that is not exactly one well-formed
    /// VoiceData frame so the tap never feeds garbage to the decoder.
    /// </summary>
    public static bool TryParse(ReadOnlyMemory<byte> packet, out DissonanceFrame frame, out DissonanceFrameError error)
    {
        frame = default;
        var span = packet.Span;

        if (span.Length >= 2 && !HasMagic(span))
        {
            error = DissonanceFrameError.BadMagic;
            return false;
        }
        if (span.Length < FixedHeaderSize)
        {
            error = DissonanceFrameError.TooShort;
            return false;
        }
        if ((DissonanceMessageType)span[2] != DissonanceMessageType.VoiceData)
        {
            error = DissonanceFrameError.NotVoiceData;
            return false;
        }

        var offset = 3;
        var session = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offset)); offset += 4;
        var sender = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset)); offset += 2;
        var flags = span[offset]; offset += 1;
        if ((flags & 0x80) == 0)
        {
            error = DissonanceFrameError.BadFlags;
            return false;
        }
        var sequence = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset)); offset += 2;
        var channelCount = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset)); offset += 2;

        if (span.Length < offset + channelCount * 4 + 2)
        {
            error = DissonanceFrameError.Truncated;
            return false;
        }
        var channels = channelCount == 0 ? Array.Empty<DissonanceChannel>() : new DissonanceChannel[channelCount];
        for (var i = 0; i < channelCount; i++)
        {
            var bitfield = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset)); offset += 2;
            var id = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset)); offset += 2;
            channels[i] = new DissonanceChannel(bitfield, id);
        }

        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset)); offset += 2;
        if (span.Length < offset + payloadLength)
        {
            error = DissonanceFrameError.Truncated;
            return false;
        }
        if (span.Length != offset + payloadLength)
        {
            error = DissonanceFrameError.TrailingBytes;
            return false;
        }

        frame = new DissonanceFrame(session, sender, (byte)(flags & 0x7F), sequence, channels,
            packet.Slice(offset, payloadLength));
        error = DissonanceFrameError.None;
        return true;
    }
}
