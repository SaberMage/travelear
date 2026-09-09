namespace TravelEar.Core;

/// <summary>Which parts of the Megaphone voice are active (config <c>Fidelity.Megaphone*</c>).</summary>
public readonly record struct MegaphoneToggles(bool Master, bool Crusher, bool HighPass, bool Compressors, bool Mixer = true)
{
    public static MegaphoneToggles All => new(true, true, true, true, true);
    public static MegaphoneToggles Off => new(false, false, false, false, false);
}

/// <summary>
/// The Megaphone voice as a listener beside the holder hears it: the game's Megaphone
/// <c>VoicePlayer</c> chain evaluated at listener distance 0 (docs/reference/
/// big-walk-local-voice-wiring.md section 3, the remote-holder branch of <c>VoicePlayer.Update</c>
/// with <c>d = 0</c>): in process, the <see cref="BitCrusher"/> (24 bit, 4000 Hz hold, dry/wet 0.5,
/// smooth 0.5) then a 300 Hz Q 0.4 high-pass; on its mixer, a compressor (threshold -25 dB,
/// make-up +6 dB) into a post-compressor (threshold -15 dB, release at FMOD's 10 ms floor),
/// reverb send off, HP 0 Hz and LP 22 kHz (both open), and the megaphone channel's wet at 0 dB
/// with its dry at -80 dB. The result is the megaphone's own output, which peers hear on top of
/// the holder's direct voice. Exact: crusher and high-pass (ported kernels). Approximate: the
/// compressors (<see cref="Compressor"/>). Mono, encoder thread.
/// </summary>
public sealed class MegaphoneVoice
{
    public const int CrusherBitDepth = 24;
    public const int CrusherRateHz = 4000;
    public const float CrusherDryWet = 0.5f;
    public const float CrusherSmooth = 0.5f;
    public const float HighPassHz = 300f;
    public const float HighPassQ = 0.4f;
    public const float CompressorThresholdDb = -25f;
    public const float CompressorMakeupDb = 6f;
    public const float PostCompressorThresholdDb = -15f;
    /// <summary>The megaphone mixer's Duck Volume: threshold -15 dB, ratio 5, attack 0, release 0.25 s, knee 10 (catalogue 5.9).</summary>
    public const float PostCompressorReleaseMs = 250f;
    public const float PostCompressorRatio = 5f;
    public const float PostCompressorKneeDb = 10f;
    /// <summary>The megaphone mixer's Compressor: attack 10 ms, release 1000 ms (asset values, catalogue 5.9).</summary>
    public const float UnityCompressorAttackMs = 10f;
    public const float UnityCompressorReleaseMs = 1000f;
    /// <summary>Fixed effects on every megaphone mixer, in order: Lowpass Simple 5 kHz, ParamEQ 2500 Hz octave 0.8 gain 2.5, SFX Reverb (-10 dB wet, 2 s, full dry), Echo 100 ms decay 0.3 wet-only.</summary>
    public const float MixerLowPassHz = 5000f;
    public const float MixerEqHz = 2500f;
    public const float MixerEqOctaves = 0.8f;
    public const float MixerEqGainLinear = 2.5f;
    public const float MixerEchoMs = 100f;
    public const float MixerEchoDecay = 0.3f;
    /// <summary>The megaphone mixer's SFX Reverb at the Self-Ear (d = 0): DryLevel 0, Room -1000 mB, DecayTime 2 s, DecayHFRatio 0.5, Density 0, RoomLF 0, RoomHF -2000, Reflections -10000, ReflectDelay 0, Reverb 0, ReverbDelay 0.04, Diffusion 100, HFReference 3500, LFReference 1000.</summary>
    public static readonly EnvironmentReverbParams MixerReverb = new(
        DryLevelMb: 0, RoomMb: -1000, RoomHfMb: -2000, RoomLfMb: 0, DecayTimeS: 2f, DecayHfRatio: 0.5f,
        ReflectionsMb: -10000, ReflectDelayS: 0f, ReverbMb: 0, ReverbDelayS: 0.04f, HfReferenceHz: 3500, LfReferenceHz: 1000,
        DiffusionPct: 100, DensityPct: 0, MasterWetDb: 0, VoiceBusDb: 0);

    private readonly BitCrusher _crusher = new(CrusherBitDepth, CrusherRateHz, CrusherDryWet, CrusherSmooth);
    private readonly Biquad _highPass;
    private readonly Compressor _compressor;
    private readonly Compressor _postCompressor;
    private readonly Biquad _mixerLowPass;
    private readonly Biquad _mixerEq;
    private readonly SfxReverb _mixerReverb;
    private readonly float[] _wet = new float[512];
    private readonly float[] _echo;
    private int _echoIndex;

    public int SampleRate { get; }
    public long Frames { get; private set; }
    public float CompressorReductionDb => _compressor.ReductionDb;
    public float PostCompressorReductionDb => _postCompressor.ReductionDb;

    public MegaphoneVoice(int sampleRate)
    {
        SampleRate = sampleRate;
        _highPass = new Biquad(sampleRate);
        _highPass.Configure(Biquad.Kind.HighPass, HighPassHz, HighPassQ, 0f, 1f, 1f);
        _compressor = new Compressor(sampleRate, CompressorThresholdDb, UnityCompressorAttackMs, UnityCompressorReleaseMs, CompressorMakeupDb);
        _postCompressor = new Compressor(sampleRate, PostCompressorThresholdDb, 0.1f, PostCompressorReleaseMs, 0f, PostCompressorRatio, PostCompressorKneeDb);
        _mixerLowPass = new Biquad(sampleRate);
        _mixerLowPass.Configure(Biquad.Kind.LowPass, MixerLowPassHz, 0.707f, 0f, 1f, 1f);
        _mixerEq = new Biquad(sampleRate);
        // Unity ParamEQ: octave range -> Q = 1 / (2 sinh(ln2 / 2 * octaves)); gain is linear.
        var q = 1f / (2f * MathF.Sinh(MathF.Log(2f) / 2f * MixerEqOctaves));
        _mixerEq.Configure(Biquad.Kind.PeakingEq, MixerEqHz, q, 20f * MathF.Log10(MixerEqGainLinear), 1f, 1f);
        _mixerReverb = new SfxReverb(sampleRate);
        _mixerReverb.Configure(MixerReverb);
        _echo = new float[(int)(MixerEchoMs * sampleRate / 1000f)];
    }

    /// <summary>Clears the filter and detector state (the megaphone was picked up again after a gap).</summary>
    public void Reset()
    {
        _highPass.Reset();
        _compressor.Reset();
        _postCompressor.Reset();
        _mixerLowPass.Reset();
        _mixerEq.Reset();
        _mixerReverb.Reset();
        Array.Clear(_echo);
        _echoIndex = 0;
    }

    // [impl->REQ-RENDER-MEGAPHONE]
    /// <summary>
    /// Renders the megaphone output of <paramref name="voice"/> into <paramref name="megaphone"/>
    /// (same length). With the master toggle off the output is silence.
    /// </summary>
    public void Process(ReadOnlySpan<float> voice, Span<float> megaphone, in MegaphoneToggles toggles)
    {
        Frames++;
        if (!toggles.Master)
        {
            megaphone.Clear();
            return;
        }
        voice.CopyTo(megaphone);
        if (toggles.Crusher) _crusher.Process(megaphone, SampleRate);
        if (toggles.HighPass) _highPass.Process(megaphone);
        // The megaphone mixer, in the asset's effect order (catalogue 5.9): Lowpass, ParamEQ,
        // Compressor, SFX Reverb, Echo, then the Duck Volume.
        if (toggles.Mixer)
        {
            _mixerLowPass.Process(megaphone);
            _mixerEq.Process(megaphone);
        }
        if (toggles.Compressors) _compressor.Process(megaphone);
        if (toggles.Mixer)
        {
            for (var offset = 0; offset < megaphone.Length; offset += _wet.Length)
            {
                var chunk = megaphone.Slice(offset, Math.Min(_wet.Length, megaphone.Length - offset));
                var wet = _wet.AsSpan(0, chunk.Length);
                _mixerReverb.Process(chunk, wet);
                for (var i = 0; i < chunk.Length; i++) chunk[i] += wet[i]; // DryLevel 0 mB: the dry copy at unity
            }
            // Echo, DryMix 0 / WetMix 1: the delay line's output only, y[n] = x[n-D] + decay * y[n-D].
            for (var i = 0; i < megaphone.Length; i++)
            {
                var delayed = _echo[_echoIndex];
                var y = delayed;
                _echo[_echoIndex] = megaphone[i] + MixerEchoDecay * delayed;
                if (++_echoIndex >= _echo.Length) _echoIndex = 0;
                megaphone[i] = y;
            }
        }
        if (toggles.Compressors) _postCompressor.Process(megaphone);
    }
}
