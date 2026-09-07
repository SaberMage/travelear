using TravelEar.Core;
using Xunit;

namespace TravelEar.Tests;

/// <summary>
/// The game's voice EQ kernel (docs/reference/big-walk-voice-dsp.md section 5) ported for the
/// Encoder feed: RBJ peaking filter, wet mix (dryWet * vol), dry mix (1 - dryWet), clamp +-1.
/// </summary>
public class PeakingEqTests
{
    private const int Rate = 48_000;

    private static float SteadyAmplitude(PeakingEq eq, float hz)
    {
        var block = new float[Rate]; // 1 s: settle over the first half, measure the second
        for (var i = 0; i < block.Length; i++) block[i] = MathF.Sin(2f * MathF.PI * hz * i / Rate);
        eq.Process(block);
        var peak = 0f;
        for (var i = block.Length / 2; i < block.Length; i++) peak = MathF.Max(peak, MathF.Abs(block[i]));
        return peak;
    }

    // [unit->REQ-EAR-SELF]
    [Fact]
    public void Dry_wet_zero_is_an_exact_passthrough()
    {
        var eq = PeakingEq.GameVoiceEq(0f, Rate);
        var block = new float[480];
        for (var i = 0; i < block.Length; i++) block[i] = MathF.Sin(i * 0.05f) * 0.7f;
        var copy = (float[])block.Clone();
        Assert.True(eq.IsPassthrough);
        eq.Process(block);
        Assert.Equal(copy, block);
    }

    [Fact]
    public void Full_wet_at_the_centre_frequency_is_the_gain_times_vol()
    {
        // +30 dB at 400 Hz = x31.62 linear, times Vol 0.03 = 0.949, on a unit sine.
        var eq = PeakingEq.GameVoiceEq(1f, Rate);
        var amplitude = SteadyAmplitude(eq, 400f);
        Assert.InRange(amplitude, 0.90f, 0.98f);
    }

    [Fact]
    public void Full_wet_falls_away_from_the_centre_frequency()
    {
        var centre = SteadyAmplitude(PeakingEq.GameVoiceEq(1f, Rate), 400f);
        var high = SteadyAmplitude(PeakingEq.GameVoiceEq(1f, Rate), 8000f);
        var low = SteadyAmplitude(PeakingEq.GameVoiceEq(1f, Rate), 40f);
        Assert.True(high < centre * 0.5f, $"8 kHz {high} vs 400 Hz {centre}");
        Assert.True(low < centre * 0.5f, $"40 Hz {low} vs 400 Hz {centre}");
    }

    [Fact]
    public void Half_wet_mixes_dry_and_wet_paths()
    {
        // Far from the band the wet path is ~Vol (0.03) of the input, so half wet ~= 0.5 + 0.5 * small.
        var eq = PeakingEq.GameVoiceEq(0.5f, Rate);
        var amplitude = SteadyAmplitude(eq, 8000f);
        Assert.InRange(amplitude, 0.45f, 0.62f);
    }

    [Fact]
    public void Output_never_exceeds_the_clamp()
    {
        var eq = new PeakingEq(400f, 0.3f, 30f, 1f, 1f, Rate); // Vol 1: +30 dB unclamped would be 31x
        var block = new float[Rate / 10];
        for (var i = 0; i < block.Length; i++) block[i] = MathF.Sin(2f * MathF.PI * 400f * i / Rate);
        eq.Process(block);
        foreach (var s in block) Assert.InRange(s, -1f, 1f);
    }

    [Fact]
    public void Reset_clears_the_delay_line()
    {
        var eq = PeakingEq.GameVoiceEq(1f, Rate);
        var block = new float[480];
        for (var i = 0; i < block.Length; i++) block[i] = 0.5f;
        eq.Process(block);
        eq.Reset();
        var zeros = new float[480];
        eq.Process(zeros);
        foreach (var s in zeros) Assert.Equal(0f, s);
    }
}
