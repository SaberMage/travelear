namespace TravelEar.Core;

/// <summary>
/// The two <c>AudioVolume</c>s the game adds to a remote voice's <c>AudioSourceController</c>
/// volume chain (docs/reference/big-walk-voice-effects-catalog.md sections 5.1b and 5.5,
/// <c>PlayerVoicePlaybackControl.PlayVoice</c> / <c>Update</c>): plain linear gains on the source,
/// applied before every mixer-side effect.
/// <list type="bullet">
/// <item><b>Indoor attenuation</b>: <c>Outdoorness * 0.5 + 0.5</c> from the <i>listener's</i>
/// <c>AudioDynamicReverb.Outdoorness</c> (1 with no dynamic reverb), so a voice is 6 dB down when
/// the listener is fully indoors; smoothed by <c>clamp01(dt * 3)</c> per frame.</item>
/// <item><b>Speechlessness</b> (the red bells): <c>1 - speaker.speechless.speechlessness</c>, lerped
/// by <c>dt * 5</c>; silent at the centre of a zone.</item>
/// </list>
/// At the Self-Ear the local player is both speaker and listener, so both read local state.
/// Main-thread model; the encoder thread multiplies by <see cref="Gain"/>.
/// </summary>
public sealed class SourceVolume
{
    public const float IndoorSmoothingPerSecond = 3f;
    public const float SpeechlessSmoothingPerSecond = 5f;

    /// <summary>The indoor attenuation term, 0.5..1.</summary>
    public float IndoorGain { get; private set; } = 1f;

    /// <summary>The speechlessness term, 0..1.</summary>
    public float SpeechlessGain { get; private set; } = 1f;

    public bool IndoorEnabled { get; }
    public bool SpeechlessEnabled { get; }

    /// <summary>The product of the enabled terms.</summary>
    public float Gain => (IndoorEnabled ? IndoorGain : 1f) * (SpeechlessEnabled ? SpeechlessGain : 1f);

    public SourceVolume(bool indoorEnabled, bool speechlessEnabled)
    {
        IndoorEnabled = indoorEnabled;
        SpeechlessEnabled = speechlessEnabled;
    }

    // [impl->REQ-MIXER-RESYNTH]
    /// <summary>One frame: the listener's outdoorness (1 when unknown), the speaker's speechlessness (0 outside a zone), the frame time.</summary>
    public void Step(float listenerOutdoorness, float speakerSpeechlessness, float dt)
    {
        var outdoor = float.IsNaN(listenerOutdoorness) ? 1f : Math.Clamp(listenerOutdoorness, 0f, 1f);
        var sp = float.IsNaN(speakerSpeechlessness) ? 0f : Math.Clamp(speakerSpeechlessness, 0f, 1f);
        var indoorTarget = outdoor * 0.5f + 0.5f;
        IndoorGain += (indoorTarget - IndoorGain) * Math.Clamp(dt * IndoorSmoothingPerSecond, 0f, 1f);
        SpeechlessGain += (1f - sp - SpeechlessGain) * Math.Clamp(dt * SpeechlessSmoothingPerSecond, 0f, 1f);
    }

    public void Reset()
    {
        IndoorGain = 1f;
        SpeechlessGain = 1f;
    }
}
