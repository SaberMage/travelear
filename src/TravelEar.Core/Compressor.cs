namespace TravelEar.Core;

/// <summary>
/// An approximation of Unity's mixer <c>Compressor</c> effect (the FMOD compressor behind it: hard
/// knee, fixed 2.5:1 ratio, exposed threshold / attack / release / make-up gain) for the
/// megaphone's two mixer compressors (ADR-0002: Mixer Stage effects are approximations). Peak
/// detector with exponential attack and release in the linear domain, gain computed above the
/// threshold in dB. Parameters are clamped to FMOD's ranges (attack 0.1-500 ms, release
/// 10-5000 ms, threshold -60..0 dB, make-up -30..30 dB). Mono, one instance per thread.
/// </summary>
public sealed class Compressor
{
    public const float Ratio = 2.5f;

    public float ThresholdDb { get; private set; }
    public float AttackMs { get; private set; }
    public float ReleaseMs { get; private set; }
    public float MakeupDb { get; private set; }
    public int SampleRate { get; }

    private float _envelope;
    private float _attackCoef, _releaseCoef, _threshold, _makeup;

    /// <summary>The last block's gain reduction in dB (0 = none), for diagnostics.</summary>
    public float ReductionDb { get; private set; }

    public Compressor(int sampleRate, float thresholdDb, float attackMs, float releaseMs, float makeupDb)
    {
        SampleRate = sampleRate;
        Set(thresholdDb, attackMs, releaseMs, makeupDb);
    }

    public void Set(float thresholdDb, float attackMs, float releaseMs, float makeupDb)
    {
        ThresholdDb = Math.Clamp(thresholdDb, -60f, 0f);
        AttackMs = Math.Clamp(attackMs, 0.1f, 500f);
        ReleaseMs = Math.Clamp(releaseMs, 10f, 5000f);
        MakeupDb = Math.Clamp(makeupDb, -30f, 30f);
        _threshold = MathF.Pow(10f, ThresholdDb / 20f);
        _makeup = MathF.Pow(10f, MakeupDb / 20f);
        _attackCoef = MathF.Exp(-1f / (AttackMs * 0.001f * SampleRate));
        _releaseCoef = MathF.Exp(-1f / (ReleaseMs * 0.001f * SampleRate));
    }

    public void Reset()
    {
        _envelope = 0f;
        ReductionDb = 0f;
    }

    // [impl->REQ-RENDER-MEGAPHONE]
    /// <summary>Compresses <paramref name="mono"/> in place; no allocation.</summary>
    public void Process(Span<float> mono)
    {
        var maxReduction = 0f;
        for (var i = 0; i < mono.Length; i++)
        {
            var x = mono[i];
            var level = MathF.Abs(x);
            var coef = level > _envelope ? _attackCoef : _releaseCoef;
            _envelope = coef * _envelope + (1f - coef) * level;

            var gain = 1f;
            if (_envelope > _threshold)
            {
                var overDb = 20f * MathF.Log10(_envelope / _threshold);
                var reductionDb = overDb - overDb / Ratio;
                if (reductionDb > maxReduction) maxReduction = reductionDb;
                gain = MathF.Pow(10f, -reductionDb / 20f);
            }
            mono[i] = x * gain * _makeup;
        }
        ReductionDb = maxReduction;
    }
}
