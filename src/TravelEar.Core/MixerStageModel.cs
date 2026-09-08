namespace TravelEar.Core;

/// <summary>
/// The per-frame inputs of the game's voice-channel model (<c>PlayerVoicePlaybackControl.Update</c>,
/// docs/reference/big-walk-voice-dsp.md section 4), as a listener's machine would evaluate them
/// for a remote voice. For the Self-Ear the renderer fills the geometric terms with the listener's
/// own position (distance 0, occlusion 0, no height difference) and the speaker terms with the
/// local player's state.
/// </summary>
public readonly record struct MixerStageInputs(
    /// <summary><c>min(SpatialVolCurve(spatial), AttenuationCurve(distance))</c>; 1 at the listener.</summary>
    float Attenuation,
    /// <summary>Listener distance in metres (the boost send fades to 0 at 45 m).</summary>
    float DistanceMeters,
    /// <summary>Occlusion level 0..1 from the game's occlusion probe.</summary>
    float Occlusion,
    /// <summary>The listener side's outdoorness (<c>AudioDynamicReverb.Outdoorness</c>), 0..1.</summary>
    float ListenerOutdoorness,
    /// <summary>The listener side's global voice volume factor.</summary>
    float GlobalVoiceVolume,
    /// <summary>Source height above the listener in metres (the boost send needs the speaker above).</summary>
    float HeightAboveListenerMeters,
    /// <summary>The speaker's <c>PlayerFaller.isInDanger</c>: falling.</summary>
    bool SpeakerInDanger,
    /// <summary>The speaker's synced <c>PlayerNetworking.outdoorness</c>, 0..1.</summary>
    float SpeakerOutdoorness)
{
    /// <summary>The Self-Ear geometry: at the listener, unoccluded, level, with the given speaker state.</summary>
    public static MixerStageInputs SelfEar(bool inDanger, float speakerOutdoorness, float listenerOutdoorness, float globalVoiceVolume)
        => new(1f, 0f, 0f, listenerOutdoorness, globalVoiceVolume, 0f, inDanger, speakerOutdoorness);
}

/// <summary>
/// The four per-channel mixer floats the game writes every frame for a voice, ported from
/// <c>PlayerVoicePlaybackControl.Update</c> so the Mixer Stage can be re-synthesized for the local
/// player (ADR-0002 as amended: the game never writes these floats for the local player, so the
/// mod evaluates the same formulas at the Self-Ear). Values are in dB exactly as the game sets
/// them; <see cref="TravelEar.Core.MixerStage"/> turns them into gains. One instance per voice;
/// <see cref="Step"/> once per frame on the main thread.
/// </summary>
public sealed class MixerStageModel
{
    public const float FloorDb = -80f;
    private const float Floor = 1e-4f;

    /// <summary><c>Dry{n}</c>: the channel's dry level.</summary>
    public float DryDb { get; private set; }

    /// <summary><c>High{n}</c>: occlusion high cut, <c>occlusion * -30 dB</c>.</summary>
    public float HighDb { get; private set; }

    /// <summary><c>ReverbFallWet{n}</c>: the fall reverb send.</summary>
    public float ReverbFallWetDb { get; private set; } = FloorDb;

    /// <summary><c>ReverbBoostWet{n}</c>: the height/indoor reverb boost send.</summary>
    public float ReverbBoostWetDb { get; private set; } = FloorDb;

    /// <summary>The smoothed fall send level, linear (the game's <c>_fallWetLvl</c>).</summary>
    public float FallWetLevel { get; private set; }

    /// <summary>Resets the smoothed state as <c>PlayVoice</c> does (<c>_fallWetLvl = 0</c>, dry at the floor).</summary>
    public void Reset()
    {
        FallWetLevel = 0f;
        DryDb = FloorDb;
        ReverbFallWetDb = FloorDb;
        ReverbBoostWetDb = FloorDb;
        HighDb = 0f;
    }

    // [impl->REQ-MIXER-RESYNTH]
    /// <summary>One frame of the game's model. <paramref name="dt"/> is the frame time in seconds.</summary>
    public void Step(in MixerStageInputs i, float dt)
    {
        // E. the fall send: instant rise to the speaker's outdoorness while falling, 1 s lerp decay.
        var wetTarget = i.SpeakerInDanger ? i.SpeakerOutdoorness : 0f;
        FallWetLevel = wetTarget > FallWetLevel ? wetTarget : Lerp(FallWetLevel, wetTarget, dt);

        var heightFactor = Clamp01(i.HeightAboveListenerMeters / 30f);
        var boost = Cube(1f - i.Attenuation)
                    * i.GlobalVoiceVolume
                    * Clamp01((i.DistanceMeters - 45f) / -45f)
                    * (1f - i.ListenerOutdoorness)
                    * (1f - i.Occlusion)
                    * heightFactor;

        var dryLin = MathF.Max(MathF.Max(i.Attenuation, boost / 3f * heightFactor), Floor);
        DryDb = MathF.Max(FloorDb, Db(dryLin));
        ReverbFallWetDb = Db(MathF.Max(FallWetLevel, Floor));
        ReverbBoostWetDb = Db(MathF.Max(boost, Floor));
        HighDb = i.Occlusion * -30f;
    }

    /// <summary>dB to linear gain; the game's -80 dB floor maps to 0.</summary>
    public static float Gain(float db) => db <= FloorDb ? 0f : MathF.Pow(10f, db / 20f);

    private static float Db(float lin) => 20f * MathF.Log10(lin);
    private static float Cube(float x) => x * x * x;
    private static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
    private static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
}
