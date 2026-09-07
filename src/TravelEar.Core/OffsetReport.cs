using System.Buffers.Binary;

namespace TravelEar.Core;

/// <summary>One Offset observation the Helper sends back to the mod.</summary>
/// <param name="CaptureTimestamp">The capture timestamp that travelled with the Sink frame (the mod's <c>Stopwatch</c> ticks at <c>OpusEncoder.Encode</c>).</param>
/// <param name="RenderTimestamp">When the frame's first sample leaves the Helper's endpoint, in the same clock (both processes read the machine's performance counter).</param>
public readonly record struct OffsetReport(long CaptureTimestamp, long RenderTimestamp);

/// <summary>
/// Framing for the return pipe (<c>TravelEar.Sink.Back</c>): fixed 20-byte little-endian records,
/// magic then the two timestamps. Shared by the Helper (writer) and the mod (reader).
/// </summary>
public static class OffsetReportFrame
{
    /// <summary>"TEOF" (TravelEar Offset) read as a little-endian uint32.</summary>
    public const uint Magic = 0x464F4554;
    public const int Size = 4 + 8 + 8;

    // [impl->REQ-OFFSET-MEASURE]
    public static void Write(Span<byte> destination, in OffsetReport report)
    {
        if (destination.Length < Size) throw new ArgumentException("Destination too small.", nameof(destination));
        BinaryPrimitives.WriteUInt32LittleEndian(destination, Magic);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(4), report.CaptureTimestamp);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(12), report.RenderTimestamp);
    }

    /// <summary>Parses one record. False if the bytes are too short or carry no magic.</summary>
    public static bool TryRead(ReadOnlySpan<byte> source, out OffsetReport report)
    {
        report = default;
        if (source.Length < Size) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(source) != Magic) return false;
        report = new OffsetReport(
            BinaryPrimitives.ReadInt64LittleEndian(source.Slice(4)),
            BinaryPrimitives.ReadInt64LittleEndian(source.Slice(12)));
        return true;
    }

    /// <summary>Writes one record to a stream. <paramref name="scratch"/> must hold <see cref="Size"/> bytes.</summary>
    public static void WriteTo(Stream stream, in OffsetReport report, byte[] scratch)
    {
        Write(scratch, report);
        stream.Write(scratch, 0, Size);
    }

    /// <summary>
    /// Reads exactly one record. False on a clean end-of-stream before a record starts; throws
    /// <see cref="InvalidDataException"/> on a bad magic or a record cut by end-of-stream.
    /// </summary>
    public static bool TryReadFrom(Stream stream, out OffsetReport report, byte[] scratch)
    {
        report = default;
        var total = 0;
        while (total < Size)
        {
            var n = stream.Read(scratch, total, Size - total);
            if (n <= 0) break;
            total += n;
        }
        if (total == 0) return false;
        if (total < Size) throw new InvalidDataException("Offset stream ended inside a record.");
        if (!TryRead(scratch, out report)) throw new InvalidDataException("Invalid Offset record.");
        return true;
    }
}
