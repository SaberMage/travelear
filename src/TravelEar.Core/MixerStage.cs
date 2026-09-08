namespace TravelEar.Core;

/// <summary>Which Mixer Stage effects are active (config <c>Fidelity.Mixer*</c>); the master off bypasses everything.</summary>
public readonly record struct MixerStageToggles(bool Master, bool Dry, bool High, bool ReverbFall, bool ReverbBoost)
{
    public static MixerStageToggles All => new(true, true, true, true, true);
    public static MixerStageToggles Off => new(false, false, false, false, false);
}

/// <summary>
/// The Mixer Stage re-synthesis (ADR-0002): what Unity's mixer does to a voice channel, done in
/// mod DSP from the four channel floats the game's model produces (<see cref="MixerStageModel"/>).
/// Per sample: <c>out = x * dry + reverb(x * (fallWet + boostWet))</c>, then the occlusion high cut
/// when <c>High</c> is non-zero. Dry is exact (a gain); the reverb is an approximation
/// (<see cref="Reverb"/>); the high cut is an approximation (a 3 kHz high shelf at the game's dB,
/// the mixer's actual effect being unreadable). Every part is toggleable so the operator can A/B
/// each one. Runs on the encoder thread; parameters arrive as a snapshot per frame.
/// </summary>
public sealed class MixerStage
{
    private const float HighShelfHz = 3000f;
    private const float HighShelfQ = 0.7f;

    private readonly Reverb _reverb;
    private readonly Biquad _highShelf;
    private float _highShelfDb;
    private bool _highShelfActive;

    public int SampleRate { get; }

    /// <summary>The last applied gains, for the stats line.</summary>
    public float DryGain { get; private set; } = 1f;
    public float FallWetGain { get; private set; }
    public float BoostWetGain { get; private set; }
    public float HighDb { get; private set; }
    public long Frames { get; private set; }

    public MixerStage(int sampleRate, float reverbDecaySeconds)
    {
        SampleRate = sampleRate;
        _reverb = new Reverb(sampleRate, reverbDecaySeconds);
        _highShelf = new Biquad(sampleRate);
    }

    public void SetReverbDecay(float seconds) => _reverb.SetDecay(seconds);

    /// <summary>Clears the reverb tail and the shelf state (a new talk burst after a long gap).</summary>
    public void Reset()
    {
        _reverb.Reset();
        _highShelf.Reset();
    }

    // [impl->REQ-MIXER-RESYNTH]
    /// <summary>
    /// Applies one frame in place. With the master toggle off the frame is untouched; each
    /// disabled part falls back to its neutral value (dry 0 dB, no send, no cut).
    /// </summary>
    public void Process(Span<float> mono, float dryDb, float highDb, float fallWetDb, float boostWetDb, in MixerStageToggles toggles)
    {
        Frames++;
        if (!toggles.Master)
        {
            DryGain = 1f; FallWetGain = 0f; BoostWetGain = 0f; HighDb = 0f;
            return;
        }

        DryGain = toggles.Dry ? MixerStageModel.Gain(dryDb) : 1f;
        FallWetGain = toggles.ReverbFall ? MixerStageModel.Gain(fallWetDb) : 0f;
        BoostWetGain = toggles.ReverbBoost ? MixerStageModel.Gain(boostWetDb) : 0f;
        HighDb = toggles.High ? highDb : 0f;

        var send = FallWetGain + BoostWetGain;
        var dry = DryGain;
        for (var i = 0; i < mono.Length; i++)
        {
            var x = mono[i];
            var wet = _reverb.Process(x * send); // always stepped so a tail keeps decaying after the send closes
            mono[i] = x * dry + wet;
        }

        if (MathF.Abs(HighDb) < 0.01f)
        {
            if (_highShelfActive) { _highShelfActive = false; _highShelf.Reset(); }
            return;
        }
        if (!_highShelfActive || MathF.Abs(HighDb - _highShelfDb) > 0.1f)
        {
            _highShelf.Configure(Biquad.Kind.HighShelf, HighShelfHz, HighShelfQ, HighDb, 1f, 1f);
            _highShelfDb = HighDb;
            _highShelfActive = true;
        }
        _highShelf.Process(mono);
    }
}
