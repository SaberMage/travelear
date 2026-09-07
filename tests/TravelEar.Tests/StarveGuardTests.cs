using TravelEar.Core;
using Xunit;

namespace TravelEar.Tests;

/// <summary>
/// The Helper's re-prime rule after a starve (ADR-0005): once the ring runs dry, hold playback
/// on silence until the backlog is back at the jitter target, then resume.
/// </summary>
public class StarveGuardTests
{
    private const int Target = 4800; // 100 ms mono at 48 kHz
    private const int Block = 480;   // 10 ms

    // [unit->REQ-SINK-FORMAT]
    [Fact]
    public void Reads_normally_while_the_backlog_covers_the_request()
    {
        var guard = new StarveGuard(Target);
        Assert.False(guard.ShouldHold(Target, Block));
        Assert.False(guard.ShouldHold(Block, Block));
        Assert.Equal(0, guard.Starves);
        Assert.False(guard.Holding);
    }

    [Fact]
    public void A_short_read_starts_a_hold_that_lasts_until_the_target_backlog()
    {
        var guard = new StarveGuard(Target);
        Assert.True(guard.ShouldHold(Block - 1, Block));
        Assert.True(guard.Holding);
        Assert.Equal(1, guard.Starves);
        Assert.True(guard.ShouldHold(Block, Block));         // enough for one read, still below target
        Assert.True(guard.ShouldHold(Target - 1, Block));
        Assert.False(guard.ShouldHold(Target, Block));       // back at the jitter budget: resume
        Assert.False(guard.Holding);
        Assert.Equal(1, guard.Starves);
    }

    [Fact]
    public void An_empty_ring_holds_and_a_second_starve_counts_again()
    {
        var guard = new StarveGuard(Target);
        Assert.True(guard.ShouldHold(0, Block));
        Assert.False(guard.ShouldHold(Target * 2, Block));
        Assert.True(guard.ShouldHold(0, Block));
        Assert.Equal(2, guard.Starves);
    }

    [Fact]
    public void Resume_waits_for_the_request_when_it_exceeds_the_target()
    {
        var guard = new StarveGuard(100);
        Assert.True(guard.ShouldHold(0, Block));
        Assert.True(guard.ShouldHold(100, Block));
        Assert.False(guard.ShouldHold(Block, Block));
    }

    [Fact]
    public void A_zero_target_still_holds_only_while_the_request_is_short()
    {
        var guard = new StarveGuard(0);
        Assert.False(guard.ShouldHold(Block, Block));
        Assert.True(guard.ShouldHold(10, Block));
        Assert.False(guard.ShouldHold(Block, Block));
    }
}
