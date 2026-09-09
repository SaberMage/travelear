namespace TravelEar.Core;

/// <summary>
/// The main mixer's <c>Master</c> group limiter (docs/reference/big-walk-voice-effects-catalog.md
/// section 5.15): a Duck Volume the group sends into itself, threshold
/// <c>MasterLimiterThreshold</c> (asset default -3 dB, never written by the game), ratio 10,
/// attack 0, release 0.125 s, make-up 0, knee 20 dB. Last thing a listener's mix goes through
/// before the speakers, so it caps the sum of the dry copies and the reverb on loud speech.
/// Approximation: the shared <see cref="Compressor"/> detector with FMOD's attack floor.
/// </summary>
public sealed class MasterLimiter
{
    public const float DefaultThresholdDb = -3f;
    public const float Ratio = 10f;
    public const float AttackMs = 0.1f;
    public const float ReleaseMs = 125f;
    public const float KneeDb = 20f;

    private readonly Compressor _compressor;

    public float ThresholdDb => _compressor.ThresholdDb;
    public float ReductionDb => _compressor.ReductionDb;

    public MasterLimiter(int sampleRate)
    {
        _compressor = new Compressor(sampleRate, DefaultThresholdDb, AttackMs, ReleaseMs, 0f, Ratio, KneeDb);
    }

    public void Reset() => _compressor.Reset();

    // [impl->REQ-MIXER-RESYNTH]
    /// <summary>Limits <paramref name="mono"/> in place at <paramref name="thresholdDb"/> (the live mixer float, or the asset default).</summary>
    public void Process(Span<float> mono, float thresholdDb = DefaultThresholdDb)
    {
        if (float.IsNaN(thresholdDb)) thresholdDb = DefaultThresholdDb;
        if (MathF.Abs(thresholdDb - _compressor.ThresholdDb) > 0.01f)
            _compressor.Set(thresholdDb, AttackMs, ReleaseMs, 0f, Ratio, KneeDb);
        _compressor.Process(mono);
    }
}
