namespace TravelEar.Core;

/// <summary>Which parts of the Megaphone voice are active (config <c>Fidelity.Megaphone*</c>).</summary>
public readonly record struct MegaphoneToggles(bool Master, bool Crusher, bool HighPass, bool Compressors)
{
    public static MegaphoneToggles All => new(true, true, true, true);
    public static MegaphoneToggles Off => new(false, false, false, false);
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
    public const float PostCompressorReleaseMs = 0.25f; // clamped to FMOD's 10 ms floor
    public const float UnityCompressorAttackMs = 50f;
    public const float UnityCompressorReleaseMs = 50f;

    private readonly BitCrusher _crusher = new(CrusherBitDepth, CrusherRateHz, CrusherDryWet, CrusherSmooth);
    private readonly Biquad _highPass;
    private readonly Compressor _compressor;
    private readonly Compressor _postCompressor;

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
        _postCompressor = new Compressor(sampleRate, PostCompressorThresholdDb, UnityCompressorAttackMs, PostCompressorReleaseMs, 0f);
    }

    /// <summary>Clears the filter and detector state (the megaphone was picked up again after a gap).</summary>
    public void Reset()
    {
        _highPass.Reset();
        _compressor.Reset();
        _postCompressor.Reset();
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
        if (toggles.Compressors)
        {
            _compressor.Process(megaphone);
            _postCompressor.Process(megaphone);
        }
    }
}
