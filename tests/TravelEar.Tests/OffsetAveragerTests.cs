using TravelEar.Core;
using Xunit;

namespace TravelEar.Tests;

public class OffsetAveragerTests
{
    // [unit->REQ-OFFSET-MEASURE]
    [Fact]
    public void Averages_the_samples_inside_the_window()
    {
        var avg = new OffsetAverager(windowMs: 10_000);
        avg.Add(200, nowMs: 0);
        avg.Add(220, 1000);
        avg.Add(180, 2000);
        Assert.True(avg.TryAverage(2000, out var mean, out var min, out var max, out var count));
        Assert.Equal(200, mean, 6);
        Assert.Equal(180, min);
        Assert.Equal(220, max);
        Assert.Equal(3, count);
    }

    // [unit->REQ-OFFSET-MEASURE]
    [Fact]
    public void Drops_samples_older_than_the_window()
    {
        var avg = new OffsetAverager(10_000);
        avg.Add(1000, 0);      // will fall out
        avg.Add(200, 5000);
        avg.Add(300, 12_000);
        Assert.True(avg.TryAverage(12_000, out var mean, out _, out _, out var count));
        Assert.Equal(2, count);
        Assert.Equal(250, mean, 6);
        // Time moves on without new samples: the window empties.
        Assert.False(avg.TryAverage(30_000, out _, out _, out _, out count));
        Assert.Equal(0, count);
    }

    [Fact]
    public void A_sample_exactly_at_the_window_edge_still_counts()
    {
        var avg = new OffsetAverager(10_000);
        avg.Add(100, 0);
        Assert.True(avg.TryAverage(10_000, out _, out _, out _, out var count));
        Assert.Equal(1, count);
        Assert.False(avg.TryAverage(10_001, out _, out _, out _, out _));
    }

    [Fact]
    public void Ignores_NaN_and_infinite_samples()
    {
        var avg = new OffsetAverager();
        avg.Add(double.NaN, 0);
        avg.Add(double.PositiveInfinity, 0);
        Assert.False(avg.TryAverage(0, out _, out _, out _, out _));
    }

    [Fact]
    public void Rejects_a_non_positive_window()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OffsetAverager(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OffsetAverager(-5));
    }
}
