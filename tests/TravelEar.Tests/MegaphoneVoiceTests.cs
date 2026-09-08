using TravelEar.Core;
using Xunit;

namespace TravelEar.Tests;

/// <summary>The game's BitCrusher kernel (managed Burst fallback) checked against its behaviour.</summary>
public class BitCrusherTests
{
    private const int Rate = 48_000;

    // [unit->REQ-RENDER-MEGAPHONE]
    [Fact]
    public void Step_is_the_integer_hold_length()
    {
        Assert.Equal(12, new BitCrusher(24, 4000, 0.5f, 0.5f).StepFor(Rate));
        Assert.Equal(10, new BitCrusher(24, 4800, 0.5f, 0.5f).StepFor(Rate));
        Assert.Equal(1, new BitCrusher(24, 96_000, 0.5f, 0.5f).StepFor(Rate));
    }

    [Fact]
    public void Full_wet_no_smooth_holds_each_blocks_first_sample()
    {
        var bc = new BitCrusher(24, 4800, 1f, 0f);
        var block = new float[30];
        for (var i = 0; i < block.Length; i++) block[i] = i * 0.01f;
        bc.Process(block, Rate);
        for (var i = 0; i < 10; i++) Assert.Equal(0.00f, block[i], 4);
        for (var i = 10; i < 20; i++) Assert.Equal(0.10f, block[i], 4);
        for (var i = 20; i < 30; i++) Assert.Equal(0.20f, block[i], 4);
    }

    [Fact]
    public void Full_smooth_ramps_linearly_to_the_next_block()
    {
        var bc = new BitCrusher(24, 4800, 1f, 1f);
        var block = new float[20];
        for (var i = 0; i < block.Length; i++) block[i] = i * 0.01f;
        bc.Process(block, Rate);
        for (var i = 0; i < 10; i++) Assert.Equal(i * 0.01f, block[i], 4); // ramp 0 -> 0.1 over the hold
        for (var i = 10; i < 20; i++) Assert.Equal(0.10f, block[i], 4);   // last block: no successor, held
    }

    [Fact]
    public void Half_smooth_ramps_halfway()
    {
        var bc = new BitCrusher(24, 4800, 1f, 0.5f);
        var block = new float[20];
        for (var i = 0; i < block.Length; i++) block[i] = i * 0.01f;
        bc.Process(block, Rate);
        Assert.Equal(0.045f, block[9], 4);
    }

    [Fact]
    public void Dry_wet_mixes_the_held_value_with_the_input()
    {
        var bc = new BitCrusher(24, 4800, 0.5f, 0f);
        var block = new float[10];
        for (var i = 0; i < block.Length; i++) block[i] = 0.4f;
        block[0] = 0.2f;
        bc.Process(block, Rate);
        Assert.Equal(0.2f, block[0], 4);
        Assert.Equal(0.3f, block[5], 4); // 0.5 * held 0.2 + 0.5 * 0.4
    }

    [Fact]
    public void Dry_wet_zero_is_identity()
    {
        var bc = new BitCrusher(24, 4000, 0f, 0.5f);
        var block = new float[48];
        for (var i = 0; i < block.Length; i++) block[i] = MathF.Sin(i * 0.3f);
        var copy = (float[])block.Clone();
        bc.Process(block, Rate);
        Assert.Equal(copy, block);
    }

    [Fact]
    public void Quantization_clamps_to_unity_and_rounds_at_low_bit_depth()
    {
        var bc = new BitCrusher(2, 48_000, 1f, 0f); // ampVal 2, crushScale log(3)/log(25)/2
        var block = new[] { 5f, -5f, 0.3f };
        bc.Process(block, Rate);
        Assert.Equal(1f, block[0]);
        Assert.Equal(-1f, block[1]);
        var expected = (int)(0.3f * 2f + 0.5f) * (MathF.Log(3f) / MathF.Log(25f) / 2f);
        Assert.Equal(expected, block[2], 5);
    }

    [Fact]
    public void Twenty_four_bit_quantization_is_inaudible()
    {
        var bc = new BitCrusher(24, 48_000, 1f, 0f);
        var block = new float[100];
        for (var i = 0; i < block.Length; i++) block[i] = MathF.Sin(i * 0.1f) * 0.9f;
        var copy = (float[])block.Clone();
        bc.Process(block, Rate);
        for (var i = 0; i < block.Length; i++) Assert.Equal(copy[i], block[i], 5);
    }
}

/// <summary>The FMOD-style compressor approximation behind the megaphone mixer.</summary>
public class CompressorTests
{
    private const int Rate = 48_000;

    private static float SteadyPeak(Compressor c, float amplitude)
    {
        var block = new float[Rate / 2];
        for (var i = 0; i < block.Length; i++) block[i] = amplitude * MathF.Sin(2f * MathF.PI * 440f * i / Rate);
        c.Process(block);
        var peak = 0f;
        for (var i = block.Length / 2; i < block.Length; i++) peak = MathF.Max(peak, MathF.Abs(block[i]));
        return peak;
    }

    // [unit->REQ-RENDER-MEGAPHONE]
    [Fact]
    public void Below_threshold_only_the_makeup_gain_applies()
    {
        var c = new Compressor(Rate, -20f, 50f, 50f, 6f);
        var peak = SteadyPeak(c, 0.05f); // -26 dB
        Assert.InRange(peak, 0.05f * 1.99f * 0.97f, 0.05f * 1.99f * 1.03f);
        Assert.Equal(0f, c.ReductionDb);
    }

    [Fact]
    public void Above_threshold_the_excess_is_reduced_by_the_ratio()
    {
        var c = new Compressor(Rate, -20f, 50f, 50f, 0f);
        var peak = SteadyPeak(c, 1f); // 20 dB over: reduction 20 - 20/2.5 = 12 dB on the detected level
        // The attack-timed detector settles on the sine's mean rectified level (~-4 dB below its
        // peak), as FMOD's does, so the reduction lands between the mean and peak predictions.
        var onPeak = MathF.Pow(10f, -12f / 20f);        // 0.251
        var onMean = MathF.Pow(10f, -(16f - 16f / 2.5f) / 20f); // 0.331
        Assert.InRange(peak, onPeak * 0.9f, onMean * 1.1f);
        Assert.InRange(c.ReductionDb, 8f, 12.5f);
    }

    [Fact]
    public void Parameters_are_clamped_to_fmod_ranges()
    {
        var c = new Compressor(Rate, -90f, 0f, 0.25f, 40f);
        Assert.Equal(-60f, c.ThresholdDb);
        Assert.Equal(0.1f, c.AttackMs);
        Assert.Equal(10f, c.ReleaseMs);
        Assert.Equal(30f, c.MakeupDb);
    }

    [Fact]
    public void Reset_clears_the_detector()
    {
        var c = new Compressor(Rate, -20f, 50f, 5000f, 0f);
        SteadyPeak(c, 1f);
        c.Reset();
        var quiet = new float[10];
        c.Process(quiet);
        Assert.Equal(0f, c.ReductionDb);
    }
}

/// <summary>The Megaphone voice chain at the Self-Ear.</summary>
public class MegaphoneVoiceTests
{
    private const int Rate = 48_000;

    private static float[] Sine(float hz, int n, float a = 0.5f)
    {
        var block = new float[n];
        for (var i = 0; i < n; i++) block[i] = a * MathF.Sin(2f * MathF.PI * hz * i / Rate);
        return block;
    }

    private static float Peak(ReadOnlySpan<float> block, int from = 0)
    {
        var peak = 0f;
        for (var i = from; i < block.Length; i++) peak = MathF.Max(peak, MathF.Abs(block[i]));
        return peak;
    }

    // [unit->REQ-RENDER-MEGAPHONE]
    [Fact]
    public void Master_off_renders_silence()
    {
        var m = new MegaphoneVoice(Rate);
        var voice = Sine(440f, 2880);
        var output = new float[2880];
        Array.Fill(output, 0.3f);
        m.Process(voice, output, MegaphoneToggles.Off);
        Assert.Equal(0f, Peak(output));
    }

    [Fact]
    public void Output_is_a_transformed_copy_and_leaves_the_input_alone()
    {
        var m = new MegaphoneVoice(Rate);
        var voice = Sine(440f, 2880);
        var copy = (float[])voice.Clone();
        var output = new float[2880];
        m.Process(voice, output, MegaphoneToggles.All);
        Assert.Equal(copy, voice);
        var differs = false;
        for (var i = 0; i < voice.Length; i++) if (MathF.Abs(output[i] - voice[i]) > 1e-4f) { differs = true; break; }
        Assert.True(differs);
    }

    [Fact]
    public void High_pass_removes_bass()
    {
        var m = new MegaphoneVoice(Rate);
        var voice = Sine(40f, Rate / 2);
        var output = new float[voice.Length];
        m.Process(voice, output, MegaphoneToggles.All with { Crusher = false, Compressors = false });
        Assert.True(Peak(output, output.Length / 2) < 0.1f);
    }

    [Fact]
    public void Compressors_level_a_loud_voice_toward_the_threshold()
    {
        var m = new MegaphoneVoice(Rate);
        var voice = Sine(1000f, Rate / 2, 0.9f);
        var output = new float[voice.Length];
        m.Process(voice, output, MegaphoneToggles.All with { Crusher = false, HighPass = false });
        var peak = Peak(output, output.Length / 2);
        Assert.True(peak < 0.5f, $"peak {peak}");
        Assert.True(m.CompressorReductionDb > 5f);
    }

    [Fact]
    public void Crusher_holds_samples_at_the_megaphone_rate()
    {
        var m = new MegaphoneVoice(Rate);
        var voice = new float[24];
        for (var i = 0; i < voice.Length; i++) voice[i] = i * 0.01f;
        var output = new float[24];
        m.Process(voice, output, MegaphoneToggles.All with { HighPass = false, Compressors = false });
        // step 12, dry/wet 0.5, smooth 0.5: sample 0 = 0.5*q(0) + 0.5*0 = 0
        Assert.Equal(0f, output[0], 4);
        // sample 6 of the first hold: held 0 ramping toward 0.12 by half -> 0.03; mixed half with 0.06 -> 0.045
        Assert.Equal(0.045f, output[6], 4);
    }
}
