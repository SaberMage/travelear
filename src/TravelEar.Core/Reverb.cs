namespace TravelEar.Core;

/// <summary>
/// The Mixer Stage's reverb approximation (ADR-0002: the game's mixer reverb cannot be read, so it
/// is approximated and calibrated by ear against a second-client recording). A Schroeder /
/// Freeverb-style mono reverb: eight parallel damped feedback combs summed into four series
/// allpasses. The comb feedback follows the requested decay time (RT60) at each comb's delay, so
/// one knob (<see cref="DecaySeconds"/>) sets the tail. Returns wet only; the caller mixes.
/// Allocation-free after construction; one instance per thread.
/// </summary>
public sealed class Reverb
{
    // Freeverb tunings at 44.1 kHz, scaled to the sample rate.
    private static readonly int[] CombTunings = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
    private static readonly int[] AllpassTunings = { 556, 441, 341, 225 };
    private const float ReferenceRate = 44_100f;
    private const float AllpassFeedback = 0.5f;
    private const float InputGain = 0.015f;
    private const float Damping = 0.2f;

    private readonly float[][] _comb;
    private readonly int[] _combIndex;
    private readonly float[] _combFeedback;
    private readonly float[] _combFilterStore;
    private readonly float[][] _allpass;
    private readonly int[] _allpassIndex;

    public int SampleRate { get; }
    public float DecaySeconds { get; private set; }

    public Reverb(int sampleRate, float decaySeconds)
    {
        SampleRate = sampleRate;
        var scale = sampleRate / ReferenceRate;
        _comb = new float[CombTunings.Length][];
        _combIndex = new int[CombTunings.Length];
        _combFeedback = new float[CombTunings.Length];
        _combFilterStore = new float[CombTunings.Length];
        for (var i = 0; i < CombTunings.Length; i++)
            _comb[i] = new float[Math.Max(1, (int)(CombTunings[i] * scale))];
        _allpass = new float[AllpassTunings.Length][];
        _allpassIndex = new int[AllpassTunings.Length];
        for (var i = 0; i < AllpassTunings.Length; i++)
            _allpass[i] = new float[Math.Max(1, (int)(AllpassTunings[i] * scale))];
        SetDecay(decaySeconds);
    }

    /// <summary>Sets the RT60 in seconds: comb feedback <c>10^(-3 * delay / (rt60 * rate))</c>, capped below 1.</summary>
    public void SetDecay(float seconds)
    {
        DecaySeconds = MathF.Max(0.05f, seconds);
        for (var i = 0; i < _comb.Length; i++)
        {
            var fb = MathF.Pow(10f, -3f * _comb[i].Length / (DecaySeconds * SampleRate));
            _combFeedback[i] = MathF.Min(fb, 0.98f);
        }
    }

    public void Reset()
    {
        foreach (var c in _comb) Array.Clear(c);
        foreach (var a in _allpass) Array.Clear(a);
        Array.Clear(_combIndex);
        Array.Clear(_allpassIndex);
        Array.Clear(_combFilterStore);
    }

    // [impl->REQ-MIXER-RESYNTH]
    /// <summary>One sample in, the wet sample out.</summary>
    public float Process(float input)
    {
        var x = input * InputGain;
        var sum = 0f;
        for (var i = 0; i < _comb.Length; i++)
        {
            var buf = _comb[i];
            var idx = _combIndex[i];
            var output = buf[idx];
            var store = _combFilterStore[i] = output * (1f - Damping) + _combFilterStore[i] * Damping;
            buf[idx] = x + store * _combFeedback[i];
            if (++idx >= buf.Length) idx = 0;
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
            buf[idx] = y + bufOut * AllpassFeedback;
            if (++idx >= buf.Length) idx = 0;
            _allpassIndex[i] = idx;
            y = output;
        }
        return y;
    }
}
