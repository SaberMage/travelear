using TravelEar.Core;
using Xunit;

namespace TravelEar.Tests;

public class TransmitFaderTests
{
    private const int Rate = 48000;

    private static float[] Ones(int count)
    {
        var frame = new float[count];
        Array.Fill(frame, 1f);
        return frame;
    }

    // [unit->REQ-RENDER-CLEAN]
    [Fact]
    public void Starts_silent_and_fades_in_linearly_over_the_fade_in_time()
    {
        var fader = new TransmitFader(fadeInMs: 100, fadeOutMs: 100);
        var frame = Ones(4800); // 100 ms
        var silent = fader.Apply(frame, open: true, Rate);
        Assert.False(silent);
        Assert.Equal(1.0 / 4800, frame[0], 5);
        Assert.Equal(0.5, frame[2399], 2);
        Assert.Equal(1.0, frame[4799], 3);
        Assert.Equal(1f, fader.Gain);
    }

    [Fact]
    public void Zero_fades_are_a_hard_gate()
    {
        var fader = new TransmitFader(0, 0);
        var frame = Ones(480);
        Assert.False(fader.Apply(frame, true, Rate));
        Assert.All(frame, s => Assert.Equal(1f, s));
        Assert.True(fader.Apply(frame, false, Rate));
        Assert.All(frame, s => Assert.Equal(0f, s));
    }

    // [unit->REQ-RENDER-CLEAN]
    [Fact]
    public void Fades_out_over_the_fade_out_time_after_the_signal_drops()
    {
        var fader = new TransmitFader(0, 100);
        fader.Apply(Ones(480), true, Rate);
        var frame = Ones(4800);
        Assert.False(fader.Apply(frame, false, Rate)); // still audible while fading
        Assert.Equal(0.5, frame[2399], 2);
        Assert.Equal(0f, frame[4799]);
        Assert.True(fader.Apply(Ones(480), false, Rate)); // fully faded: the caller may push zeros
    }

    [Fact]
    public void A_re_onset_mid_fade_ramps_back_from_the_current_gain()
    {
        var fader = new TransmitFader(100, 100);
        fader.Apply(Ones(4800), true, Rate);
        fader.Apply(Ones(2400), false, Rate); // half way down
        Assert.Equal(0.5, fader.Gain, 2);
        var frame = Ones(240);
        fader.Apply(frame, true, Rate);
        Assert.InRange(frame[0], 0.5f, 0.501f); // no jump
        Assert.Equal(0.55, frame[239], 2);
    }

    [Fact]
    public void Set_keeps_the_current_gain()
    {
        var fader = new TransmitFader(100, 100);
        fader.Apply(Ones(2400), true, Rate);
        fader.Set(0, 0);
        Assert.Equal(0.5, fader.Gain, 2);
        Assert.Equal(0, fader.FadeInMs);
    }

    [Fact]
    public void Rejects_negative_fades_and_a_bad_sample_rate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransmitFader(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransmitFader(0, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransmitFader(0, 0).Apply(new float[1], true, 0));
    }
}
