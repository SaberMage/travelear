using TravelEar.Core;
using Xunit;

namespace TravelEar.Tests;

public class VoiceRingBufferDiscardTests
{
    private static float[] Ramp(int start, int count) =>
        Enumerable.Range(start, count).Select(i => (float)i).ToArray();

    [Fact]
    public void Discard_drops_the_oldest_samples_and_keeps_the_rest_in_order()
    {
        var ring = new VoiceRingBuffer(16);
        ring.Write(Ramp(0, 10));

        Assert.Equal(6, ring.Discard(6));
        Assert.Equal(4, ring.Count);

        var dest = new float[4];
        Assert.Equal(4, ring.Read(dest));
        Assert.Equal(Ramp(6, 4), dest);
    }

    [Fact]
    public void Discard_is_clamped_to_what_is_buffered()
    {
        var ring = new VoiceRingBuffer(8);
        ring.Write(Ramp(0, 3));

        Assert.Equal(3, ring.Discard(100));
        Assert.Equal(0, ring.Count);
        Assert.Equal(0, ring.Discard(1));
        Assert.Equal(0, ring.Discard(-5));
    }

    [Fact]
    public void Discard_frees_space_for_the_writer()
    {
        var ring = new VoiceRingBuffer(8);
        ring.Write(Ramp(0, 8)); // full
        Assert.Equal(0, ring.Write(Ramp(8, 2)));

        ring.Discard(2);
        Assert.Equal(2, ring.Write(Ramp(8, 2)));

        var dest = new float[8];
        ring.Read(dest);
        Assert.Equal(Ramp(2, 8), dest);
    }
}
