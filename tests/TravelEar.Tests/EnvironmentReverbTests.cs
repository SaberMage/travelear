using TravelEar.Core;
using Xunit;

namespace TravelEar.Tests;

/// <summary>
/// The environment reverb (M3 T2b): the game's fourteen SFX Reverb formulas, the SfxReverb
/// approximation's exact targets (levels, onsets, decay, shelves) and the stage's bus arithmetic.
/// </summary>
public class EnvironmentReverbTests
{
    private const int Rate = 48_000;

    /// <summary>The doc's worked indoor corridor: O 0.1, RS 0.3, RT 0.6, D 0.5.</summary>
    private static EnvironmentReverbParams Corridor => EnvironmentReverbParams.FromListener(0.3f, 0.1f, 0.6f, 0.5f);

    private static float[] Sine(float hz, int samples, float amplitude = 0.5f)
    {
        var block = new float[samples];
        for (var i = 0; i < samples; i++) block[i] = amplitude * MathF.Sin(2f * MathF.PI * hz * i / Rate);
        return block;
    }

    private static float Rms(ReadOnlySpan<float> block, int from, int to)
    {
        to = Math.Min(to, block.Length);
        if (to <= from) return 0f;
        double sum = 0;
        for (var i = from; i < to; i++) sum += (double)block[i] * block[i];
        return (float)Math.Sqrt(sum / (to - from));
    }

    private static int FirstNonZero(ReadOnlySpan<float> block, float threshold = 1e-6f)
    {
        for (var i = 0; i < block.Length; i++) if (MathF.Abs(block[i]) > threshold) return i;
        return -1;
    }

    /// <summary>Renders <paramref name="seconds"/> of wet output for a unit impulse through a fresh SfxReverb.</summary>
    private static float[] ImpulseResponse(in EnvironmentReverbParams p, float seconds)
    {
        var reverb = new SfxReverb(Rate);
        reverb.Configure(p);
        var n = (int)(seconds * Rate);
        var input = new float[n];
        input[0] = 1f;
        var wet = new float[n];
        for (var offset = 0; offset < n; offset += 2880)
        {
            var len = Math.Min(2880, n - offset);
            reverb.Process(input.AsSpan(offset, len), wet.AsSpan(offset, len));
        }
        return wet;
    }

    // [unit->REQ-MIXER-RESYNTH]
    [Fact]
    public void Formulas_reproduce_the_documented_corridor()
    {
        var p = Corridor;
        Assert.Equal(-225f, p.DryLevelMb, 1);
        Assert.Equal(-250f, p.RoomMb, 1);
        Assert.Equal(-500f, p.RoomHfMb, 1);
        Assert.Equal(-1150f, p.RoomLfMb, 1);
        Assert.Equal(2.16f, p.DecayTimeS, 2);
        Assert.Equal(0.55f, p.DecayHfRatio, 3);
        Assert.Equal(-2190f, p.ReflectionsMb, 1);
        Assert.Equal(0.045f, p.ReflectDelayS, 4);
        Assert.Equal(-610f, p.ReverbMb, 1);
        Assert.Equal(0.03f, p.ReverbDelayS, 4);
        Assert.Equal(4800f, p.HfReferenceHz, 1);
        Assert.Equal(330f, p.LfReferenceHz, 1);
        Assert.Equal(50f, p.DiffusionPct, 1);
        Assert.Equal(65f, p.DensityPct, 1);
    }

    [Fact]
    public void Formulas_clamp_outdoors_to_no_room()
    {
        var p = EnvironmentReverbParams.FromListener(0.8f, 0.9f, 0.3f, 0.5f);
        Assert.Equal(-670f, p.RoomMb, 1);
        Assert.Equal(-8370f, p.ReflectionsMb, 1); // the reference doc's "-10000 (clamped)" is an arithmetic slip
        Assert.Equal(-2880f, p.ReverbMb, 1);
        Assert.Equal(-10_000f, EnvironmentReverbParams.FromListener(1f, 0.5f, 0.3f, 0.5f).ReflectionsMb, 1);
        Assert.Equal(0.23f, p.DecayTimeS, 2);
        Assert.Equal(16f, EnvironmentReverbParams.FromListener(0f, 0f, 1f, 0f).DecayTimeS, 3);
    }

    [Fact]
    public void Clamped_applies_unity_ranges_and_neutralizes_nan()
    {
        var p = new EnvironmentReverbParams(50f, -20_000f, float.NaN, 0f, 99f, 5f, 2000f, 1f, 5000f, 1f, 1f, 5000f, 150f, -5f, float.NaN, 0f).Clamped();
        Assert.Equal(0f, p.DryLevelMb);
        Assert.Equal(-10_000f, p.RoomMb);
        Assert.Equal(0f, p.RoomHfMb);
        Assert.Equal(20f, p.DecayTimeS);
        Assert.Equal(2f, p.DecayHfRatio);
        Assert.Equal(1000f, p.ReflectionsMb);
        Assert.Equal(0.3f, p.ReflectDelayS);
        Assert.Equal(2000f, p.ReverbMb);
        Assert.Equal(0.1f, p.ReverbDelayS);
        Assert.Equal(20f, p.HfReferenceHz);
        Assert.Equal(1000f, p.LfReferenceHz);
        Assert.Equal(100f, p.DiffusionPct);
        Assert.Equal(0f, p.DensityPct);
        Assert.Equal(0f, p.MasterWetDb);
    }

    [Fact]
    public void Millibel_gain_is_exact_and_floors_to_silence()
    {
        Assert.Equal(1f, EnvironmentReverbParams.GainMb(0f), 6);
        Assert.Equal(0.5f, EnvironmentReverbParams.GainMb(-602.06f), 3);
        Assert.Equal(0f, EnvironmentReverbParams.GainMb(-10_000f));
    }

    [Fact]
    public void Wet_onset_waits_for_the_reflect_delay()
    {
        var p = Corridor with { ReflectDelayS = 0.1f, ReverbDelayS = 0.05f, ReflectionsMb = 0f };
        var wet = ImpulseResponse(p, 0.5f);
        var first = FirstNonZero(wet);
        Assert.InRange(first, (int)(0.1f * Rate) - 2, (int)(0.1f * Rate) + 2);

        // With the early reflections silenced the late tail starts ReverbDelay later still.
        var late = ImpulseResponse(p with { ReflectionsMb = -10_000f }, 0.5f);
        Assert.True(FirstNonZero(late) >= (int)(0.15f * Rate) - 2);
    }

    [Fact]
    public void Bypassed_parameters_produce_no_wet()
    {
        var wet = ImpulseResponse(EnvironmentReverbParams.Bypassed, 0.5f);
        Assert.Equal(-1, FirstNonZero(wet));
    }

    [Fact]
    public void Room_level_scales_the_whole_wet_signal()
    {
        var loud = ImpulseResponse(Corridor with { RoomMb = -250f }, 1f);
        var quiet = ImpulseResponse(Corridor with { RoomMb = -1250f }, 1f);
        var ratio = Rms(quiet, 0, loud.Length) / Rms(loud, 0, loud.Length);
        Assert.InRange(ratio, 0.30f, 0.33f); // 1000 mB = 10 dB
    }

    [Fact]
    public void Decay_time_sets_the_tail_length()
    {
        var shortTail = ImpulseResponse(Corridor with { DecayTimeS = 0.3f, DecayHfRatio = 1f }, 3f);
        var longTail = ImpulseResponse(Corridor with { DecayTimeS = 3f, DecayHfRatio = 1f }, 3f);
        var window = (int)(0.2f * Rate);
        var at1s = (int)(1f * Rate);
        var shortRatio = Rms(shortTail, at1s, at1s + window) / Rms(shortTail, 0, window);
        var longRatio = Rms(longTail, at1s, at1s + window) / Rms(longTail, 0, window);
        Assert.True(shortRatio < 0.01f, $"0.3 s tail still at {shortRatio} after 1 s");
        Assert.True(longRatio > 0.1f, $"3 s tail already down to {longRatio} after 1 s");
        // RT60 target: at DecayTime the tail is ~60 dB down (RMS ratio 1e-3); allow the network's spread.
        var atRt = (int)(3f * Rate) - window;
        var rt60 = Rms(longTail, atRt, atRt + window) / Rms(longTail, 0, window);
        Assert.InRange(rt60, 1e-4f, 1e-2f);
    }

    [Fact]
    public void Low_hf_ratio_darkens_the_tail()
    {
        var bright = ImpulseResponse(Corridor with { DecayTimeS = 2f, DecayHfRatio = 1f, RoomHfMb = 0f, RoomLfMb = 0f }, 1.5f);
        var dark = ImpulseResponse(Corridor with { DecayTimeS = 2f, DecayHfRatio = 0.2f, RoomHfMb = 0f, RoomLfMb = 0f }, 1.5f);
        var from = (int)(1f * Rate);
        Assert.True(Rms(dark, from, dark.Length) < 0.5f * Rms(bright, from, bright.Length));
    }

    [Fact]
    public void Room_lf_shelf_cuts_the_low_band()
    {
        var flat = new SfxReverb(Rate);
        flat.Configure(Corridor with { RoomLfMb = 0f, RoomHfMb = 0f, LfReferenceHz = 300f });
        var cut = new SfxReverb(Rate);
        cut.Configure(Corridor with { RoomLfMb = -4000f, RoomHfMb = 0f, LfReferenceHz = 300f });
        var input = Sine(60f, Rate * 2);
        var wetFlat = new float[input.Length];
        var wetCut = new float[input.Length];
        flat.Process(input, wetFlat);
        cut.Process(input, wetCut);
        var ratio = Rms(wetCut, Rate, wetCut.Length) / Rms(wetFlat, Rate, wetFlat.Length);
        Assert.InRange(ratio, 0.005f, 0.05f); // -40 dB shelf, well below the corner
    }

    [Fact]
    public void Room_hf_shelf_cuts_the_high_band()
    {
        var flat = new SfxReverb(Rate);
        flat.Configure(Corridor with { RoomLfMb = 0f, RoomHfMb = 0f, HfReferenceHz = 4000f });
        var cut = new SfxReverb(Rate);
        cut.Configure(Corridor with { RoomLfMb = 0f, RoomHfMb = -4000f, HfReferenceHz = 4000f });
        var input = Sine(15_000f, Rate * 2);
        var wetFlat = new float[input.Length];
        var wetCut = new float[input.Length];
        flat.Process(input, wetFlat);
        cut.Process(input, wetCut);
        var ratio = Rms(wetCut, Rate, wetCut.Length) / Rms(wetFlat, Rate, wetFlat.Length);
        Assert.InRange(ratio, 0.005f, 0.05f);
    }

    [Fact]
    public void Reset_drops_the_tail()
    {
        var reverb = new SfxReverb(Rate);
        reverb.Configure(Corridor);
        var input = new float[2880];
        input[0] = 1f;
        var wet = new float[2880];
        reverb.Process(input, wet);
        reverb.Reset();
        Array.Clear(input);
        reverb.Process(input, wet);
        Assert.Equal(-1, FirstNonZero(wet));
    }

    [Fact]
    public void Stage_master_off_is_an_exact_passthrough()
    {
        var stage = new EnvironmentReverb(Rate);
        var block = Sine(440f, 2880);
        var copy = (float[])block.Clone();
        stage.Process(block, Corridor, EnvironmentReverbToggles.Off);
        Assert.Equal(copy, block);
        Assert.Equal(1f, stage.DryGain);
    }

    [Fact]
    public void Stage_bypass_is_the_two_dry_copies_and_no_tail()
    {
        var stage = new EnvironmentReverb(Rate);
        var block = Sine(440f, 2880, 0.2f);
        var copy = (float[])block.Clone();
        stage.Process(block, EnvironmentReverbParams.Bypassed, EnvironmentReverbToggles.Default);
        var expected = MathF.Pow(10f, -3f / 20f) * (MathF.Pow(10f, -6f / 20f) + 1f);
        Assert.Equal(expected, stage.DryGain * MathF.Pow(10f, -3f / 20f), 4);
        for (var i = 0; i < block.Length; i++) Assert.True(MathF.Abs(copy[i] * expected - block[i]) < 1e-5f, $"sample {i}: {block[i]} vs {copy[i] * expected}");
    }

    [Fact]
    public void Stage_dry_copy_and_bus_gain_toggles_change_only_their_gains()
    {
        var p = Corridor;
        var noBus = new EnvironmentReverb(Rate);
        noBus.Process(new float[64], p, EnvironmentReverbToggles.Default with { BusGains = false });
        var withBus = new EnvironmentReverb(Rate);
        withBus.Process(new float[64], p, EnvironmentReverbToggles.Default);
        var noCopy = new EnvironmentReverb(Rate);
        noCopy.Process(new float[64], p, EnvironmentReverbToggles.Default with { DryCopy = false });

        var copyGain = MathF.Pow(10f, p.DryLevelMb / 2000f);
        Assert.Equal(1f + copyGain, noBus.DryGain, 4);
        Assert.Equal(MathF.Pow(10f, -6f / 20f) + copyGain, withBus.DryGain, 4);
        Assert.Equal(MathF.Pow(10f, -6f / 20f), noCopy.DryGain, 4);
        Assert.Equal(0f, noCopy.DryCopyGain);
    }

    [Fact]
    public void Stage_master_wet_scales_the_return_and_voice_slider_is_opt_in()
    {
        var p = Corridor with { MasterWetDb = -20f, VoiceBusDb = -6f };
        var stage = new EnvironmentReverb(Rate);
        stage.Process(new float[64], p, EnvironmentReverbToggles.Default);
        Assert.Equal(0.1f, stage.ReturnGain, 4);

        // The slider multiplies the input; with a silent frame its only trace is that nothing breaks.
        var block = Sine(440f, 2880, 0.1f);
        var off = (float[])block.Clone();
        var on = (float[])block.Clone();
        new EnvironmentReverb(Rate).Process(off, p with { MasterWetDb = 0f }, EnvironmentReverbToggles.Default);
        new EnvironmentReverb(Rate).Process(on, p with { MasterWetDb = 0f }, EnvironmentReverbToggles.All);
        var ratio = Rms(on, 0, 2000) / Rms(off, 0, 2000);
        Assert.InRange(ratio, 0.49f, 0.52f); // -6 dB
    }

    [Fact]
    public void Stage_adds_a_tail_to_a_burst_in_a_room_and_none_outdoors()
    {
        var indoors = new EnvironmentReverb(Rate);
        var outdoors = new EnvironmentReverb(Rate);
        var outdoorParams = EnvironmentReverbParams.FromListener(0.8f, 0.9f, 0.3f, 0.5f);
        var burst = Sine(440f, 4800, 0.3f);
        var silence = new float[Rate];
        var tailIn = (float[])silence.Clone();
        var tailOut = (float[])silence.Clone();
        indoors.Process(burst, Corridor, EnvironmentReverbToggles.Default);
        indoors.Process(tailIn, Corridor, EnvironmentReverbToggles.Default);
        outdoors.Process((float[])burst.Clone(), outdoorParams, EnvironmentReverbToggles.Default);
        outdoors.Process(tailOut, outdoorParams, EnvironmentReverbToggles.Default);
        var inTail = Rms(tailIn, 4800, 4800 + 9600);
        var outTail = Rms(tailOut, 4800, 4800 + 9600);
        Assert.True(inTail > 1e-3f, $"indoor tail {inTail}");
        Assert.True(outTail < inTail * 0.1f, $"outdoor tail {outTail} vs indoor {inTail}");
    }

    [Fact]
    public void Stage_output_is_clamped()
    {
        var stage = new EnvironmentReverb(Rate);
        var block = new float[2880];
        Array.Fill(block, 1f);
        stage.Process(block, EnvironmentReverbParams.Bypassed, EnvironmentReverbToggles.Default with { BusGains = false });
        foreach (var s in block) Assert.InRange(s, -1f, 1f);
    }

    [Fact]
    public void Low_shelf_biquad_matches_its_gain_below_and_unity_above_the_corner()
    {
        var shelf = new Biquad(Rate, 4f);
        shelf.Configure(Biquad.Kind.LowShelf, 300f, 0.707f, -20f, 1f, 1f);
        var low = Sine(30f, Rate);
        shelf.Process(low);
        Assert.InRange(Rms(low, Rate / 2, Rate) / (0.5f / MathF.Sqrt(2f)), 0.08f, 0.12f);
        shelf.Reset();
        var high = Sine(6000f, Rate);
        shelf.Process(high);
        Assert.InRange(Rms(high, Rate / 2, Rate) / (0.5f / MathF.Sqrt(2f)), 0.95f, 1.05f);
    }
}
