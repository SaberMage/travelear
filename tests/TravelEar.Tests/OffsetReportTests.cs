using TravelEar.Core;
using Xunit;

namespace TravelEar.Tests;

public class OffsetReportTests
{
    // [unit->REQ-OFFSET-MEASURE]
    [Fact]
    public void Round_trips_through_bytes()
    {
        var report = new OffsetReport(123_456_789_012L, 123_456_999_999L);
        var bytes = new byte[OffsetReportFrame.Size];
        OffsetReportFrame.Write(bytes, report);
        Assert.Equal(new byte[] { (byte)'T', (byte)'E', (byte)'O', (byte)'F' }, bytes[..4]);
        Assert.True(OffsetReportFrame.TryRead(bytes, out var back));
        Assert.Equal(report, back);
    }

    [Fact]
    public void Rejects_short_input_and_a_bad_magic()
    {
        Assert.False(OffsetReportFrame.TryRead(new byte[OffsetReportFrame.Size - 1], out _));
        var bytes = new byte[OffsetReportFrame.Size];
        OffsetReportFrame.Write(bytes, new OffsetReport(1, 2));
        bytes[0] ^= 0xFF;
        Assert.False(OffsetReportFrame.TryRead(bytes, out _));
        Assert.Throws<ArgumentException>(() => OffsetReportFrame.Write(new byte[3], new OffsetReport(1, 2)));
    }

    // [unit->REQ-OFFSET-MEASURE]
    [Fact]
    public void Streams_records_back_to_back_and_stops_cleanly_at_end_of_stream()
    {
        var stream = new MemoryStream();
        var scratch = new byte[OffsetReportFrame.Size];
        OffsetReportFrame.WriteTo(stream, new OffsetReport(1, 2), scratch);
        OffsetReportFrame.WriteTo(stream, new OffsetReport(3, 4), scratch);
        stream.Position = 0;
        Assert.True(OffsetReportFrame.TryReadFrom(stream, out var a, scratch));
        Assert.True(OffsetReportFrame.TryReadFrom(stream, out var b, scratch));
        Assert.False(OffsetReportFrame.TryReadFrom(stream, out _, scratch));
        Assert.Equal(new OffsetReport(1, 2), a);
        Assert.Equal(new OffsetReport(3, 4), b);
    }

    [Fact]
    public void A_record_cut_by_end_of_stream_is_an_error()
    {
        var stream = new MemoryStream(new byte[OffsetReportFrame.Size - 5]);
        Assert.Throws<InvalidDataException>(() => OffsetReportFrame.TryReadFrom(stream, out _, new byte[OffsetReportFrame.Size]));
    }

    [Fact]
    public void Back_pipe_name_derives_from_the_sink_pipe_name()
    {
        Assert.Equal("TravelEar.Sink.Back", HelperOptions.Default.BackPipe);
        Assert.Equal("X.Back", HelperOptions.BackPipeName("X"));
    }
}
