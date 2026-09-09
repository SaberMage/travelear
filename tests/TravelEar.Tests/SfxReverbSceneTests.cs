using System;
using TravelEar.Core;
using Xunit;

namespace TravelEar.Tests;

/// <summary>
/// Two real parameter sets from M3 run 4's <c>Environment reverb:</c> log line (2026-09-09): the
/// starting-area hallway (Decay 1.09 s, Reverb -621 mB) and a big indoor room (Decay 0.24 s,
/// Reverb -2238 mB). The operator heard them as nearly the same because the gate cut every tail
/// at ~360 ms; the network itself must keep them far apart, or the tail fix changes nothing.
/// </summary>
public class SfxReverbSceneTests
{
    private const int Rate = 48_000;
    private const int Block = 480;

    private static readonly EnvironmentReverbParams Hallway = new(
        DryLevelMb: -256, RoomMb: -262, RoomHfMb: -1000, RoomLfMb: -1257,
        DecayTimeS: 1.09f, DecayHfRatio: 0.30f, ReflectionsMb: -3490, ReflectDelayS: 0.064f,
        ReverbMb: -621, ReverbDelayS: 0.043f, HfReferenceHz: 4864, LfReferenceHz: 320,
        DiffusionPct: 100, DensityPct: 71, MasterWetDb: 0, VoiceBusDb: 0);

    private static readonly EnvironmentReverbParams BigRoom = new(
        DryLevelMb: -254, RoomMb: -491, RoomHfMb: -1000, RoomLfMb: -904,
        DecayTimeS: 0.24f, DecayHfRatio: 0.30f, ReflectionsMb: -4013, ReflectDelayS: 0.062f,
        ReverbMb: -2238, ReverbDelayS: 0.042f, HfReferenceHz: 3709, LfReferenceHz: 494,
        DiffusionPct: 100, DensityPct: 71, MasterWetDb: 0, VoiceBusDb: 0);

    /// <summary>Wet energy of an impulse response in [from, to) seconds.</summary>
    private static double Energy(EnvironmentReverbParams p, double from, double to)
    {
        var reverb = new SfxReverb(Rate);
        reverb.Configure(p);
        var length = (int)(to * Rate);
        var input = new float[length];
        input[0] = 1f;
        var wet = new float[length];
        for (var offset = 0; offset < length; offset += Block)
        {
            var n = Math.Min(Block, length - offset);
            reverb.Process(input.AsSpan(offset, n), wet.AsSpan(offset, n));
        }
        var energy = 0.0;
        for (var i = (int)(from * Rate); i < length; i++) energy += (double)wet[i] * wet[i];
        return energy;
    }

    // [unit->REQ-MIXER-RESYNTH]
    [Fact]
    public void Hallway_tail_after_the_gate_would_have_closed_is_far_above_the_big_room()
    {
        // 0.4 s on: past the gate's hold + fade (210 + 150 ms) where run 4 cut both tails.
        var hallway = Energy(Hallway, 0.4, 2.0);
        var bigRoom = Energy(BigRoom, 0.4, 2.0);
        var margin = 10 * Math.Log10(hallway / bigRoom);
        Assert.True(margin >= 10, $"hallway tail is only {margin:F1} dB above the big room's after 0.4 s ({hallway:E2} vs {bigRoom:E2})");
    }

    // [unit->REQ-MIXER-RESYNTH]
    [Fact]
    public void Hallway_tail_is_still_ringing_a_second_in()
    {
        var first = Energy(Hallway, 0.1, 0.4);
        var later = Energy(Hallway, 0.7, 1.0);
        var decay = 10 * Math.Log10(first / later);
        // RT60 1.09 s: about -33 dB per 0.6 s on average; anything under 45 dB means a tail is there.
        Assert.True(decay < 45, $"hallway tail decayed {decay:F1} dB between 0.1-0.4 s and 0.7-1.0 s; expected a live tail");
        Assert.True(later > 0, "no energy at all a second in");
    }
}
