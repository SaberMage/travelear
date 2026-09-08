namespace TravelEar.Core;

/// <summary>
/// A general RBJ-cookbook biquad with the game's <c>BiquadFilters</c> kernel (docs/reference/
/// big-walk-voice-dsp.md section 5): output <c>(1 - dryWet) * x + (dryWet * vol) * biquad(x)</c>,
/// denormal flush, clamp. <see cref="PeakingEq"/> is the same kernel specialised for the voice
/// EQ; this one serves the Mixer Stage's high cut (HighShelf) and the megaphone's 300 Hz HighPass.
/// Mono, coefficients constant until <see cref="Configure"/> is called again.
/// </summary>
public sealed class Biquad
{
    public enum Kind { LowPass, HighPass, HighShelf, PeakingEq, LowShelf }

    private const float DenormalThreshold = 1e-15f;

    private float _b0, _b1, _b2, _a1, _a2;
    private float _volWet = 1f, _dryInv;
    private float _za1, _za2, _zb1, _zb2;

    public Kind Type { get; private set; }
    public float FrequencyHz { get; private set; }
    public float Q { get; private set; }
    public float GainDb { get; private set; }
    public int SampleRate { get; }
    public float ClampLimit { get; }

    public Biquad(int sampleRate, float clampLimit = 1f)
    {
        SampleRate = sampleRate;
        ClampLimit = clampLimit;
        Configure(Kind.PeakingEq, 1000f, 0.7f, 0f, 1f, 0f);
    }

    /// <summary>The game's coefficient table (<c>CoefficientCalculation</c>) for the four kinds the mod uses, plus the RBJ low shelf (the environment reverb's <c>RoomLF</c>).</summary>
    public void Configure(Kind type, float frequencyHz, float q, float gainDb, float vol, float dryWet)
    {
        Type = type;
        FrequencyHz = frequencyHz;
        Q = q;
        GainDb = gainDb;
        dryWet = Math.Clamp(dryWet, 0f, 1f);

        var w0 = frequencyHz / SampleRate * 6.2831855f;
        var cos = MathF.Cos(w0);
        var alpha = MathF.Sin(w0) / (2f * q);
        var a = MathF.Pow(10f, gainDb / 40f);
        if (a == 0f) a = 1f;
        var sqrtAAlpha = MathF.Sqrt(a) * alpha;

        float b0, b1, b2, a0, a1, a2;
        switch (type)
        {
            case Kind.LowPass:
                b0 = (1f - cos) / 2f; b1 = 1f - cos; b2 = (1f - cos) / 2f;
                a0 = 1f + alpha; a1 = -2f * cos; a2 = 1f - alpha;
                break;
            case Kind.HighPass:
                b0 = (1f + cos) / 2f; b1 = -(1f + cos); b2 = (1f + cos) / 2f;
                a0 = 1f + alpha; a1 = -2f * cos; a2 = 1f - alpha;
                break;
            case Kind.HighShelf:
                b0 = a * ((a + 1f) + (a - 1f) * cos + 2f * sqrtAAlpha);
                b1 = -2f * a * ((a - 1f) + (a + 1f) * cos);
                b2 = a * ((a + 1f) + (a - 1f) * cos - 2f * sqrtAAlpha);
                a0 = (a + 1f) - (a - 1f) * cos + 2f * sqrtAAlpha;
                a1 = 2f * ((a - 1f) - (a + 1f) * cos);
                a2 = (a + 1f) - (a - 1f) * cos - 2f * sqrtAAlpha;
                break;
            case Kind.LowShelf:
                b0 = a * ((a + 1f) - (a - 1f) * cos + 2f * sqrtAAlpha);
                b1 = 2f * a * ((a - 1f) - (a + 1f) * cos);
                b2 = a * ((a + 1f) - (a - 1f) * cos - 2f * sqrtAAlpha);
                a0 = (a + 1f) + (a - 1f) * cos + 2f * sqrtAAlpha;
                a1 = -2f * ((a - 1f) + (a + 1f) * cos);
                a2 = (a + 1f) + (a - 1f) * cos - 2f * sqrtAAlpha;
                break;
            default:
                b0 = 1f + alpha * a; b1 = -2f * cos; b2 = 1f - alpha * a;
                a0 = 1f + alpha / a; a1 = -2f * cos; a2 = 1f - alpha / a;
                break;
        }
        var invA0 = a0 == 0f ? float.PositiveInfinity : 1f / a0;
        _b0 = b0 * invA0; _b1 = b1 * invA0; _b2 = b2 * invA0; _a1 = a1 * invA0; _a2 = a2 * invA0;
        _volWet = dryWet * vol;
        _dryInv = 1f - dryWet;
    }

    public void Reset()
    {
        _za1 = _za2 = _zb1 = _zb2 = 0f;
    }

    /// <summary>Filters <paramref name="mono"/> in place; no allocation.</summary>
    public void Process(Span<float> mono)
    {
        var limit = ClampLimit;
        for (var i = 0; i < mono.Length; i++)
        {
            var x = mono[i];
            var y = _b0 * x + _b1 * _zb1 + _b2 * _zb2 - _a1 * _za1 - _a2 * _za2;
            if (y < DenormalThreshold && y > -DenormalThreshold) y = 0f;
            var o = y * _volWet + x * _dryInv;
            if (o > limit) o = limit;
            else if (o < -limit) o = -limit;
            mono[i] = o;
            _zb2 = _zb1;
            _zb1 = x;
            _za2 = _za1;
            _za1 = y;
        }
    }
}
