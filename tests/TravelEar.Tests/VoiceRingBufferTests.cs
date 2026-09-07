using TravelEar.Core;
using Xunit;

namespace TravelEar.Tests;

public class VoiceRingBufferTests
{
    private static float[] Ramp(int start, int count) =>
        Enumerable.Range(start, count).Select(i => (float)i).ToArray();

    [Fact]
    public void Reads_back_what_was_written_in_order()
    {
        var ring = new VoiceRingBuffer(16);
        Assert.Equal(5, ring.Write(Ramp(0, 5)));
        Assert.Equal(5, ring.Count);

        var dest = new float[5];
        Assert.Equal(5, ring.Read(dest));
        Assert.Equal(Ramp(0, 5), dest);
        Assert.Equal(0, ring.Count);
        Assert.Equal(0, ring.Underruns);
    }

    [Fact]
    public void Wraps_around_the_end_of_the_backing_array()
    {
        var ring = new VoiceRingBuffer(8);
        ring.Write(Ramp(0, 6));
        ring.Read(new float[6]); // head = tail = 6

        Assert.Equal(5, ring.Write(Ramp(100, 5))); // occupies indices 6,7,0,1,2
        var dest = new float[5];
        Assert.Equal(5, ring.Read(dest));
        Assert.Equal(Ramp(100, 5), dest);
    }

    [Fact]
    public void Wrap_survives_many_cycles_with_odd_sizes()
    {
        var ring = new VoiceRingBuffer(7);
        var next = 0;
        var expect = 0;
        for (var cycle = 0; cycle < 200; cycle++)
        {
            var w = Ramp(next, 3);
            Assert.Equal(3, ring.Write(w));
            next += 3;

            var dest = new float[3];
            Assert.Equal(3, ring.Read(dest));
            Assert.Equal(Ramp(expect, 3), dest);
            expect += 3;
        }
        Assert.Equal(0, ring.DroppedSamples);
        Assert.Equal(0, ring.Underruns);
    }

    [Fact]
    public void Underrun_pads_the_remainder_with_silence()
    {
        var ring = new VoiceRingBuffer(16);
        ring.Write(Ramp(1, 3));

        var dest = Enumerable.Repeat(9f, 8).ToArray();
        Assert.Equal(3, ring.Read(dest));
        Assert.Equal(new[] { 1f, 2f, 3f, 0f, 0f, 0f, 0f, 0f }, dest);
        Assert.Equal(1, ring.Underruns);
    }

    [Fact]
    public void Empty_read_is_all_silence()
    {
        var ring = new VoiceRingBuffer(4);
        var dest = Enumerable.Repeat(9f, 4).ToArray();
        Assert.Equal(0, ring.Read(dest));
        Assert.All(dest, s => Assert.Equal(0f, s));
        Assert.Equal(1, ring.Underruns);
    }

    [Fact]
    public void Overflow_keeps_the_oldest_samples_and_counts_the_dropped_ones()
    {
        var ring = new VoiceRingBuffer(4);
        Assert.Equal(4, ring.Write(Ramp(0, 6)));
        Assert.Equal(2, ring.DroppedSamples);
        Assert.Equal(0, ring.Write(Ramp(10, 1)));
        Assert.Equal(3, ring.DroppedSamples);

        var dest = new float[4];
        Assert.Equal(4, ring.Read(dest));
        Assert.Equal(Ramp(0, 4), dest);
    }

    [Fact]
    public void Clear_discards_buffered_samples()
    {
        var ring = new VoiceRingBuffer(8);
        ring.Write(Ramp(0, 5));
        ring.Clear();
        Assert.Equal(0, ring.Count);
        Assert.Equal(0, ring.Read(new float[2]));
    }

    [Fact]
    public async Task Concurrent_writer_and_reader_preserve_sample_order()
    {
        const int total = 200_000;
        var ring = new VoiceRingBuffer(1024);
        var received = new List<float>(total);

        var writer = Task.Run(() =>
        {
            var next = 0;
            var chunk = new float[64];
            while (next < total)
            {
                var n = Math.Min(chunk.Length, total - next);
                for (var i = 0; i < n; i++) chunk[i] = next + i;
                var written = ring.Write(chunk.AsSpan(0, n));
                // Back off when full instead of dropping, so the order check stays exact.
                if (written < n)
                {
                    next += written;
                    Thread.SpinWait(50);
                    continue;
                }
                next += n;
            }
        });

        var dest = new float[48];
        while (received.Count < total)
        {
            var got = ring.Read(dest);
            for (var i = 0; i < got; i++) received.Add(dest[i]);
            if (got == 0) Thread.SpinWait(50);
        }
        await writer;

        Assert.Equal(total, received.Count);
        for (var i = 0; i < total; i++)
            Assert.Equal((float)i, received[i]);
    }
}
