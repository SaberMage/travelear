namespace TravelEar.Core;

/// <summary>
/// The environment reverb's parameter snapshot: Unity's fourteen "SFX Reverb" (I3DL2) floats
/// exactly as the game writes them on its main mixer every frame (<c>AudioDynamicReverb
/// .UpdateReverb</c>, or <c>AudioBasicReverb</c> in Basic mode; docs/reference/
/// big-walk-environment-reverb.md section 1), plus the two bus levels a voice meets on the way
/// (<c>MasterWet</c>, 0 dB unless speechless; the voice slider, a common factor of both buses).
/// Levels in millibels (mB; 100 mB = 1 dB), times in seconds, references in Hz, percentages 0-100.
/// </summary>
public readonly record struct EnvironmentReverbParams(
    float DryLevelMb, float RoomMb, float RoomHfMb, float RoomLfMb,
    float DecayTimeS, float DecayHfRatio, float ReflectionsMb, float ReflectDelayS,
    float ReverbMb, float ReverbDelayS, float HfReferenceHz, float LfReferenceHz,
    float DiffusionPct, float DensityPct, float MasterWetDb, float VoiceBusDb)
{
    /// <summary>The floor Unity uses for a silent level.</summary>
    public const float SilentMb = -10_000f;

    /// <summary>What <c>UpdateReverb</c> leaves on the mixer under <c>Bypass</c>: the un-reverbed copy at 0 dB, no room.</summary>
    public static EnvironmentReverbParams Bypassed => new(0f, SilentMb, 0f, 0f, 0.1f, 1f, SilentMb, 0f, SilentMb, 0f, 5000f, 300f, 0f, 50f, 0f, 0f);

    /// <summary>
    /// The game's formulas from the four listener-side scalars (all 0..1): room size, outdoorness,
    /// reverb time (a reflectivity average) and diffusion. The mod reads the live results instead;
    /// this is the unit-test oracle and the calibration aid.
    /// </summary>
    public static EnvironmentReverbParams FromListener(float roomSize, float outdoorness, float reverbTime, float diffusion, float masterWetDb = 0f, float voiceBusDb = 0f)
    {
        var rs = roomSize; var o = outdoorness; var rt = reverbTime; var d = diffusion;
        return new EnvironmentReverbParams(
            DryLevelMb: -150f - 250f * rs,
            RoomMb: -150f - 400f * o - 200f * rs,
            RoomHfMb: -1000f * d,
            RoomLfMb: -1000f - 700f * rs + 600f * o,
            DecayTimeS: Math.Clamp(15.9f * rt * rt * rt * rt + 0.1f, 0.1f, 16f),
            DecayHfRatio: 0.8f - 0.5f * d,
            ReflectionsMb: Math.Clamp(1100f * (1f - o) - 10_600f * rs, -10_000f, 500f),
            ReflectDelayS: 0.15f * rs,
            ReverbMb: 900f * rt - 900f - 2500f * o,
            ReverbDelayS: 0.1f * rs,
            HfReferenceHz: 5000f - 2000f * o,
            LfReferenceHz: 300f + 300f * o,
            DiffusionPct: 100f * d,
            DensityPct: 50f + 50f * rs,
            MasterWetDb: masterWetDb,
            VoiceBusDb: voiceBusDb);
    }

    /// <summary>Unity's own ranges for the effect; NaN becomes the neutral end.</summary>
    public EnvironmentReverbParams Clamped() => new(
        C(DryLevelMb, SilentMb, 0f, 0f), C(RoomMb, SilentMb, 0f, SilentMb), C(RoomHfMb, SilentMb, 0f, 0f), C(RoomLfMb, SilentMb, 0f, 0f),
        C(DecayTimeS, 0.1f, 20f, 0.1f), C(DecayHfRatio, 0.1f, 2f, 1f), C(ReflectionsMb, SilentMb, 1000f, SilentMb), C(ReflectDelayS, 0f, 0.3f, 0f),
        C(ReverbMb, SilentMb, 2000f, SilentMb), C(ReverbDelayS, 0f, 0.1f, 0f), C(HfReferenceHz, 20f, 20_000f, 5000f), C(LfReferenceHz, 20f, 1000f, 300f),
        C(DiffusionPct, 0f, 100f, 100f), C(DensityPct, 0f, 100f, 100f), C(MasterWetDb, -80f, 20f, 0f), C(VoiceBusDb, -80f, 20f, 0f));

    private static float C(float v, float lo, float hi, float ifNaN) => float.IsNaN(v) ? ifNaN : Math.Clamp(v, lo, hi);

    /// <summary>Millibels to linear gain; at or below the floor it is exactly 0.</summary>
    public static float GainMb(float mb) => mb <= SilentMb ? 0f : MathF.Pow(10f, mb / 2000f);
}

/// <summary>Which parts of the environment reverb stage apply (config <c>Fidelity.EnvironmentReverb*</c>).</summary>
public readonly record struct EnvironmentReverbToggles(bool Master, bool DryCopy, bool BusGains, bool VoiceSlider)
{
    public static EnvironmentReverbToggles All => new(true, true, true, true);
    public static EnvironmentReverbToggles Default => new(true, true, true, false);
    public static EnvironmentReverbToggles Off => new(false, false, false, false);
}

/// <summary>
/// An approximation of Unity's "SFX Reverb" (FMOD's I3DL2 reverb) driven by its fourteen
/// parameters. Structure: a pre-delay line feeding a six-tap early-reflection FIR at
/// <c>ReflectDelay</c> and, <c>ReverbDelay</c> later, a Freeverb-style bank of eight damped
/// feedback combs into four allpasses. Exact as targets: every level (<c>Room</c>,
/// <c>Reflections</c>, <c>Reverb</c>), both onset delays, the mid-band RT60 (<c>DecayTime</c>) and
/// the two shelves (<c>RoomHF</c> at <c>HFReference</c>, <c>RoomLF</c> at <c>LFReference</c>).
/// Approximate: the network itself, the HF-ratio damping, what Diffusion (allpass coefficient)
/// and Density (comb spread) mean, the early-reflection pattern, and a mono tail. Returns wet
/// only (the effect's own dry copy is the caller's, see <see cref="EnvironmentReverb"/>).
/// Allocation-free after construction; one instance per thread.
/// </summary>
public sealed class SfxReverb
{
    private static readonly int[] CombTunings = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
    private static readonly int[] AllpassTunings = { 556, 441, 341, 225 };
    private static readonly float[] EarlyTapMs = { 0f, 7f, 13f, 19f, 29f, 37f };
    private static readonly float[] EarlyTapGain = { 1f, 0.8f, 0.6f, 0.5f, 0.35f, 0.25f };
    private const float ReferenceRate = 44_100f;
    private const float InputGain = 0.015f;
    private const float MaxPreDelaySeconds = 0.3f + 0.1f; // ReflectDelay + ReverbDelay at Unity's maxima
    private const float MinDensitySpread = 0.6f;
    private const float MaxCombFeedback = 0.98f;

    private readonly float[] _pre;
    private int _preIndex;
    private readonly int[] _earlyOffset;
    private readonly float[] _earlyGain;
    private readonly float[][] _comb;
    private readonly int[] _combBase;
    private readonly int[] _combLength;
    private readonly int[] _combIndex;
    private readonly float[] _combFeedback;
    private readonly float[] _combDamping;
    private readonly float[] _combStore;
    private readonly float[][] _allpass;
    private readonly int[] _allpassIndex;
    private readonly Biquad _highShelf;
    private readonly Biquad _lowShelf;
    private bool _shelvesSet;

    private int _reflectDelay;
    private int _lateDelay;
    private float _earlyLevel;
    private float _lateLevel;
    private float _allpassFeedback = 0.5f;

    public int SampleRate { get; }
    public EnvironmentReverbParams Params { get; private set; }

    public SfxReverb(int sampleRate)
    {
        SampleRate = sampleRate;
        var scale = sampleRate / ReferenceRate;
        _pre = new float[(int)(MaxPreDelaySeconds * sampleRate) + (int)(EarlyTapMs[^1] * sampleRate / 1000f) + 2];
        _earlyOffset = new int[EarlyTapMs.Length];
        _earlyGain = new float[EarlyTapMs.Length];
        var sum = 0f;
        for (var i = 0; i < EarlyTapMs.Length; i++) { _earlyOffset[i] = (int)(EarlyTapMs[i] * sampleRate / 1000f); sum += EarlyTapGain[i]; }
        for (var i = 0; i < EarlyTapMs.Length; i++) _earlyGain[i] = EarlyTapGain[i] / sum;
        _comb = new float[CombTunings.Length][];
        _combBase = new int[CombTunings.Length];
        _combLength = new int[CombTunings.Length];
        _combIndex = new int[CombTunings.Length];
        _combFeedback = new float[CombTunings.Length];
        _combDamping = new float[CombTunings.Length];
        _combStore = new float[CombTunings.Length];
        for (var i = 0; i < CombTunings.Length; i++)
        {
            _combBase[i] = Math.Max(2, (int)(CombTunings[i] * scale));
            _comb[i] = new float[_combBase[i]];
            _combLength[i] = _combBase[i];
        }
        _allpass = new float[AllpassTunings.Length][];
        _allpassIndex = new int[AllpassTunings.Length];
        for (var i = 0; i < AllpassTunings.Length; i++) _allpass[i] = new float[Math.Max(1, (int)(AllpassTunings[i] * scale))];
        _highShelf = new Biquad(sampleRate, 4f);
        _lowShelf = new Biquad(sampleRate, 4f);
        Configure(EnvironmentReverbParams.Bypassed);
    }

    /// <summary>Clears every delay line and the shelf state.</summary>
    public void Reset()
    {
        Array.Clear(_pre);
        _preIndex = 0;
        foreach (var c in _comb) Array.Clear(c);
        foreach (var a in _allpass) Array.Clear(a);
        Array.Clear(_combIndex);
        Array.Clear(_allpassIndex);
        Array.Clear(_combStore);
        _highShelf.Reset();
        _lowShelf.Reset();
    }

    // [impl->REQ-MIXER-RESYNTH]
    /// <summary>Maps the fourteen parameters onto the network; cheap enough to call every frame.</summary>
    public void Configure(in EnvironmentReverbParams raw)
    {
        var p = raw.Clamped();
        var room = EnvironmentReverbParams.GainMb(p.RoomMb);
        _earlyLevel = room * EnvironmentReverbParams.GainMb(p.ReflectionsMb);
        _lateLevel = room * EnvironmentReverbParams.GainMb(p.ReverbMb);
        _reflectDelay = (int)(p.ReflectDelayS * SampleRate);
        _lateDelay = _reflectDelay + (int)(p.ReverbDelayS * SampleRate);

        var spread = MinDensitySpread + (1f - MinDensitySpread) * p.DensityPct / 100f;
        var hfRatio = MathF.Min(p.DecayHfRatio, 1f); // > 1 (a tail brighter than its mid band) would need an in-loop boost; capped
        for (var i = 0; i < _comb.Length; i++)
        {
            var length = Math.Clamp((int)(_combBase[i] * spread), 2, _combBase[i]);
            if (length != _combLength[i])
            {
                _combLength[i] = length;
                if (_combIndex[i] >= length) _combIndex[i] = 0;
            }
            var fb = MathF.Min(MathF.Pow(10f, -3f * length / (p.DecayTimeS * SampleRate)), MaxCombFeedback);
            var fbHf = MathF.Pow(10f, -3f * length / (p.DecayTimeS * hfRatio * SampleRate));
            _combFeedback[i] = fb;
            _combDamping[i] = fb <= 0f ? 0f : Math.Clamp(1f - fbHf / fb, 0f, 0.99f);
        }
        _allpassFeedback = 0.3f + 0.4f * p.DiffusionPct / 100f;

        var prev = Params;
        if (!_shelvesSet || MathF.Abs(prev.RoomHfMb - p.RoomHfMb) > 10f || MathF.Abs(prev.HfReferenceHz - p.HfReferenceHz) > 10f)
            _highShelf.Configure(Biquad.Kind.HighShelf, p.HfReferenceHz, 0.707f, p.RoomHfMb / 100f, 1f, 1f);
        if (!_shelvesSet || MathF.Abs(prev.RoomLfMb - p.RoomLfMb) > 10f || MathF.Abs(prev.LfReferenceHz - p.LfReferenceHz) > 10f)
            _lowShelf.Configure(Biquad.Kind.LowShelf, p.LfReferenceHz, 0.707f, p.RoomLfMb / 100f, 1f, 1f);
        _shelvesSet = true;
        Params = p;
    }

    // [impl->REQ-MIXER-RESYNTH]
    /// <summary>Renders the wet signal for <paramref name="input"/> into <paramref name="wet"/> (same length). Always stepped, so a tail keeps decaying after the input closes.</summary>
    public void Process(ReadOnlySpan<float> input, Span<float> wet)
    {
        var n = Math.Min(input.Length, wet.Length);
        var preLength = _pre.Length;
        for (var s = 0; s < n; s++)
        {
            _pre[_preIndex] = input[s];

            var early = 0f;
            for (var k = 0; k < _earlyOffset.Length; k++)
                early += _earlyGain[k] * _pre[Wrap(_preIndex - _reflectDelay - _earlyOffset[k], preLength)];

            var x = _pre[Wrap(_preIndex - _lateDelay, preLength)] * InputGain;
            var sum = 0f;
            for (var i = 0; i < _comb.Length; i++)
            {
                var buf = _comb[i];
                var idx = _combIndex[i];
                var output = buf[idx];
                var damping = _combDamping[i];
                var store = _combStore[i] = output * (1f - damping) + _combStore[i] * damping;
                buf[idx] = x + store * _combFeedback[i];
                if (++idx >= _combLength[i]) idx = 0;
                _combIndex[i] = idx;
                sum += output;
            }
            var y = sum;
            for (var i = 0; i < _allpass.Length; i++)
            {
                var buf = _allpass[i];
                var idx = _allpassIndex[i];
                var bufOut = buf[idx];
                var output = -y + bufOut;
                buf[idx] = y + bufOut * _allpassFeedback;
                if (++idx >= buf.Length) idx = 0;
                _allpassIndex[i] = idx;
                y = output;
            }

            wet[s] = _earlyLevel * early + _lateLevel * y;
            if (++_preIndex >= preLength) _preIndex = 0;
        }
        var span = wet[..n];
        _highShelf.Process(span);
        _lowShelf.Process(span);
    }

    private static int Wrap(int i, int length)
    {
        i %= length;
        return i < 0 ? i + length : i;
    }
}

/// <summary>
/// The listener-side environment reverb a nearby voice carries on a peer's machine (docs/
/// reference/big-walk-environment-reverb.md section 3): the voice mixer's Dry group (-3 dB) feeds
/// the main mixer's Voice group, which sends to the VOICE DRY BUS (-6 dB, straight to Master Dry)
/// and to the VOICE WET BUS (0 dB) into the Master Wet return, an SFX Reverb whose own
/// <c>DryLevel</c> adds a second, louder dry copy. So
/// <c>x = bus * voice; out = dryBus * x + masterWet * (10^(DryLevel/2000) * x + reverb(x))</c>.
/// Sits last in the encoder-thread chain, on the sum of the Clean and Megaphone voices, because
/// both enter it together. Parameters come straight from the game's live values, unsmoothed
/// (the game smooths its inputs already).
/// </summary>
public sealed class EnvironmentReverb
{
    public const float BusGainDb = -3f;
    public const float DryBusDb = -6f;
    public const float WetBusDb = 0f;

    private readonly SfxReverb _reverb;
    private readonly float[] _wet = new float[8192];

    public int SampleRate { get; }
    public long Frames { get; private set; }
    /// <summary>Linear gain of the straight dry path after the last <see cref="Process"/>.</summary>
    public float DryGain { get; private set; } = 1f;
    /// <summary>Linear gain of the reverb's un-reverbed copy after the last <see cref="Process"/>.</summary>
    public float DryCopyGain { get; private set; }
    /// <summary>Linear gain of the Master Wet return after the last <see cref="Process"/>.</summary>
    public float ReturnGain { get; private set; } = 1f;
    public EnvironmentReverbParams Params => _reverb.Params;

    public EnvironmentReverb(int sampleRate)
    {
        SampleRate = sampleRate;
        _reverb = new SfxReverb(sampleRate);
    }

    /// <summary>Drops the tail (a new talk burst after a long gap).</summary>
    public void Reset() => _reverb.Reset();

    // [impl->REQ-MIXER-RESYNTH]
    /// <summary>
    /// Applies one frame in place. Master off leaves the frame untouched. The output is clamped to
    /// [-1, 1] like the megaphone mix, since the two dry copies sum above unity by design.
    /// </summary>
    public void Process(Span<float> mono, in EnvironmentReverbParams p, in EnvironmentReverbToggles toggles)
    {
        Frames++;
        if (!toggles.Master)
        {
            DryGain = 1f; DryCopyGain = 0f; ReturnGain = 1f;
            return;
        }
        _reverb.Configure(p);
        var clamped = _reverb.Params;
        var bus = (toggles.BusGains ? MixerStageModel.Gain(BusGainDb) : 1f) * (toggles.VoiceSlider ? MixerStageModel.Gain(clamped.VoiceBusDb) : 1f);
        var dryBus = toggles.BusGains ? MixerStageModel.Gain(DryBusDb) : 1f;
        ReturnGain = MixerStageModel.Gain(clamped.MasterWetDb + WetBusDb);
        DryCopyGain = toggles.DryCopy ? EnvironmentReverbParams.GainMb(clamped.DryLevelMb) : 0f;
        DryGain = dryBus + ReturnGain * DryCopyGain;

        for (var i = 0; i < mono.Length; i++) mono[i] *= bus;
        for (var offset = 0; offset < mono.Length; offset += _wet.Length)
        {
            var chunk = mono.Slice(offset, Math.Min(_wet.Length, mono.Length - offset));
            var wet = _wet.AsSpan(0, chunk.Length);
            _reverb.Process(chunk, wet);
            for (var i = 0; i < chunk.Length; i++)
                chunk[i] = Math.Clamp(chunk[i] * DryGain + ReturnGain * wet[i], -1f, 1f);
        }
    }
}
