using System;
using TravelEar.Core;
using Xunit;

namespace TravelEar.Tests;

/// <summary>The red bells' super-wet bloom and the chorus in it (catalogue rows 9-10, section 5.5).</summary>
public class SpeechlessBloomTests
{
    private const int Rate = 48_000;

    private static float[] Sine(float hz, int samples, float amplitude = 0.5f)
    {
        var block = new float[samples];
        for (var i = 0; i < samples; i++) block[i] = amplitude * MathF.Sin(2f * MathF.PI * hz * i / Rate);
        return block;
    }

    private static float Energy(ReadOnlySpan<float> block)
    {
        var sum = 0.0;
        for (var i = 0; i < block.Length; i++) sum += block[i] * (double)block[i];
        return (float)sum;
    }

    // [unit->REQ-MIXER-RESYNTH]
    [Fact]
    public void Chorus_dry_only_is_a_passthrough_and_wet_taps_add_delayed_copies()
    {
        var dry = new Chorus(Rate, 1f, 0f, 0f, 0f, 100f, 0.5f, 0.5f);
        var block = Sine(440f, 4800);
        var copy = (float[])block.Clone();
        dry.Process(block);
        for (var i = 0; i < block.Length; i++) Assert.Equal(copy[i], block[i], 5);

        var chorus = Chorus.SuperWet(Rate);
        var impulse = new float[Rate / 4];
        impulse[0] = 1f;
        chorus.Process(impulse);
        Assert.Equal(1f, impulse[0], 4); // the dry copy
        var tail = 0f;
        for (var i = 1; i < impulse.Length; i++) tail += MathF.Abs(impulse[i]);
        Assert.InRange(tail, 1.4f, 1.6f); // wet 0.75 + 0.5 + 0.25, spread by the interpolation
    }

    // [unit->REQ-MIXER-RESYNTH]
    [Fact]
    public void Closed_return_renders_nothing_and_does_no_work()
    {
        var bloom = new SpeechlessBloom(Rate);
        var voice = Sine(440f, 2880);
        var output = new float[2880];
        Assert.False(bloom.Process(voice, output, 0f, 1f, SpeechlessBloom.FloorDb));
        Assert.Equal(0f, Energy(output));
        Assert.False(bloom.Active);
    }

    // [unit->REQ-MIXER-RESYNTH]
    [Fact]
    public void Open_return_blooms_the_voice_and_rings_out_after_it_closes()
    {
        var bloom = new SpeechlessBloom(Rate);
        var voice = Sine(440f, 2880);
        var output = new float[2880];
        var opened = 0f;
        for (var i = 0; i < 20; i++)
        {
            Assert.True(bloom.Process(voice, output, 0f, 0.8f, -8.7f)); // sp 0.75
            opened += Energy(output);
        }
        Assert.True(opened > 0f);
        Assert.True(bloom.Active);
        Assert.Equal(0.8f, bloom.Pitch, 3);

        var silence = new float[2880];
        Assert.True(bloom.Process(silence, output, 0f, 1f, SpeechlessBloom.FloorDb));
        Assert.True(Energy(output) > 0f, "the 6.8 s tail keeps ringing after the return closes");
        Assert.True(bloom.Active);
        for (var i = 0; i < (int)(SpeechlessBloom.TailSeconds * Rate / 2880) + 2; i++) bloom.Process(silence, output, 0f, 1f, SpeechlessBloom.FloorDb);
        Assert.False(bloom.Active);
        Assert.False(bloom.Process(silence, output, 0f, 1f, SpeechlessBloom.FloorDb));
    }

    // [unit->REQ-MIXER-RESYNTH]
    [Fact]
    public void Return_level_follows_the_game_dB_and_the_bus_gain()
    {
        var quiet = new SpeechlessBloom(Rate);
        var loud = new SpeechlessBloom(Rate);
        var voice = Sine(440f, 2880);
        var a = new float[2880];
        var b = new float[2880];
        var ea = 0f;
        var eb = 0f;
        for (var i = 0; i < 10; i++)
        {
            quiet.Process(voice, a, 0f, 1f, -34.1f); // sp 0.25
            loud.Process(voice, b, 0f, 1f, 0f);      // sp 1
            ea += Energy(a); eb += Energy(b);
        }
        Assert.InRange(10f * MathF.Log10(eb / ea), 33f, 35.5f);
        Assert.Equal(MixerStageModel.Gain(-34.1f), quiet.ReturnGain, 4);
    }
}
