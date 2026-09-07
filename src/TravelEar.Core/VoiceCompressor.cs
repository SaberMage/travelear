namespace TravelEar.Core;

/// <summary>
/// Port of the game's <c>VoiceCompressor</c> (AudioSystem; docs/reference/big-walk-voice-dsp.md
/// section 2): peak follower with instant attack and exponential release, envelope smoothing,
/// soft-knee 2:1 gain reduction above the threshold. Scalar DSP with eight floats of state; the
/// only external input is the threshold the game moves with its voice-volume slider.
/// </summary>
public sealed class VoiceCompressor
{
    public const float MaxThreshold = 0.6f;
    public const float KneeFactor = 1.4f;
    public const float Ratio = 2f;
    public const float AttackSeconds = 0.005f;
    public const float ReleaseSeconds = 0.15f;
    public const float DenormalFloor = 1e-12f;

    private float _peak;
    private float _envelope;
    private float _threshold = MaxThreshold;
    private float _kneeWidth = MaxThreshold * (KneeFactor - 1f);
    private int _sampleRate;
    private float _attackCoefficient;
    private float _releaseCoefficient;

    /// <summary>Gain applied to the last sample (1 = no reduction).</summary>
    public float Reduction { get; private set; } = 1f;

    public float Threshold => _threshold;

    public void Reset()
    {
        _peak = 0f;
        _envelope = 0f;
        Reduction = 1f;
    }

    /// <summary>Called once per block, as the game does: re-reads the threshold and refreshes the coefficients on a rate change.</summary>
    public void Prepare(int sampleRate, float threshold)
    {
        _threshold = threshold;
        _kneeWidth = threshold * (KneeFactor - 1f);
        if (sampleRate > 0 && sampleRate != _sampleRate)
        {
            _sampleRate = sampleRate;
            _attackCoefficient = 1f - MathF.Exp(-1f / (sampleRate * AttackSeconds));
            _releaseCoefficient = 1f - MathF.Exp(-1f / (sampleRate * ReleaseSeconds));
        }
    }

    public float Process(float sample)
    {
        var magnitude = MathF.Abs(sample);
        if (magnitude > _peak)
        {
            _peak = magnitude;
        }
        else
        {
            _peak += (magnitude - _peak) * _releaseCoefficient;
            if (_peak < DenormalFloor) _peak = 0f;
        }
        _envelope += (_peak - _envelope) * _attackCoefficient;
        if (_envelope < DenormalFloor) _envelope = 0f;

        float gain;
        if (_envelope <= _threshold)
        {
            gain = 1f;
        }
        else
        {
            var t = MathF.Min((_envelope - _threshold) / _kneeWidth, 1f);
            gain = MathF.Pow(_threshold / _envelope, t * (1f - 1f / Ratio));
        }
        Reduction = gain;
        return sample * gain;
    }
}
