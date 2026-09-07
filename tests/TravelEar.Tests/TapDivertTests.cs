using TravelEar.Core;
using Xunit;

namespace TravelEar.Tests;

public class TapDivertTests
{
    private static float[] Ramp(int start, int count) =>
        Enumerable.Range(start, count).Select(i => (float)i + 0.5f).ToArray();

    // [unit->REQ-TAP-DIVERT]
    [Fact]
    public void Copies_the_buffer_into_the_ring_and_zeroes_it_in_place()
    {
        var ring = new VoiceRingBuffer(64);
        var data = Ramp(1, 16);
        var expected = (float[])data.Clone();

        Assert.Equal(16, TapDivert.Divert(data, ring));

        Assert.All(data, s => Assert.Equal(0f, s));
        var readBack = new float[16];
        Assert.Equal(16, ring.Read(readBack));
        Assert.Equal(expected, readBack);
    }

    // [unit->REQ-TAP-DIVERT]
    [Fact]
    public void Zeroes_the_buffer_even_when_the_ring_is_full()
    {
        var ring = new VoiceRingBuffer(8);
        ring.Write(Ramp(0, 8)); // ring now full
        var data = Ramp(100, 8);

        Assert.Equal(0, TapDivert.Divert(data, ring));

        Assert.All(data, s => Assert.Equal(0f, s));
        Assert.Equal(8, ring.DroppedSamples);
    }

    [Fact]
    public void Handles_an_empty_buffer()
    {
        var ring = new VoiceRingBuffer(8);
        Assert.Equal(0, TapDivert.Divert(Span<float>.Empty, ring));
        Assert.Equal(0, ring.Count);
    }
}
