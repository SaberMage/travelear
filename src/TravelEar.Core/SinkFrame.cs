using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace TravelEar.Core;

/// <summary>Fixed header preceding every block of samples on the Sink pipe.</summary>
/// <param name="Channels">Interleaved channel count (1 = mono, 2 = stereo).</param>
/// <param name="SampleRate">Samples per second per channel (expected 48000).</param>
/// <param name="CaptureTimestamp">Capture timestamp of the first sample (the mod's <c>Stopwatch</c> ticks at <c>OpusEncoder.Encode</c>, resolved through the rings), or 0 when the first sample carries none (mod-generated silence). The Helper pairs it with its render clock for Offset.</param>
/// <param name="SampleCount">Total float32 samples in the payload, across all channels.</param>
public readonly record struct SinkFrameHeader(ushort Channels, uint SampleRate, long CaptureTimestamp, int SampleCount);

/// <summary>
/// Sink pipe framing shared by the mod (writer) and the Helper (reader). Little-endian, one
/// header then <c>SampleCount</c> float32 samples. The magic guards against a reader attaching
/// mid-stream or to the wrong pipe.
/// </summary>
public static class SinkFrame
{
    /// <summary>"TESK" (TravelEar Sink) read as a little-endian uint32.</summary>
    public const uint Magic = 0x4B534554;
    public const int HeaderSize = 4 + 2 + 4 + 8 + 4;
    /// <summary>Upper bound on samples per frame; anything larger is a corrupt header. (1 s of 8-channel 48 kHz.)</summary>
    public const int MaxSampleCount = 48_000 * 8;

    public static int GetEncodedSize(int sampleCount) => HeaderSize + sampleCount * sizeof(float);

    /// <summary>Writes header + samples into <paramref name="destination"/>. Returns bytes written.</summary>
    public static int Write(Span<byte> destination, in SinkFrameHeader header, ReadOnlySpan<float> samples)
    {
        Validate(header);
        if (samples.Length != header.SampleCount)
            throw new ArgumentException("Sample span length must equal header.SampleCount.", nameof(samples));
        var size = GetEncodedSize(samples.Length);
        if (destination.Length < size)
            throw new ArgumentException("Destination too small.", nameof(destination));

        WriteHeader(destination, header);
        var payload = destination.Slice(HeaderSize, samples.Length * sizeof(float));
        if (BitConverter.IsLittleEndian)
        {
            MemoryMarshal.AsBytes(samples).CopyTo(payload);
        }
        else
        {
            for (var i = 0; i < samples.Length; i++)
                BinaryPrimitives.WriteSingleLittleEndian(payload.Slice(i * sizeof(float)), samples[i]);
        }
        return size;
    }

    public static void WriteHeader(Span<byte> destination, in SinkFrameHeader header)
    {
        Validate(header);
        if (destination.Length < HeaderSize) throw new ArgumentException("Destination too small.", nameof(destination));
        BinaryPrimitives.WriteUInt32LittleEndian(destination, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(4), header.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(6), header.SampleRate);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(10), header.CaptureTimestamp);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(18), header.SampleCount);
    }

    /// <summary>Parses a header. False if the bytes are too short, have no magic, or describe an impossible frame.</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> source, out SinkFrameHeader header)
    {
        header = default;
        if (source.Length < HeaderSize) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(source) != Magic) return false;
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(4));
        var rate = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(6));
        var ts = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(10));
        var count = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(18));
        var candidate = new SinkFrameHeader(channels, rate, ts, count);
        if (!IsValid(candidate)) return false;
        header = candidate;
        return true;
    }

    /// <summary>
    /// Parses one complete frame from the front of <paramref name="source"/>. Returns false if
    /// the header is invalid or the payload is incomplete. On success <paramref name="samples"/>
    /// receives a fresh array of <c>header.SampleCount</c> floats and <paramref name="consumed"/>
    /// the total bytes used.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> source, out SinkFrameHeader header, out float[] samples, out int consumed)
    {
        samples = Array.Empty<float>();
        consumed = 0;
        if (!TryReadHeader(source, out header)) return false;
        var size = GetEncodedSize(header.SampleCount);
        if (source.Length < size) return false;
        samples = new float[header.SampleCount];
        ReadPayload(source.Slice(HeaderSize, header.SampleCount * sizeof(float)), samples);
        consumed = size;
        return true;
    }

    /// <summary>Copies a float32 little-endian payload into <paramref name="samples"/>.</summary>
    public static void ReadPayload(ReadOnlySpan<byte> payload, Span<float> samples)
    {
        if (payload.Length != samples.Length * sizeof(float))
            throw new ArgumentException("Payload length must be SampleCount * 4.", nameof(payload));
        if (BitConverter.IsLittleEndian)
        {
            payload.CopyTo(MemoryMarshal.AsBytes(samples));
        }
        else
        {
            for (var i = 0; i < samples.Length; i++)
                samples[i] = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(i * sizeof(float)));
        }
    }

    /// <summary>Writes one frame to a stream (the pipe). <paramref name="scratch"/> is reused across calls when large enough.</summary>
    public static void WriteTo(Stream stream, in SinkFrameHeader header, ReadOnlySpan<float> samples, ref byte[] scratch)
    {
        var size = GetEncodedSize(samples.Length);
        if (scratch.Length < size) scratch = new byte[size];
        Write(scratch, header, samples);
        stream.Write(scratch, 0, size);
    }

    /// <summary>
    /// Reads exactly one frame from a stream. Returns false on a clean end-of-stream before a
    /// header starts; throws <see cref="InvalidDataException"/> on a bad header or a payload
    /// truncated by end-of-stream. <paramref name="samples"/> is reused when large enough; the
    /// valid samples are the first <c>header.SampleCount</c>.
    /// </summary>
    public static bool TryReadFrom(Stream stream, out SinkFrameHeader header, ref float[] samples, ref byte[] scratch)
    {
        header = default;
        if (scratch.Length < HeaderSize) scratch = new byte[Math.Max(HeaderSize, 4096)];

        var got = ReadFully(stream, scratch, HeaderSize);
        if (got == 0) return false;
        if (got < HeaderSize) throw new InvalidDataException("Sink stream ended inside a frame header.");
        if (!TryReadHeader(scratch, out header)) throw new InvalidDataException("Invalid Sink frame header.");

        var payloadBytes = header.SampleCount * sizeof(float);
        if (scratch.Length < payloadBytes) scratch = new byte[payloadBytes];
        if (ReadFully(stream, scratch, payloadBytes) < payloadBytes)
            throw new InvalidDataException("Sink stream ended inside a frame payload.");

        if (samples.Length < header.SampleCount) samples = new float[header.SampleCount];
        ReadPayload(scratch.AsSpan(0, payloadBytes), samples.AsSpan(0, header.SampleCount));
        return true;
    }

    private static int ReadFully(Stream stream, byte[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var n = stream.Read(buffer, total, count - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    private static bool IsValid(in SinkFrameHeader h) =>
        h.Channels >= 1 && h.SampleRate > 0 && h.SampleCount >= 0 && h.SampleCount <= MaxSampleCount
        && h.SampleCount % h.Channels == 0;

    private static void Validate(in SinkFrameHeader h)
    {
        if (!IsValid(h)) throw new ArgumentException("Invalid Sink frame header.", nameof(h));
    }
}
