using System.Buffers.Binary;
using TravelEar.Core;
using Xunit;

namespace TravelEar.Tests;

public class SinkFrameTests
{
    private static readonly float[] Stereo960 =
        Enumerable.Range(0, 960).Select(i => MathF.Sin(i * 0.01f) * (i % 2 == 0 ? 1f : -0.5f)).ToArray();

    private static SinkFrameHeader Header(int count = 960, ushort channels = 2) =>
        new(channels, 48_000, CaptureTimestamp: 123_456_789_012L, SampleCount: count);

    [Fact]
    public void Round_trips_header_and_samples_through_a_span()
    {
        var header = Header();
        var buffer = new byte[SinkFrame.GetEncodedSize(header.SampleCount)];

        var written = SinkFrame.Write(buffer, header, Stereo960);
        Assert.Equal(buffer.Length, written);
        Assert.Equal(SinkFrame.HeaderSize + 960 * 4, written);

        Assert.True(SinkFrame.TryRead(buffer, out var read, out var samples, out var consumed));
        Assert.Equal(header, read);
        Assert.Equal(Stereo960, samples);
        Assert.Equal(written, consumed);
    }

    [Fact]
    public void Header_is_little_endian_with_magic_first()
    {
        var header = new SinkFrameHeader(2, 48_000, 0x0102030405060708L, 4);
        var buffer = new byte[SinkFrame.HeaderSize];
        SinkFrame.WriteHeader(buffer, header);

        Assert.Equal("TESK", System.Text.Encoding.ASCII.GetString(buffer, 0, 4));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(4)));
        Assert.Equal(48_000u, BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(6)));
        Assert.Equal(0x08, buffer[10]); // low byte of the timestamp comes first
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(18)));
    }

    [Fact]
    public void Round_trips_a_sequence_of_frames_through_a_stream()
    {
        var stream = new MemoryStream();
        var scratch = Array.Empty<byte>();
        var frames = new[] { Header(4, 2), Header(6, 3), Header(0, 1) };
        var payloads = new[] { new float[] { 1, 2, 3, 4 }, new float[] { 5, 6, 7, 8, 9, 10 }, Array.Empty<float>() };

        for (var i = 0; i < frames.Length; i++)
            SinkFrame.WriteTo(stream, frames[i], payloads[i], ref scratch);

        stream.Position = 0;
        var samples = Array.Empty<float>();
        var readScratch = Array.Empty<byte>();
        for (var i = 0; i < frames.Length; i++)
        {
            Assert.True(SinkFrame.TryReadFrom(stream, out var header, ref samples, ref readScratch));
            Assert.Equal(frames[i], header);
            Assert.Equal(payloads[i], samples.Take(header.SampleCount));
        }
        Assert.False(SinkFrame.TryReadFrom(stream, out _, ref samples, ref readScratch)); // clean EOF
    }

    [Fact]
    public void Rejects_bad_magic_short_input_and_impossible_headers()
    {
        var buffer = new byte[SinkFrame.HeaderSize];
        SinkFrame.WriteHeader(buffer, Header(4));

        Assert.False(SinkFrame.TryReadHeader(buffer.AsSpan(0, SinkFrame.HeaderSize - 1), out _));

        var badMagic = (byte[])buffer.Clone();
        badMagic[0] ^= 0xFF;
        Assert.False(SinkFrame.TryReadHeader(badMagic, out _));

        var zeroChannels = (byte[])buffer.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(zeroChannels.AsSpan(4), 0);
        Assert.False(SinkFrame.TryReadHeader(zeroChannels, out _));

        var hugeCount = (byte[])buffer.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(hugeCount.AsSpan(18), SinkFrame.MaxSampleCount + 1);
        Assert.False(SinkFrame.TryReadHeader(hugeCount, out _));

        var negativeCount = (byte[])buffer.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(negativeCount.AsSpan(18), -4);
        Assert.False(SinkFrame.TryReadHeader(negativeCount, out _));

        var oddForStereo = (byte[])buffer.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(oddForStereo.AsSpan(18), 3);
        Assert.False(SinkFrame.TryReadHeader(oddForStereo, out _));
    }

    [Fact]
    public void TryRead_fails_on_incomplete_payload_without_consuming()
    {
        var header = Header(4);
        var buffer = new byte[SinkFrame.GetEncodedSize(4)];
        SinkFrame.Write(buffer, header, new float[] { 1, 2, 3, 4 });

        Assert.False(SinkFrame.TryRead(buffer.AsSpan(0, buffer.Length - 1), out _, out _, out var consumed));
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void Stream_reader_throws_on_truncated_payload_and_bad_header()
    {
        var full = new byte[SinkFrame.GetEncodedSize(4)];
        SinkFrame.Write(full, Header(4), new float[] { 1, 2, 3, 4 });

        var samples = Array.Empty<float>();
        var scratch = Array.Empty<byte>();
        var truncated = new MemoryStream(full, 0, full.Length - 2);
        Assert.Throws<InvalidDataException>(() => SinkFrame.TryReadFrom(truncated, out _, ref samples, ref scratch));

        var garbage = new MemoryStream(Enumerable.Repeat((byte)0xAA, SinkFrame.HeaderSize).ToArray());
        Assert.Throws<InvalidDataException>(() => SinkFrame.TryReadFrom(garbage, out _, ref samples, ref scratch));
    }

    [Fact]
    public void Write_rejects_mismatched_sample_count_and_small_destination()
    {
        var header = Header(4);
        Assert.Throws<ArgumentException>(() => SinkFrame.Write(new byte[64], header, new float[3]));
        Assert.Throws<ArgumentException>(() => SinkFrame.Write(new byte[8], header, new float[4]));
    }
}
