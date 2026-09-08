using TravelEar.Core;
using Xunit;

namespace TravelEar.Tests;

/// <summary>
/// The game's per-frame voice-channel model (<c>PlayerVoicePlaybackControl.Update</c>, section E of
/// docs/reference/big-walk-voice-dsp.md section 4) checked against its formulas.
/// </summary>
public class MixerStageModelTests
{
    private static MixerStageInputs Remote(float attenuation, float distance, float occlusion, float listenerOutdoor,
        float voiceVol, float height, bool inDanger, float speakerOutdoor)
        => new(attenuation, distance, occlusion, listenerOutdoor, voiceVol, height, inDanger, speakerOutdoor);

    // [unit->REQ-MIXER-RESYNTH]
    [Fact]
    public void Self_ear_at_rest_is_dry_at_unity_with_no_sends_and_no_cut()
    {
        var m = new MixerStageModel();
        m.Step(MixerStageInputs.SelfEar(false, 0.8f, 0.5f, 1f), 1f / 60f);
        Assert.Equal(0f, m.DryDb, 4);
        Assert.Equal(0f, m.HighDb, 4);
        Assert.Equal(MixerStageModel.FloorDb, m.ReverbFallWetDb, 3);
        Assert.Equal(MixerStageModel.FloorDb, m.ReverbBoostWetDb, 3);
    }

    [Fact]
    public void Falling_outdoors_opens_the_fall_send_instantly_at_the_speakers_outdoorness()
    {
        var m = new MixerStageModel();
        m.Step(MixerStageInputs.SelfEar(true, 0.5f, 0f, 1f), 0.016f);
        Assert.Equal(0.5f, m.FallWetLevel, 5);
        Assert.Equal(20f * MathF.Log10(0.5f), m.ReverbFallWetDb, 3);
    }

    [Fact]
    public void After_landing_the_fall_send_decays_with_a_one_second_lerp()
    {
        var m = new MixerStageModel();
        m.Step(MixerStageInputs.SelfEar(true, 1f, 0f, 1f), 0.016f);
        m.Step(MixerStageInputs.SelfEar(false, 1f, 0f, 1f), 0.25f);
        Assert.Equal(0.75f, m.FallWetLevel, 5);
        m.Step(MixerStageInputs.SelfEar(false, 1f, 0f, 1f), 0.25f);
        Assert.Equal(0.5625f, m.FallWetLevel, 5);
    }

    [Fact]
    public void Falling_indoors_sends_nothing()
    {
        var m = new MixerStageModel();
        m.Step(MixerStageInputs.SelfEar(true, 0f, 0f, 1f), 0.016f);
        Assert.Equal(0f, m.FallWetLevel, 5);
        Assert.Equal(MixerStageModel.FloorDb, m.ReverbFallWetDb, 3);
    }

    [Fact]
    public void Occlusion_writes_the_high_cut_in_dB()
    {
        var m = new MixerStageModel();
        m.Step(Remote(1f, 0f, 0.5f, 0f, 1f, 0f, false, 0f), 0.016f);
        Assert.Equal(-15f, m.HighDb, 4);
    }

    [Fact]
    public void Attenuated_voice_lowers_the_dry_level_to_its_dB()
    {
        var m = new MixerStageModel();
        m.Step(Remote(0.1f, 30f, 0f, 1f, 1f, 0f, false, 0f), 0.016f);
        Assert.Equal(-20f, m.DryDb, 3);
    }

    [Fact]
    public void Dry_level_never_goes_below_the_floor()
    {
        var m = new MixerStageModel();
        m.Step(Remote(0f, 100f, 0f, 1f, 1f, 0f, false, 0f), 0.016f);
        Assert.Equal(MixerStageModel.FloorDb, m.DryDb, 3);
    }

    [Fact]
    public void Boost_send_needs_a_far_indoor_speaker_above_the_listener()
    {
        var m = new MixerStageModel();
        // attenuation 0 -> (1-0)^3 = 1; distance 0 -> 1; indoor -> 1; unoccluded -> 1; 30 m up -> 1
        m.Step(Remote(0f, 0f, 0f, 0f, 1f, 30f, false, 0f), 0.016f);
        Assert.Equal(0f, m.ReverbBoostWetDb, 3);
        // the dry level then follows boost / 3 * heightFactor = 1/3
        Assert.Equal(20f * MathF.Log10(1f / 3f), m.DryDb, 3);

        m.Step(Remote(0f, 0f, 0f, 0f, 1f, 0f, false, 0f), 0.016f); // level with the listener: no boost
        Assert.Equal(MixerStageModel.FloorDb, m.ReverbBoostWetDb, 3);
    }

    [Fact]
    public void Boost_send_fades_out_by_45_metres()
    {
        var m = new MixerStageModel();
        m.Step(Remote(0f, 45f, 0f, 0f, 1f, 30f, false, 0f), 0.016f);
        Assert.Equal(MixerStageModel.FloorDb, m.ReverbBoostWetDb, 3);
    }

    [Fact]
    public void Gain_maps_the_floor_to_silence_and_zero_dB_to_unity()
    {
        Assert.Equal(0f, MixerStageModel.Gain(MixerStageModel.FloorDb));
        Assert.Equal(1f, MixerStageModel.Gain(0f), 5);
        Assert.Equal(0.5f, MixerStageModel.Gain(20f * MathF.Log10(0.5f)), 5);
    }

    [Fact]
    public void Reset_returns_to_the_play_voice_state()
    {
        var m = new MixerStageModel();
        m.Step(MixerStageInputs.SelfEar(true, 1f, 0f, 1f), 0.016f);
        m.Reset();
        Assert.Equal(0f, m.FallWetLevel);
        Assert.Equal(MixerStageModel.FloorDb, m.DryDb);
        Assert.Equal(MixerStageModel.FloorDb, m.ReverbFallWetDb);
    }
}
