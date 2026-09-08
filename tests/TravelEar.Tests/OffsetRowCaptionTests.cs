using TravelEar.Core;
using Xunit;

namespace TravelEar.Tests;

public class OffsetRowCaptionTests
{
    // [unit->REQ-OFFSET-MEASURE]
    [Fact]
    public void Reads_measuring_before_the_first_average()
    {
        Assert.Equal("TravelEar offset: measuring", OffsetRowCaption.Format(double.NaN));
        Assert.Equal("TravelEar offset: measuring", OffsetRowCaption.Format(double.PositiveInfinity));
    }

    // [unit->REQ-OFFSET-MEASURE]
    [Fact]
    public void Rounds_the_average_to_whole_milliseconds()
    {
        Assert.Equal("TravelEar offset: 203 ms", OffsetRowCaption.Format(203.4));
    }

    // [unit->REQ-OFFSET-MEASURE]
    [Fact]
    public void Shows_zero_as_a_figure()
    {
        Assert.Equal("TravelEar offset: 0 ms", OffsetRowCaption.Format(0));
    }

    // [unit->REQ-OFFSET-MEASURE]
    [Fact]
    public void Treats_a_negative_average_as_not_measured()
    {
        Assert.Equal("TravelEar offset: measuring", OffsetRowCaption.Format(-5));
    }
}
