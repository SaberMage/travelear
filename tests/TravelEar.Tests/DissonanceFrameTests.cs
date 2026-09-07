using TravelEar.Core;
using Xunit;

namespace TravelEar.Tests;

public class DissonanceFrameTests
{
    /// <summary>Big-endian packet builder mirroring Dissonance's PacketWriter, per the documented protocol.</summary>
    private sealed class PacketBuilder
    {
        private readonly List<byte> _bytes = new();
        public PacketBuilder U8(byte b) { _bytes.Add(b); return this; }
        public PacketBuilder U16(ushort v) { _bytes.Add((byte)(v >> 8)); _bytes.Add((byte)v); return this; }
        public PacketBuilder U32(uint v) { U16((ushort)(v >> 16)); U16((ushort)v); return this; }
        public PacketBuilder Bytes(ReadOnlySpan<byte> b) { _bytes.AddRange(b.ToArray()); return this; }
        public byte[] Build() => _bytes.ToArray();
    }

    private static readonly byte[] Opus = { 0xFC, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09 };

    private static byte[] VoiceDataPacket(
        uint session = 0xDEADBEEF, ushort sender = 7, byte flags = 0x80 | 5, ushort sequence = 1234,
        (ushort bitfield, ushort id)[]? channels = null, byte[]? payload = null)
    {
        channels ??= new (ushort, ushort)[] { (0x0001, 0xABCD), (0x0100, 0x0042) };
        payload ??= Opus;
        var b = new PacketBuilder()
            .U16(DissonanceFrame.Magic)
            .U8((byte)DissonanceMessageType.VoiceData)
            .U32(session)
            .U16(sender)
            .U8(flags)
            .U16(sequence)
            .U16((ushort)channels.Length);
        foreach (var (bitfield, id) in channels) b.U16(bitfield).U16(id);
        b.U16((ushort)payload.Length).Bytes(payload);
        return b.Build();
    }

    // [unit->REQ-VOICE-OUTBOUND-TAP]
    [Fact]
    public void Parses_hand_built_voice_data_packet_per_documented_protocol()
    {
        var packet = VoiceDataPacket();

        Assert.True(DissonanceFrame.TryParse(packet, out var frame, out var error));
        Assert.Equal(DissonanceFrameError.None, error);
        Assert.Equal(0xDEADBEEFu, frame.SessionId);
        Assert.Equal((ushort)7, frame.SenderId);
        Assert.Equal((byte)5, frame.ChannelSession);
        Assert.Equal((ushort)1234, frame.Sequence);
        Assert.Equal(2, frame.Channels.Count);
        Assert.Equal(new DissonanceChannel(0x0001, 0xABCD), frame.Channels[0]);
        Assert.Equal(new DissonanceChannel(0x0100, 0x0042), frame.Channels[1]);
        Assert.Equal(Opus, frame.Payload.ToArray());
    }

    [Fact]
    public void Payload_is_a_slice_of_the_input_not_a_copy()
    {
        var packet = VoiceDataPacket();
        Assert.True(DissonanceFrame.TryParse(packet, out var frame, out _));

        packet[^1] = 0xEE;
        Assert.Equal(0xEE, frame.Payload.Span[^1]);
    }

    [Fact]
    public void Handles_empty_channel_list_and_empty_payload()
    {
        var packet = VoiceDataPacket(channels: Array.Empty<(ushort, ushort)>(), payload: Array.Empty<byte>());

        Assert.True(DissonanceFrame.TryParse(packet, out var frame, out _));
        Assert.Empty(frame.Channels);
        Assert.Equal(0, frame.Payload.Length);
    }

    [Fact]
    public void Channel_session_masks_off_the_always_set_msb()
    {
        var packet = VoiceDataPacket(flags: 0xFF);
        Assert.True(DissonanceFrame.TryParse(packet, out var frame, out _));
        Assert.Equal((byte)0x7F, frame.ChannelSession);
    }

    [Fact]
    public void Rejects_wrong_magic()
    {
        var packet = VoiceDataPacket();
        packet[0] = 0x00;

        Assert.False(DissonanceFrame.TryParse(packet, out _, out var error));
        Assert.Equal(DissonanceFrameError.BadMagic, error);
        Assert.False(DissonanceFrame.HasMagic(packet));
    }

    [Theory]
    [InlineData(DissonanceMessageType.ClientState)]
    [InlineData(DissonanceMessageType.TextData)]
    [InlineData(DissonanceMessageType.ServerRelayUnreliable)]
    public void Rejects_non_voice_packet_types(DissonanceMessageType type)
    {
        var packet = VoiceDataPacket();
        packet[2] = (byte)type;

        Assert.False(DissonanceFrame.TryParse(packet, out _, out var error));
        Assert.Equal(DissonanceFrameError.NotVoiceData, error);
        Assert.True(DissonanceFrame.TryGetMessageType(packet, out var seen));
        Assert.Equal(type, seen);
    }

    [Fact]
    public void Rejects_flags_with_msb_clear()
    {
        var packet = VoiceDataPacket(flags: 0x05);
        Assert.False(DissonanceFrame.TryParse(packet, out _, out var error));
        Assert.Equal(DissonanceFrameError.BadFlags, error);
    }

    [Fact]
    public void Rejects_packets_shorter_than_the_fixed_header()
    {
        var packet = VoiceDataPacket();
        Assert.False(DissonanceFrame.TryParse(packet.AsMemory(0, DissonanceFrame.FixedHeaderSize - 1), out _, out var error));
        Assert.Equal(DissonanceFrameError.TooShort, error);
        Assert.False(DissonanceFrame.TryParse(ReadOnlyMemory<byte>.Empty, out _, out error));
        Assert.Equal(DissonanceFrameError.TooShort, error);
    }

    [Fact]
    public void Rejects_truncated_channel_list_and_truncated_payload()
    {
        var packet = VoiceDataPacket();
        var channelListEnd = DissonanceFrame.FixedHeaderSize + 2 * 4;

        Assert.False(DissonanceFrame.TryParse(packet.AsMemory(0, channelListEnd - 1), out _, out var error));
        Assert.Equal(DissonanceFrameError.Truncated, error);

        Assert.False(DissonanceFrame.TryParse(packet.AsMemory(0, packet.Length - 1), out _, out error));
        Assert.Equal(DissonanceFrameError.Truncated, error);
    }

    [Fact]
    public void Rejects_trailing_bytes_after_payload()
    {
        var packet = VoiceDataPacket().Concat(new byte[] { 0x00 }).ToArray();
        Assert.False(DissonanceFrame.TryParse(packet, out _, out var error));
        Assert.Equal(DissonanceFrameError.TrailingBytes, error);
    }
}
