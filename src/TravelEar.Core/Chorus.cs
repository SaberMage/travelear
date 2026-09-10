namespace TravelEar.Core;

/// <summary>
/// A three-tap chorus in the shape of Unity's <c>AudioChorusFilter</c> / FMOD's chorus DSP:
/// <c>out = dry * x + wet1 * d1(x) + wet2 * d2(x) + wet3 * d3(x)</c>, each tap a delay line
/// read at <c>Delay * (1 - Depth * (1 + sin(2 pi Rate t + phase_k)) / 2)</c> with the three
/// LFO phases 120 degrees apart and linear interpolation. Used at the asset's fixed values on
/// the game's <c>Master Super Wet</c> chain (docs/reference/big-walk-voice-effects-catalog.md
/// section 2: dry 1, wet 0.75 / 0.5 / 0.25, delay 100 ms, rate 0.5 Hz, depth 0.5). The LFO
/// shape and the depth-to-delay mapping are the approximation; the tap layout and levels are the
/// asset's. Mono, allocation-free after construction.
/// </summary>
public sealed class Chorus
{
    public const int Taps = 3;

    private readonly float[] _line;
    private readonly float[] _wet = new float[Taps];
    private readonly float _maxDelaySamples;
    private readonly float _phaseStep;
    private int _write;
    private float _phase;

    public int SampleRate { get; }
    public float DryMix { get; }
    public float DelayMs { get; }
    public float RateHz { get; }
    public float Depth { get; }

    public Chorus(int sampleRate, float dryMix, float wet1, float wet2, float wet3, float delayMs, float rateHz, float depth)
    {
        SampleRate = sampleRate;
        DryMix = dryMix;
        _wet[0] = wet1; _wet[1] = wet2; _wet[2] = wet3;
        DelayMs = MathF.Max(0.1f, delayMs);
        RateHz = MathF.Max(0f, rateHz);
        Depth = Math.Clamp(depth, 0f, 1f);
        _maxDelaySamples = DelayMs * sampleRate / 1000f;
        _line = new float[(int)MathF.Ceiling(_maxDelaySamples) + 2];
        _phaseStep = 2f * MathF.PI * RateHz / sampleRate;
    }

    /// <summary>The game's fixed super-wet chorus.</summary>
    public static Chorus SuperWet(int sampleRate) => new(sampleRate, 1f, 0.75f, 0.5f, 0.25f, 100f, 0.5f, 0.5f);

    public void Reset()
    {
        Array.Clear(_line);
        _write = 0;
        _phase = 0f;
    }

    // [impl->REQ-MIXER-RESYNTH]
    /// <summary>Applies the chorus in place.</summary>
    public void Process(Span<float> mono)
    {
        var length = _line.Length;
        for (var i = 0; i < mono.Length; i++)
        {
            var x = mono[i];
            _line[_write] = x;
            var y = DryMix * x;
            for (var t = 0; t < Taps; t++)
            {
                var lfo = 0.5f * (1f + MathF.Sin(_phase + t * (2f * MathF.PI / Taps)));
                var delay = _maxDelaySamples * (1f - Depth * lfo);
                if (delay < 1f) delay = 1f;
                var readPos = _write - delay;
                while (readPos < 0f) readPos += length;
                var i0 = (int)readPos;
                var frac = readPos - i0;
                var i1 = i0 + 1; if (i1 >= length) i1 -= length;
                y += _wet[t] * (_line[i0] * (1f - frac) + _line[i1] * frac);
            }
            mono[i] = y;
            if (++_write >= length) _write = 0;
            _phase += _phaseStep;
            if (_phase > 2f * MathF.PI) _phase -= 2f * MathF.PI;
        }
    }
}
