using TravelEar.Core;
using Xunit;

namespace TravelEar.Tests;

public class FrameStampTableTests
{
    // [unit->REQ-OFFSET-MEASURE]
    [Fact]
    public void Resolves_a_position_inside_a_marked_block_to_its_timestamp()
    {
        var table = new FrameStampTable();
        table.Mark(position: 1000, length: 480, timestamp: 77);
        Assert.True(table.TryResolve(1000, 0, out var ts));
        Assert.Equal(77, ts);
        Assert.True(table.TryResolve(1479, 0, out ts));
        Assert.Equal(77, ts);
        Assert.False(table.TryResolve(1480, 0, out _));
        Assert.False(table.TryResolve(999, 0, out _));
    }

    [Fact]
    public void An_empty_table_resolves_nothing()
    {
        Assert.False(new FrameStampTable().TryResolve(0, 0, out _));
    }

    // [unit->REQ-OFFSET-MEASURE]
    [Fact]
    public void Monotonic_positions_pick_the_block_that_contains_them()
    {
        var table = new FrameStampTable();
        table.Mark(0, 100, 1);
        table.Mark(100, 100, 2);
        table.Mark(200, 100, 3);
        Assert.True(table.TryResolve(50, 0, out var ts)); Assert.Equal(1, ts);
        Assert.True(table.TryResolve(100, 0, out ts)); Assert.Equal(2, ts);
        Assert.True(table.TryResolve(299, 0, out ts)); Assert.Equal(3, ts);
        Assert.False(table.TryResolve(300, 0, out _));
    }

    // [unit->REQ-OFFSET-MEASURE]
    [Fact]
    public void Wrapped_ring_positions_match_modulo_the_ring_and_the_newest_mark_wins()
    {
        const long ring = 1024;
        var table = new FrameStampTable(slots: 4);
        table.Mark(900, 200, 10);   // spans 900..1099, i.e. wraps to 0..75
        Assert.True(table.TryResolve(950, ring, out var ts)); Assert.Equal(10, ts);
        Assert.True(table.TryResolve(20, ring, out ts)); Assert.Equal(10, ts);   // wrapped part
        Assert.True(table.TryResolve(1024 + 20, ring, out ts)); Assert.Equal(10, ts); // monotonic caller, same index
        Assert.False(table.TryResolve(76, ring, out _));

        // One ring later a new mark covers the same indices: it shadows the stale one.
        table.Mark(900, 200, 20);
        Assert.True(table.TryResolve(950, ring, out ts)); Assert.Equal(20, ts);
    }

    // [unit->REQ-OFFSET-MEASURE]
    [Fact]
    public void A_NoStamp_mark_resolves_to_none_and_shadows_older_marks()
    {
        var table = new FrameStampTable();
        table.Mark(0, 100, 5);
        table.Mark(0, 100, FrameStampTable.NoStamp); // silence written over the same span
        Assert.False(table.TryResolve(50, 0, out _));
    }

    [Fact]
    public void Old_marks_are_evicted_when_the_slots_wrap()
    {
        var table = new FrameStampTable(slots: 2);
        table.Mark(0, 10, 1);
        table.Mark(10, 10, 2);
        table.Mark(20, 10, 3); // overwrites the slot of mark 1
        Assert.False(table.TryResolve(5, 0, out _));
        Assert.True(table.TryResolve(15, 0, out var ts)); Assert.Equal(2, ts);
        Assert.True(table.TryResolve(25, 0, out ts)); Assert.Equal(3, ts);
        Assert.Equal(3, table.Marks);
    }

    [Fact]
    public void Rejects_bad_arguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameStampTable(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameStampTable().Mark(0, 0, 1));
    }

    // [unit->REQ-OFFSET-MEASURE]
    [Fact]
    public void Concurrent_marks_and_resolves_never_yield_a_torn_timestamp()
    {
        // Producer marks block k at position 100k with timestamp 1000+k; the consumer resolves
        // random positions and must only ever see the timestamp that belongs to that block.
        var table = new FrameStampTable(slots: 8);
        const int blocks = 200_000;
        var stop = false;
        var torn = 0;
        var consumer = new Thread(() =>
        {
            var random = new Random(1);
            while (!Volatile.Read(ref stop))
            {
                var k = random.Next(blocks);
                var pos = k * 100L + random.Next(100);
                if (table.TryResolve(pos, 0, out var ts) && ts != 1000 + k) Interlocked.Increment(ref torn);
            }
        });
        consumer.Start();
        for (var k = 0; k < blocks; k++) table.Mark(k * 100L, 100, 1000 + k);
        Volatile.Write(ref stop, true);
        consumer.Join();
        Assert.Equal(0, torn);
    }
}
