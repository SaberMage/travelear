namespace TravelEar.Core;

/// <summary>
/// The red bells' super-wet bloom (docs/reference/big-walk-voice-effects-catalog.md rows 10,
/// section 5.5 item 4): on a listener's machine the main mixer's <c>Voice</c> group always sends
/// into <c>VOICE SUPER WET BUS</c> (<c>Voice_SuperWet</c>, 0 dB) whose <c>Speechlessness</c>
/// return, <c>SuperWet_Speechlessness = (1 - sp^0.4) * -80</c> dB, opens as the listener nears a
/// zone; the return runs through <c>Master Super Wet</c>: group pitch <c>SuperWetPitch</c>, a
/// fixed 6.8 s SFX Reverb (wet only, RoomHF -2000 mB, HF ratio 0.15, Density 25) and the fixed
/// chorus. At the Self-Ear the listener stands in the speaker's zone, and the game writes those
/// three floats for the local listener every frame, so the mod reads them as they are.
/// Approximations: the group pitch is a resample (pitch and tempo) in the mixer and a
/// <see cref="PitchShifter"/> (pitch only) here; <see cref="SfxReverb"/> and <see cref="Chorus"/>
/// as documented on them. Wet only; the caller sums it into the voice. Encoder thread.
/// </summary>
public sealed class SpeechlessBloom
{
    public const float FloorDb = -80f;
    /// <summary>Below this the return is treated as closed (the asset's -80 dB "off").</summary>
    public const float ClosedDb = -79f;
    /// <summary>How long the chain keeps rendering after the return closes, so the 6.8 s tail rings out.</summary>
    public const float TailSeconds = 7f;
    /// <summary>The fixed <c>Master Super Wet</c> SFX Reverb: DryLevel -10000, Room 0, RoomHF -2000, RoomLF 0, DecayTime 6.8 s, DecayHFRatio 0.15, Reflections -10000, ReflectDelay 0.01, Reverb 0, ReverbDelay 0.1, HFReference 5000, LFReference 250, Diffusion 100, Density 25.</summary>
    public static readonly EnvironmentReverbParams Reverb = new(
        DryLevelMb: -10000, RoomMb: 0, RoomHfMb: -2000, RoomLfMb: 0, DecayTimeS: 6.8f, DecayHfRatio: 0.15f,
        ReflectionsMb: -10000, ReflectDelayS: 0.01f, ReverbMb: 0, ReverbDelayS: 0.1f, HfReferenceHz: 5000, LfReferenceHz: 250,
        DiffusionPct: 100, DensityPct: 25, MasterWetDb: 0, VoiceBusDb: 0);

    private readonly PitchShifter _pitch;
    private readonly SfxReverb _reverb;
    private readonly Chorus _chorus;
    private readonly float[] _send = new float[512];
    private readonly float[] _wet = new float[512];
    private int _tailSamplesLeft;
    private bool _pitchActive;

    public int SampleRate { get; }
    public long Frames { get; private set; }
    /// <summary>True while the return is open or its tail is still ringing.</summary>
    public bool Active => _tailSamplesLeft > 0;
    /// <summary>The return gain the last frame was rendered with (0 = closed).</summary>
    public float ReturnGain { get; private set; }
    public float BusGain { get; private set; } = 1f;
    public float Pitch { get; private set; } = 1f;

    public SpeechlessBloom(int sampleRate)
    {
        SampleRate = sampleRate;
        _pitch = new PitchShifter(sampleRate);
        _reverb = new SfxReverb(sampleRate);
        _reverb.Configure(Reverb);
        _chorus = Chorus.SuperWet(sampleRate);
    }

    public void Reset()
    {
        _pitch.Reset();
        _reverb.Reset();
        _chorus.Reset();
        _tailSamplesLeft = 0;
        _pitchActive = false;
        ReturnGain = 0f;
    }

    // [impl->REQ-MIXER-RESYNTH]
    /// <summary>
    /// Renders the bloom of <paramref name="voice"/> into <paramref name="bloom"/> (same length):
    /// <c>Voice_SuperWet</c> bus gain, <c>SuperWetPitch</c>, the fixed reverb and chorus, then
    /// the <c>SuperWet_Speechlessness</c> return. Silence (and no work) while the return has been
    /// closed for longer than the tail; a frame of zeros keeps the tail ringing after it closes.
    /// True when anything was written.
    /// </summary>
    public bool Process(ReadOnlySpan<float> voice, Span<float> bloom, float busDb, float pitch, float returnDb)
    {
        Frames++;
        var open = !float.IsNaN(returnDb) && returnDb > ClosedDb;
        if (open) _tailSamplesLeft = (int)(TailSeconds * SampleRate);
        else if (_tailSamplesLeft <= 0)
        {
            ReturnGain = 0f;
            bloom.Clear();
            return false;
        }
        else _tailSamplesLeft -= bloom.Length;

        BusGain = MixerStageModel.Gain(float.IsNaN(busDb) ? 0f : busDb);
        Pitch = float.IsNaN(pitch) ? 1f : Math.Clamp(pitch, PitchShifter.MinRatio, PitchShifter.MaxRatio);
        ReturnGain = open ? MixerStageModel.Gain(returnDb) : 0f;
        var shift = Pitch < 0.999f || Pitch > 1.001f;
        if (shift && !_pitchActive) { _pitch.Reset(); _pitchActive = true; }
        else if (!shift && _pitchActive) _pitchActive = false;

        // The return volume sits on the Speechlessness child group, i.e. on the SEND into
        // Master Super Wet, so the reverb and chorus ring out after the return closes and the
        // level follows sp smoothly on the way in.
        var sendGain = BusGain * ReturnGain;
        for (var offset = 0; offset < bloom.Length; offset += _send.Length)
        {
            var count = Math.Min(_send.Length, bloom.Length - offset);
            var send = _send.AsSpan(0, count);
            var wet = _wet.AsSpan(0, count);
            for (var i = 0; i < count; i++) send[i] = voice[offset + i] * sendGain;
            if (shift) _pitch.Process(send, Pitch);
            _reverb.Process(send, wet);
            _chorus.Process(wet);
            wet.CopyTo(bloom.Slice(offset, count));
        }
        return true;
    }
}
