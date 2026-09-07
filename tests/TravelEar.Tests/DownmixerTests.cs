using TravelEar.Core;
using Xunit;

namespace TravelEar.Tests;

public class DownmixerTests
{
    // [unit->REQ-SINK-FORMAT]
    [Fact]
    public void Stereo_folds_to_the_average_of_both_channels()
    {
        var stereo = new[] { 1f, 0f, 0.5f, 0.5f, -1f, 1f, 0.25f, -0.75f };
        var mono = new float[4];
        Assert.Equal(4, Downmixer.ToMono(stereo, 2, mono));
        Assert.Equal(new[] { 0.5f, 0.5f, 0f, -0.25f }, mono);
    }

    [Fact]
    public void Identical_channels_come_out_unchanged()
    {
        var quad = new[] { 0.3f, 0.3f, 0.3f, 0.3f, -0.6f, -0.6f, -0.6f, -0.6f };
        var mono = new float[2];
        Downmixer.ToMono(quad, 4, mono);
        Assert.Equal(0.3f, mono[0], 6);
        Assert.Equal(-0.6f, mono[1], 6);
    }

    [Fact]
    public void Full_scale_input_cannot_exceed_full_scale()
    {
        var stereo = new[] { 1f, 1f, -1f, -1f };
        var mono = new float[2];
        Downmixer.ToMono(stereo, 2, mono);
        Assert.All(mono, s => Assert.InRange(s, -1f, 1f));
    }

    // [unit->REQ-SINK-FORMAT]
    [Fact]
    public void Folds_in_place_over_the_front_of_the_input()
    {
        var buffer = new[] { 1f, 0f, 0.5f, 0.5f, -1f, 1f };
        var frames = Downmixer.ToMono(buffer, 2, buffer);
        Assert.Equal(3, frames);
        Assert.Equal(new[] { 0.5f, 0.5f, 0f }, buffer[..frames]);
    }

    [Fact]
    public void Mono_input_is_copied_through()
    {
        var input = new[] { 0.1f, 0.2f, 0.3f };
        var output = new float[3];
        Assert.Equal(3, Downmixer.ToMono(input, 1, output));
        Assert.Equal(input, output);
        // In place with one channel is a no-op, not a self-copy.
        Assert.Equal(3, Downmixer.ToMono(input, 1, input));
        Assert.Equal(new[] { 0.1f, 0.2f, 0.3f }, input);
    }

    [Fact]
    public void Empty_input_yields_zero_frames()
    {
        Assert.Equal(0, Downmixer.ToMono(ReadOnlySpan<float>.Empty, 2, Span<float>.Empty));
    }

    [Fact]
    public void Rejects_bad_channel_counts_partial_frames_and_a_short_destination()
    {
        var stereo = new float[4];
        Assert.Throws<ArgumentOutOfRangeException>(() => Downmixer.ToMono(stereo, 0, new float[2]));
        Assert.Throws<ArgumentException>(() => Downmixer.ToMono(new float[3], 2, new float[2]));
        Assert.Throws<ArgumentException>(() => Downmixer.ToMono(stereo, 2, new float[1]));
    }
}
