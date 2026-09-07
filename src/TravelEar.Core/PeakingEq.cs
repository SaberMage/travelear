namespace TravelEar.Core;

/// <summary>
/// The game's per-voice EQ (<c>BiquadFilters</c>, PeakingEQ) ported to Core so the Encoder feed
/// (ADR-0005) can apply it without an in-game <c>AudioSourceController</c>: an RBJ cookbook
/// peaking filter mixed as <c>(1 - dryWet) * x + (dryWet * vol) * biquad(x)</c> and clamped to
/// +-1, exactly the kernel in docs/reference/big-walk-voice-dsp.md section 5 (mono, coefficients
/// constant per instance, so the game's per-block ramp collapses to a single set). The game's
/// voice EQ is 400 Hz, Q 0.3, +30 dB, Vol 0.03 (<see cref="GameVoiceEq"/>).
/// </summary>
public sealed class PeakingEq
{
    private const float DenormalThreshold = 1e-15f;
    private const float ClampLimit = 1f;

    private readonly float _b0, _b1, _b2, _a1, _a2;
    private readonly float _volWet, _dryInv;
    private float _za1, _za2, _zb1, _zb2;

    public float FrequencyHz { get; }
    public float Q { get; }
    public float GainDb { get; }
    public float Vol { get; }
    public float DryWet { get; }

    /// <summary>True when the filter is an exact passthrough (dry/wet 0), so callers may skip it.</summary>
    public bool IsPassthrough => DryWet <= 0f;

    public PeakingEq(float frequencyHz, float q, float gainDb, float vol, float dryWet, int sampleRate)
    {
        FrequencyHz = frequencyHz;
        Q = q;
        GainDb = gainDb;
        Vol = vol;
        DryWet = Math.Clamp(dryWet, 0f, 1f);

        // UpdateVariables + CoefficientCalculation(PeakingEQ), then the _invA0 normalisation.
        var w0 = frequencyHz / sampleRate * 6.2831855f;
        var cosW0 = MathF.Cos(w0);
        var alpha = MathF.Sin(w0) / (2f * q);
        var a = MathF.Pow(10f, gainDb / 40f);
        if (a == 0f) a = 1f;

        var b0 = 1f + alpha * a;
        var b1 = -2f * cosW0;
        var b2 = 1f - alpha * a;
        var a0 = 1f + alpha / a;
        var a1 = -2f * cosW0;
        var a2 = 1f - alpha / a;
        var invA0 = a0 == 0f ? float.PositiveInfinity : 1f / a0;

        _b0 = b0 * invA0;
        _b1 = b1 * invA0;
        _b2 = b2 * invA0;
        _a1 = a1 * invA0;
        _a2 = a2 * invA0;
        _volWet = DryWet * vol;
        _dryInv = 1f - DryWet;
    }

    /// <summary>The game's voice EQ as <c>VoicePlayer.PlayVoice</c> configures it, at the given wet mix.</summary>
    public static PeakingEq GameVoiceEq(float dryWet, int sampleRate) => new(400f, 0.3f, 30f, 0.03f, dryWet, sampleRate);

    /// <summary>Clears the delay line (a new talk burst, as the game does on a fresh component).</summary>
    public void Reset()
    {
        _za1 = _za2 = _zb1 = _zb2 = 0f;
    }

    // [impl->REQ-EAR-SELF]
    /// <summary>Filters <paramref name="mono"/> in place. No allocation; safe on any thread that owns the instance.</summary>
    public void Process(Span<float> mono)
    {
        if (IsPassthrough) return;
        for (var i = 0; i < mono.Length; i++)
        {
            var x = mono[i];
            var y = _b0 * x + _b1 * _zb1 + _b2 * _zb2 - _a1 * _za1 - _a2 * _za2;
            if (y < DenormalThreshold && y > -DenormalThreshold) y = 0f;
            var o = y * _volWet + x * _dryInv;
            if (o > ClampLimit) o = ClampLimit;
            else if (o < -ClampLimit) o = -ClampLimit;
            mono[i] = o;
            _zb2 = _zb1;
            _zb1 = x;
            _za2 = _za1;
            _za1 = y;
        }
    }
}
