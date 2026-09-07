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

    // [unit->REQ-HAZARD-NO-GAME-AUDIO-LEAK]
    [Fact]
    public void Every_channel_count_and_buffer_size_leaves_the_game_only_zeros()
    {
        // Unity DSP configurations the game may run: mono to 7.1, 256 to 4096 frames per block,
        // with the Sink ring empty, half full, and full. Whatever the ring accepts, the block the
        // game's mixer receives back is all zeros and the ring holds only what was in the block.
        int[] channelCounts = { 1, 2, 4, 6, 8 };
        int[] blockFrames = { 256, 512, 1024, 2048, 4096 };
        var random = new Random(20260907);

        foreach (var channels in channelCounts)
        foreach (var frames in blockFrames)
        foreach (var fill in new[] { 0.0, 0.5, 1.0 })
        {
            var block = frames * channels;
            var ring = new VoiceRingBuffer(block * 2);
            var preload = (int)(ring.Capacity * fill);
            if (preload > 0) ring.Write(new float[preload]);

            var data = new float[block];
            for (var i = 0; i < block; i++) data[i] = (float)(random.NextDouble() * 2 - 1);
            var original = (float[])data.Clone();

            var accepted = TapDivert.Divert(data, ring);

            Assert.All(data, s => Assert.Equal(0f, s));
            Assert.Equal(Math.Min(block, ring.Capacity - preload), accepted);
            var readBack = new float[preload + accepted];
            ring.Read(readBack);
            Assert.Equal(original.AsSpan(0, accepted).ToArray(), readBack.AsSpan(preload, accepted).ToArray());
        }
    }

    [Fact]
    public void Handles_an_empty_buffer()
    {
        var ring = new VoiceRingBuffer(8);
        Assert.Equal(0, TapDivert.Divert(Span<float>.Empty, ring));
        Assert.Equal(0, ring.Count);
    }
}
