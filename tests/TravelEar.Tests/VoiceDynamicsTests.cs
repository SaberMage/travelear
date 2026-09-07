using TravelEar.Core;
using Xunit;

namespace TravelEar.Tests;

/// <summary>
/// The remote-voice processing ported from the game (docs/reference/big-walk-voice-dsp.md),
/// checked against the constants read out of the game's bodies.
/// </summary>
public class VoiceDynamicsTests
{
    private const int Rate = 48_000;

    // [unit->REQ-RENDER-CLEAN]
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(0.85f, 0.85f)]
    [InlineData(-0.85f, -0.85f)]
    [InlineData(1f, 0.91f)]
    [InlineData(-1f, -0.91f)]
    public void Soft_clip_is_identity_below_the_knee_and_bends_above_it(float input, float expected)
    {
        Assert.Equal(expected, VoiceDynamics.SoftClip(input), 5);
    }

    [Fact]
    public void Soft_clip_never_reaches_the_ceiling()
    {
        foreach (var x in new[] { 1.5f, 3f, 10f, 1000f })
        {
            var y = VoiceDynamics.SoftClip(x);
            Assert.True(y < VoiceDynamics.SoftClipCeiling, $"{x} -> {y}");
            Assert.True(y > VoiceDynamics.SoftClipKnee);
            Assert.Equal(-y, VoiceDynamics.SoftClip(-x), 6);
        }
    }

    [Fact]
    public void Compressor_passes_signal_below_the_threshold_untouched()
    {
        var compressor = new VoiceCompressor();
        compressor.Prepare(Rate, 0.6f);
        for (var i = 0; i < 10_000; i++) Assert.Equal(0.5f, compressor.Process(0.5f));
        Assert.Equal(1f, compressor.Reduction);
    }

    // [unit->REQ-RENDER-CLEAN]
    [Fact]
    public void Compressor_settles_to_two_to_one_above_the_knee()
    {
        var compressor = new VoiceCompressor();
        compressor.Prepare(Rate, 0.6f);
        float y = 0f;
        for (var i = 0; i < 20_000; i++) y = compressor.Process(1f);
        // Envelope 1.0, threshold 0.6, full knee: gain = (0.6 / 1.0) ^ 0.5.
        Assert.Equal(MathF.Sqrt(0.6f), compressor.Reduction, 3);
        Assert.Equal(MathF.Sqrt(0.6f), y, 3);
    }

    [Fact]
    public void Compressor_blends_the_knee_halfway_through_it()
    {
        var compressor = new VoiceCompressor();
        compressor.Prepare(Rate, 0.6f);
        for (var i = 0; i < 20_000; i++) compressor.Process(0.72f);
        // Knee width 0.24: envelope 0.72 sits halfway, exponent 0.25.
        Assert.Equal(MathF.Pow(0.6f / 0.72f, 0.25f), compressor.Reduction, 3);
    }

    [Fact]
    public void Compressor_reset_clears_its_state()
    {
        var compressor = new VoiceCompressor();
        compressor.Prepare(Rate, 0.6f);
        for (var i = 0; i < 20_000; i++) compressor.Process(1f);
        compressor.Reset();
        Assert.Equal(1f, compressor.Reduction);
        Assert.Equal(0.1f, compressor.Process(0.1f));
    }

    // [unit->REQ-RENDER-CLEAN]
    [Fact]
    public void Gain_ramps_across_the_first_block_and_holds_on_the_next()
    {
        var dynamics = new VoiceDynamics();
        dynamics.Reset(1f);
        var block = new float[2880];
        Array.Fill(block, 0.1f);
        dynamics.Process(block, makeupGain: 2f, threshold: 0.6f, Rate);

        Assert.Equal(0.1f, dynamics.Arv, 4);  // float32 sum of 2880 samples drifts in the 6th place
        Assert.Equal(0.1f, block[0], 5);                       // ramp starts at the old gain
        Assert.True(block[^1] > 0.199f && block[^1] <= 0.2f);  // and ends at the new one
        Assert.True(dynamics.OutputArv > 0.149f && dynamics.OutputArv < 0.151f);

        Array.Fill(block, 0.1f);
        dynamics.Process(block, makeupGain: 2f, threshold: 0.6f, Rate);
        Assert.All(block, s => Assert.Equal(0.2f, s, 5));
        Assert.Equal(0.2f, dynamics.OutputArv, 4);
        Assert.Equal(1f, dynamics.Reduction);
        Assert.Equal(2, dynamics.Blocks);
    }

    // [unit->REQ-RENDER-CLEAN]
    [Fact]
    public void Full_scale_input_is_compressed_then_soft_clipped_under_the_ceiling()
    {
        var dynamics = new VoiceDynamics();
        dynamics.Reset(1f);
        var block = new float[2880];
        Array.Fill(block, 1f);
        dynamics.Process(block, makeupGain: 1f, threshold: 0.6f, Rate);

        Assert.Equal(1f, dynamics.Arv, 6);                       // metered before gain and compression
        Assert.Equal(1f, dynamics.PreClipPeak, 6);               // the compressor has not caught the first sample
        Assert.True(dynamics.Reduction < 0.8f);                  // but it settles well inside the block
        Assert.All(block, s => Assert.True(s < VoiceDynamics.SoftClipCeiling && s > 0.7f));
        Assert.Equal(0.91f, block[0], 5);                        // SoftClip(1.0)
    }

    [Fact]
    public void Silence_meters_zero_and_stays_silent()
    {
        var dynamics = new VoiceDynamics();
        dynamics.Reset(3f);
        var block = new float[2880];
        dynamics.Process(block, makeupGain: 3f, threshold: 0.6f, Rate);
        Assert.Equal(0f, dynamics.Arv);
        Assert.Equal(0f, dynamics.OutputArv);
        Assert.All(block, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void Reset_snaps_the_gain_and_clears_the_meters()
    {
        var dynamics = new VoiceDynamics();
        var block = new float[100];
        Array.Fill(block, 0.1f);
        dynamics.Process(block, 4f, 0.6f, Rate);
        dynamics.Reset(2f);
        Assert.Equal(0f, dynamics.Arv);
        Assert.Equal(0, dynamics.Blocks);
        Array.Fill(block, 0.1f);
        dynamics.Process(block, 2f, 0.6f, Rate);
        Assert.All(block, s => Assert.Equal(0.2f, s, 5));       // no ramp: gain started at 2
    }
}

public class VoiceMakeupGainTests
{
    private const float Frame = 1f / 60f;

    [Theory]
    [InlineData(0.132f, 0.528f)]
    [InlineData(0.2f, 0.6f)]
    [InlineData(0.001f, 0.04f)]
    public void Threshold_follows_the_target_level_capped_at_the_maximum(float targetArv, float threshold)
    {
        Assert.Equal(threshold, VoiceMakeupGain.ThresholdFor(targetArv), 5);
    }

    [Fact]
    public void Idle_gain_is_the_target_over_the_reference()
    {
        var makeup = new VoiceMakeupGain();
        Assert.Equal(1f, makeup.Evaluate(0f, isSpeaking: false, Frame, targetArv: 0.132f), 6);
        Assert.Equal(2f, makeup.Evaluate(0f, isSpeaking: false, Frame, targetArv: 0.264f), 5);
        Assert.Equal(0f, makeup.GainDb);
    }

    // [unit->REQ-RENDER-CLEAN]
    [Fact]
    public void Quiet_speech_is_lifted_toward_the_reference_within_the_first_second()
    {
        var makeup = new VoiceMakeupGain();
        float gain = 0f;
        for (var frame = 0; frame < 60; frame++)
            gain = makeup.Evaluate(arv: 0.0132f, isSpeaking: true, Frame, targetArv: 0.132f); // 20 dB under
        Assert.True(makeup.GainDb > 19f && makeup.GainDb <= 20.01f, $"GainDb {makeup.GainDb}");
        Assert.Equal(10f, gain, 0);
    }

    [Fact]
    public void Loud_speech_is_pulled_down()
    {
        var makeup = new VoiceMakeupGain();
        float gain = 0f;
        for (var frame = 0; frame < 60; frame++)
            gain = makeup.Evaluate(arv: 0.264f, isSpeaking: true, Frame, targetArv: 0.132f); // 6 dB over
        Assert.Equal(-6.02f, makeup.GainDb, 1);
        Assert.Equal(0.5f, gain, 2);
    }

    // [unit->REQ-RENDER-CLEAN]
    [Fact]
    public void Gain_freezes_between_bursts_and_slews_slowly_after_settling()
    {
        var makeup = new VoiceMakeupGain();
        for (var frame = 0; frame < 120; frame++) makeup.Evaluate(0.0132f, true, Frame, 0.132f);
        var settled = makeup.GainDb;
        var frozen = makeup.Evaluate(0f, isSpeaking: false, Frame, 0.132f);
        Assert.Equal(settled, makeup.GainDb);
        Assert.Equal(MathF.Pow(10f, settled / 20f), frozen, 5);

        // Louder now: the target drops by 20 dB but the gain comes down at 1 dB/s.
        makeup.Evaluate(0.132f, true, Frame, 0.132f);
        Assert.True(settled - makeup.GainDb <= SlewDown(Frame) + 1e-4f, $"{settled} -> {makeup.GainDb}");
    }

    private static float SlewDown(float dt) => VoiceMakeupGain.SlewDownDbPerSecond * dt;

    [Fact]
    public void Below_the_speech_floor_nothing_starts()
    {
        var makeup = new VoiceMakeupGain();
        for (var frame = 0; frame < 60; frame++) makeup.Evaluate(0.001f, true, Frame, 0.132f);
        Assert.Equal(0f, makeup.Level);
        Assert.Equal(0f, makeup.GainDb);
    }
}
