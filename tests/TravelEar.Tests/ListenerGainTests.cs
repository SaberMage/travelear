using System;
using TravelEar.Core;
using Xunit;

namespace TravelEar.Tests;

/// <summary>
/// The listener-side gain terms from the effects catalogue (sections 5.1b, 5.5, 5.15): the
/// source volume chain and the master limiter.
/// </summary>
public class ListenerGainTests
{
    // [unit->REQ-MIXER-RESYNTH]
    [Fact]
    public void Indoor_attenuation_settles_at_minus_six_dB_fully_indoors_and_unity_outdoors()
    {
        var v = new SourceVolume(true, true);
        for (var i = 0; i < 300; i++) v.Step(0f, 0f, 1f / 60f);
        Assert.Equal(0.5f, v.IndoorGain, 3);
        Assert.Equal(0.5f, v.Gain, 3);
        for (var i = 0; i < 300; i++) v.Step(1f, 0f, 1f / 60f);
        Assert.Equal(1f, v.IndoorGain, 3);
    }

    // [unit->REQ-MIXER-RESYNTH]
    [Fact]
    public void Indoor_attenuation_smooths_at_three_per_second()
    {
        var v = new SourceVolume(true, false);
        v.Step(0f, 0f, 1f / 60f);
        // one frame: 1 + (0.5 - 1) * clamp01(dt * 3) = 1 - 0.5 * 0.05
        Assert.Equal(1f - 0.5f * 0.05f, v.IndoorGain, 4);
    }

    // [unit->REQ-MIXER-RESYNTH]
    [Fact]
    public void Speechlessness_fades_the_voice_to_silence_at_the_zone_centre()
    {
        var v = new SourceVolume(true, true);
        for (var i = 0; i < 300; i++) v.Step(1f, 1f, 1f / 60f);
        Assert.Equal(0f, v.SpeechlessGain, 3);
        Assert.Equal(0f, v.Gain, 3);
        for (var i = 0; i < 300; i++) v.Step(1f, 0.25f, 1f / 60f);
        Assert.Equal(0.75f, v.SpeechlessGain, 3);
    }

    [Fact]
    public void Disabled_terms_do_not_change_the_gain()
    {
        var v = new SourceVolume(false, false);
        for (var i = 0; i < 300; i++) v.Step(0f, 1f, 1f / 60f);
        Assert.Equal(1f, v.Gain, 5);
    }

    // [unit->REQ-MIXER-RESYNTH]
    [Fact]
    public void Master_limiter_holds_a_hot_tone_near_the_threshold_and_leaves_a_quiet_one_alone()
    {
        var limiter = new MasterLimiter(48_000);
        var hot = Tone(48_000, 1000f, 1f, 0.5f);
        limiter.Process(hot);
        var tail = hot.AsSpan(hot.Length / 2);
        var peakDb = 20 * Math.Log10(Max(tail));
        // 0 dBFS in, -3 dB threshold, 10:1, 20 dB knee: the soft knee gives about 3.8 dB of reduction.
        Assert.InRange(peakDb, -5.5, -2.0);
        Assert.InRange(limiter.ReductionDb, 2.0, 5.5);

        limiter.Reset();
        var quiet = Tone(48_000, 1000f, 0.1f, 0.25f); // -20 dBFS: below the knee's lower edge (-13 dB)
        limiter.Process(quiet);
        Assert.InRange(20 * Math.Log10(Max(quiet.AsSpan(quiet.Length / 2))), -20.5, -19.5);
    }

    // [unit->REQ-MIXER-RESYNTH]
    [Fact]
    public void Compressor_soft_knee_is_continuous_and_matches_the_hard_slope_beyond_it()
    {
        var c = new Compressor(48_000, -3f, 0.1f, 125f, 0f, 10f, 20f);
        Assert.Equal(0f, c.ReductionFor(-10f), 5);
        Assert.Equal(0f, c.ReductionFor(-11f), 5);
        Assert.Equal(10f * 0.9f, c.ReductionFor(10f), 4);
        Assert.Equal(15f * 0.9f, c.ReductionFor(15f), 4);
        Assert.True(c.ReductionFor(0f) > 0f && c.ReductionFor(0f) < 9f);
        var hard = new Compressor(48_000, -3f, 0.1f, 125f, 0f, 10f, 0f);
        Assert.Equal(0f, hard.ReductionFor(-0.01f), 5);
        Assert.Equal(2.7f, hard.ReductionFor(3f), 4);
    }

    // [unit->REQ-RENDER-MEGAPHONE]
    [Fact]
    public void Megaphone_mixer_constants_are_the_asset_values()
    {
        Assert.Equal(10f, MegaphoneVoice.UnityCompressorAttackMs);
        Assert.Equal(1000f, MegaphoneVoice.UnityCompressorReleaseMs);
        Assert.Equal(250f, MegaphoneVoice.PostCompressorReleaseMs);
        Assert.Equal(5f, MegaphoneVoice.PostCompressorRatio);
        Assert.Equal(-1000f, MegaphoneVoice.MixerReverb.RoomMb);
        Assert.Equal(2f, MegaphoneVoice.MixerReverb.DecayTimeS);
    }

    // [unit->REQ-RENDER-MEGAPHONE]
    [Fact]
    public void Megaphone_mixer_delays_the_output_by_the_echo_and_rolls_off_above_five_kHz()
    {
        var m = new MegaphoneVoice(48_000);
        var input = new float[48_000];
        input[0] = 1f;
        var output = new float[input.Length];
        m.Process(input, output, new MegaphoneToggles(true, false, false, false, true));
        // Nothing before 100 ms (the echo is wet-only), energy after it.
        var before = Max(output.AsSpan(0, 4700));
        var after = Max(output.AsSpan(4800, 4800));
        Assert.True(before < 1e-4f, $"energy before the echo delay: {before}");
        Assert.True(after > 1e-3f, $"no energy after the echo delay: {after}");
    }

    private static float[] Tone(int rate, float hz, float amplitude, float seconds)
    {
        var x = new float[(int)(rate * seconds)];
        for (var i = 0; i < x.Length; i++) x[i] = amplitude * MathF.Sin(2f * MathF.PI * hz * i / rate);
        return x;
    }

    private static float Max(ReadOnlySpan<float> x)
    {
        var m = 0f;
        foreach (var v in x) m = MathF.Max(m, MathF.Abs(v));
        return m;
    }
}
