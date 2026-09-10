using TravelEar.Core;
using Xunit;

namespace TravelEar.Tests;

/// <summary>The Mixer Stage DSP (ADR-0002): dry gain, reverb sends, high cut, and the toggles.</summary>
public class MixerStageTests
{
    private const int Rate = 48_000;

    private static float[] Sine(float hz, int samples, float amplitude = 0.5f)
    {
        var block = new float[samples];
        for (var i = 0; i < samples; i++) block[i] = amplitude * MathF.Sin(2f * MathF.PI * hz * i / Rate);
        return block;
    }

    private static float Peak(ReadOnlySpan<float> block, int from = 0)
    {
        var peak = 0f;
        for (var i = from; i < block.Length; i++) peak = MathF.Max(peak, MathF.Abs(block[i]));
        return peak;
    }

    // [unit->REQ-MIXER-RESYNTH]
    [Fact]
    public void Master_off_is_an_exact_passthrough()
    {
        var stage = new MixerStage(Rate, 1.5f);
        var block = Sine(440f, 2880);
        var copy = (float[])block.Clone();
        stage.Process(block, -20f, -30f, 0f, 0f, MixerStageToggles.Off);
        Assert.Equal(copy, block);
    }

    [Fact]
    public void Self_ear_values_are_a_passthrough_with_everything_on()
    {
        var stage = new MixerStage(Rate, 1.5f);
        var block = Sine(440f, 2880);
        var copy = (float[])block.Clone();
        // The pitch shifter stays in the chain at ratio 1 (as in the game) and delays the dry path by
        // 768 samples, so the sample-exact check runs with it off; PitchShifterTests covers ratio 1.
        stage.Process(block, 0f, 0f, MixerStageModel.FloorDb, MixerStageModel.FloorDb, MixerStageToggles.All with { Pitch = false });
        for (var i = 0; i < block.Length; i++) Assert.Equal(copy[i], block[i], 5);
        Assert.Equal(1f, stage.DryGain);
        Assert.Equal(0f, stage.FallWetGain);
    }

    [Fact]
    public void Dry_in_dB_scales_the_frame()
    {
        var stage = new MixerStage(Rate, 1.5f);
        var block = Sine(440f, 2880);
        stage.Process(block, -20f, 0f, MixerStageModel.FloorDb, MixerStageModel.FloorDb, MixerStageToggles.All);
        Assert.InRange(Peak(block), 0.049f, 0.051f);
    }

    [Fact]
    public void Dry_toggle_off_keeps_unity_gain()
    {
        var stage = new MixerStage(Rate, 1.5f);
        var block = Sine(440f, 2880);
        stage.Process(block, -20f, 0f, MixerStageModel.FloorDb, MixerStageModel.FloorDb, MixerStageToggles.All with { Dry = false });
        Assert.InRange(Peak(block), 0.49f, 0.51f);
    }

    [Fact]
    public void Fall_send_leaves_a_tail_after_the_input_stops()
    {
        var stage = new MixerStage(Rate, 1.5f);
        var burst = Sine(440f, 4800);
        stage.Process(burst, 0f, 0f, 0f, MixerStageModel.FloorDb, MixerStageToggles.All);
        var silence = new float[4800];
        stage.Process(silence, 0f, 0f, 0f, MixerStageModel.FloorDb, MixerStageToggles.All);
        Assert.True(Peak(silence) > 0.001f, $"tail peak {Peak(silence)}");
    }

    [Fact]
    public void Fall_toggle_off_sends_nothing_to_the_reverb()
    {
        var stage = new MixerStage(Rate, 1.5f);
        var burst = Sine(440f, 4800);
        stage.Process(burst, 0f, 0f, 0f, MixerStageModel.FloorDb, MixerStageToggles.All with { ReverbFall = false, Pitch = false });
        var silence = new float[4800];
        stage.Process(silence, 0f, 0f, 0f, MixerStageModel.FloorDb, MixerStageToggles.All with { ReverbFall = false, Pitch = false });
        Assert.Equal(0f, Peak(silence));
    }

    [Fact]
    public void High_cut_attenuates_treble_more_than_bass()
    {
        var stage = new MixerStage(Rate, 1.5f);
        var treble = Sine(10_000f, Rate / 2);
        stage.Process(treble, 0f, -30f, MixerStageModel.FloorDb, MixerStageModel.FloorDb, MixerStageToggles.All);
        var stageB = new MixerStage(Rate, 1.5f);
        var bass = Sine(200f, Rate / 2);
        stageB.Process(bass, 0f, -30f, MixerStageModel.FloorDb, MixerStageModel.FloorDb, MixerStageToggles.All);
        var trebleOut = Peak(treble, Rate / 4);
        var bassOut = Peak(bass, Rate / 4);
        Assert.True(trebleOut < 0.1f, $"treble {trebleOut}");
        Assert.InRange(bassOut, 0.4f, 0.52f);
    }

    [Fact]
    public void High_toggle_off_leaves_treble_alone()
    {
        var stage = new MixerStage(Rate, 1.5f);
        var treble = Sine(10_000f, 4800);
        stage.Process(treble, 0f, -30f, MixerStageModel.FloorDb, MixerStageModel.FloorDb, MixerStageToggles.All with { High = false });
        Assert.InRange(Peak(treble), 0.49f, 0.51f);
    }

    // [unit->REQ-MIXER-RESYNTH]
    [Fact]
    public void Pitch_drops_the_dry_voice_and_leaves_the_reverb_returns_alone()
    {
        var stage = new MixerStage(Rate, 1.5f);
        var block = Sine(440f, Rate);
        stage.Process(block, 0f, 0f, MixerStageModel.FloorDb, MixerStageModel.FloorDb, MixerStageToggles.All, 0.5f);
        Assert.Equal(0.5f, stage.Pitch, 3);
        Assert.InRange(Fft.DominantFrequency(block.AsSpan(Rate / 2), 8192, Rate), 208f, 232f);

        var off = new MixerStage(Rate, 1.5f);
        var same = Sine(440f, Rate);
        off.Process(same, 0f, 0f, MixerStageModel.FloorDb, MixerStageModel.FloorDb, MixerStageToggles.All with { Pitch = false }, 0.5f);
        Assert.Equal(1f, off.Pitch, 3);
        Assert.InRange(Fft.DominantFrequency(same.AsSpan(Rate / 2), 8192, Rate), 430f, 450f);
    }

    [Fact]
    public void Reset_clears_the_tail()
    {
        var stage = new MixerStage(Rate, 1.5f);
        stage.Process(Sine(440f, 4800), 0f, 0f, 0f, MixerStageModel.FloorDb, MixerStageToggles.All);
        stage.Reset();
        var silence = new float[4800];
        stage.Process(silence, 0f, 0f, 0f, MixerStageModel.FloorDb, MixerStageToggles.All);
        Assert.Equal(0f, Peak(silence));
    }
}

/// <summary>The reverb approximation: decay follows the RT60 knob and it is stable.</summary>
public class ReverbTests
{
    private const int Rate = 48_000;

    private static float TailEnergy(Reverb reverb, int fromSample, int toSample)
    {
        var energy = 0f;
        for (var i = fromSample; i < toSample; i++)
        {
            var y = reverb.Process(0f);
            energy += y * y;
        }
        return energy;
    }

    // [unit->REQ-MIXER-RESYNTH]
    [Fact]
    public void An_impulse_decays_over_time()
    {
        var reverb = new Reverb(Rate, 1f);
        reverb.Process(1f);
        var early = TailEnergy(reverb, 0, Rate / 10);
        for (var i = 0; i < Rate; i++) reverb.Process(0f);
        var late = TailEnergy(reverb, 0, Rate / 10);
        Assert.True(early > 0f);
        Assert.True(late < early * 0.05f, $"early {early} late {late}");
    }

    [Fact]
    public void Longer_decay_leaves_more_late_energy()
    {
        var shortRev = new Reverb(Rate, 0.5f);
        var longRev = new Reverb(Rate, 3f);
        shortRev.Process(1f);
        longRev.Process(1f);
        for (var i = 0; i < Rate; i++) { shortRev.Process(0f); longRev.Process(0f); }
        Assert.True(TailEnergy(longRev, 0, Rate / 10) > TailEnergy(shortRev, 0, Rate / 10) * 10f);
    }

    [Fact]
    public void Sustained_input_stays_bounded()
    {
        var reverb = new Reverb(Rate, 3f);
        var peak = 0f;
        for (var i = 0; i < Rate * 5; i++)
            peak = MathF.Max(peak, MathF.Abs(reverb.Process(MathF.Sin(i * 0.05f))));
        Assert.True(peak < 4f, $"peak {peak}");
        Assert.False(float.IsNaN(peak));
    }

    [Fact]
    public void Reset_silences_the_tail()
    {
        var reverb = new Reverb(Rate, 2f);
        reverb.Process(1f);
        for (var i = 0; i < 100; i++) reverb.Process(0f);
        reverb.Reset();
        Assert.Equal(0f, TailEnergy(reverb, 0, 4800));
    }
}

/// <summary>The general biquad shares the game's kernel; the kinds the mod uses behave as their names say.</summary>
public class BiquadTests
{
    private const int Rate = 48_000;

    private static float Steady(Biquad f, float hz)
    {
        var block = new float[Rate];
        for (var i = 0; i < block.Length; i++) block[i] = MathF.Sin(2f * MathF.PI * hz * i / Rate);
        f.Process(block);
        var peak = 0f;
        for (var i = block.Length / 2; i < block.Length; i++) peak = MathF.Max(peak, MathF.Abs(block[i]));
        return peak;
    }

    // [unit->REQ-MIXER-RESYNTH]
    [Fact]
    public void High_pass_removes_bass_and_keeps_treble()
    {
        var f = new Biquad(Rate);
        f.Configure(Biquad.Kind.HighPass, 300f, 0.4f, 0f, 1f, 1f);
        Assert.True(Steady(f, 30f) < 0.1f);
        f.Reset();
        Assert.InRange(Steady(f, 3000f), 0.9f, 1.05f);
    }

    [Fact]
    public void High_shelf_at_minus_30_dB_cuts_treble_only()
    {
        var f = new Biquad(Rate);
        f.Configure(Biquad.Kind.HighShelf, 3000f, 0.7f, -30f, 1f, 1f);
        Assert.InRange(Steady(f, 100f), 0.9f, 1.05f);
        f.Reset();
        Assert.True(Steady(f, 12_000f) < 0.06f);
    }

    [Fact]
    public void Low_pass_keeps_bass_and_removes_treble()
    {
        var f = new Biquad(Rate);
        f.Configure(Biquad.Kind.LowPass, 1500f, 0.6f, 0f, 1f, 1f);
        Assert.InRange(Steady(f, 100f), 0.9f, 1.05f);
        f.Reset();
        Assert.True(Steady(f, 12_000f) < 0.1f);
    }

    [Fact]
    public void Peaking_kind_matches_the_dedicated_voice_eq()
    {
        var general = new Biquad(Rate);
        general.Configure(Biquad.Kind.PeakingEq, 400f, 0.3f, 30f, 0.03f, 1f);
        var dedicated = PeakingEq.GameVoiceEq(1f, Rate);
        var a = new float[4800];
        var b = new float[4800];
        for (var i = 0; i < a.Length; i++) a[i] = b[i] = 0.5f * MathF.Sin(2f * MathF.PI * 400f * i / Rate);
        general.Process(a);
        dedicated.Process(b);
        for (var i = 0; i < a.Length; i++) Assert.Equal(b[i], a[i], 5);
    }
}
