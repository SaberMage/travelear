using System;
using TravelEar.Core;
using Xunit;

namespace TravelEar.Tests;

/// <summary>
/// The phase-vocoder pitch shifter and its FFT (the voice mixer's Dry group Pitch Shifter and
/// the super-wet group pitch, catalogue 5.5).
/// </summary>
public class PitchShifterTests
{
    private const int Rate = 48_000;

    private static float[] Sine(float hz, int samples, float amplitude = 0.5f)
    {
        var block = new float[samples];
        for (var i = 0; i < samples; i++) block[i] = amplitude * MathF.Sin(2f * MathF.PI * hz * i / Rate);
        return block;
    }

    private static float Peak(ReadOnlySpan<float> block, int from)
    {
        var peak = 0f;
        for (var i = from; i < block.Length; i++) peak = MathF.Max(peak, MathF.Abs(block[i]));
        return peak;
    }

    // [unit->REQ-MIXER-RESYNTH]
    [Fact]
    public void Fft_round_trip_scales_by_the_length()
    {
        const int n = 16;
        var buffer = new float[2 * n];
        var expected = new float[2 * n];
        for (var i = 0; i < n; i++) { buffer[2 * i] = i % 3 - 1f; buffer[2 * i + 1] = 0.25f * i; }
        Array.Copy(buffer, expected, buffer.Length);
        Fft.Transform(buffer, n, -1);
        Fft.Transform(buffer, n, 1);
        for (var i = 0; i < 2 * n; i++) Assert.Equal(expected[i] * n, buffer[i], 3);
    }

    // [unit->REQ-MIXER-RESYNTH]
    [Fact]
    public void Fft_finds_a_pure_tone_in_its_bin()
    {
        const int n = 1024;
        var tone = Sine(Rate * 8f / n, n); // exactly bin 8
        Assert.Equal(Rate * 8f / n, Fft.DominantFrequency(tone, n, Rate), 1);
    }

    // [unit->REQ-MIXER-RESYNTH]
    [Fact]
    public void Ratio_one_passes_the_tone_at_its_pitch_and_near_its_level()
    {
        var shifter = new PitchShifter(Rate);
        var block = Sine(440f, Rate);
        shifter.Process(block, 1f);
        var settled = block.AsSpan(Rate / 2);
        Assert.InRange(Fft.DominantFrequency(settled, 4096, Rate), 420f, 460f);
        Assert.InRange(Peak(block, Rate / 2), 0.4f, 0.6f);
        Assert.Equal(768, shifter.LatencySamples);
    }

    // [unit->REQ-MIXER-RESYNTH]
    [Theory]
    [InlineData(0.5f, 220f)]
    [InlineData(0.75f, 330f)]
    [InlineData(1.5f, 660f)]
    public void Ratio_moves_the_tone_by_the_ratio(float ratio, float expectedHz)
    {
        var shifter = new PitchShifter(Rate);
        var block = Sine(440f, Rate);
        shifter.Process(block, ratio);
        var hz = Fft.DominantFrequency(block.AsSpan(Rate / 2), 8192, Rate);
        Assert.InRange(hz, expectedHz - 12f, expectedHz + 12f);
        // Downward ratios (the game's only use: VoicePitch <= 1) keep the level; an upward ratio
        // leaves target bins empty in the bin re-mapping and comes out quieter.
        Assert.InRange(Peak(block, Rate / 2), ratio > 1f ? 0.15f : 0.35f, 0.75f);
    }

    // [unit->REQ-MIXER-RESYNTH]
    [Fact]
    public void Silence_in_is_silence_out_and_reset_clears_the_fifos()
    {
        var shifter = new PitchShifter(Rate);
        var block = Sine(440f, 4096);
        shifter.Process(block, 0.5f);
        shifter.Reset();
        var silence = new float[4096];
        shifter.Process(silence, 0.5f);
        Assert.Equal(0f, Peak(silence, 0));
    }
}
