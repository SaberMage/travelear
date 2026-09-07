namespace TravelEar.Core;

/// <summary>
/// Port of the game's per-player <c>VoiceMakeupGain</c> state machine
/// (docs/reference/big-walk-voice-dsp.md section 3): an automatic gain control that levels each
/// remote voice toward a reference mean absolute level, evaluated once per frame from the last
/// block's <see cref="VoiceDynamics.Arv"/>. One instance per voice; the game's copy is a static
/// dictionary keyed by player name that the mod must not write into (its shared
/// <c>TargetARV</c> and the compressor threshold are read-only inputs here).
/// </summary>
public sealed class VoiceMakeupGain
{
    public const float ReferenceArv = 0.132f;
    public const float CompressorCrest = 4f;
    public const float MinTargetArv = 0.01f;
    public const float MaxTargetArv = 0.4f;
    public const float SlewUpDbPerSecond = 12f;
    public const float SlewDownDbPerSecond = 1f;
    public const float SettleDbPerSecond = 24f;
    public const float SettleSeconds = 1f;
    public const float LevelDropSeconds = 2f;
    public const float LevelClimbSeconds = 6f;
    public const float LevelWarmupSeconds = 0.25f;
    public const float EnvelopeAttackSeconds = 0.15f;
    public const float EnvelopeReleaseSeconds = 1f;
    public const float SpeechFloor = 0.005f;
    public const float RelativeGate = 0.1f;
    public const float ConfidenceSeconds = 0.5f;

    public float Envelope { get; private set; }
    public float Level { get; private set; }
    public float SpeechSeconds { get; private set; }
    public float GainDb { get; private set; }

    /// <summary>The compressor threshold the game derives from its target level: <c>min(target x 4, 0.6)</c>.</summary>
    public static float ThresholdFor(float targetArv) =>
        MathF.Min(MathF.Max(targetArv, MinTargetArv) * CompressorCrest, VoiceCompressor.MaxThreshold);

    public void Reset()
    {
        Envelope = 0f;
        Level = 0f;
        SpeechSeconds = 0f;
        GainDb = 0f;
    }

    // [impl->REQ-RENDER-CLEAN]
    /// <summary>
    /// One frame of the loop. Returns the linear makeup gain for the next block. While not
    /// speaking (or with no time elapsed) nothing updates and the last gain is returned: the gain
    /// freezes between bursts rather than decaying.
    /// </summary>
    public float Evaluate(float arv, bool isSpeaking, float deltaTime, float targetArv)
    {
        if (deltaTime > 0f && isSpeaking)
        {
            if (Level <= 0f)
            {
                if (arv > SpeechFloor)
                {
                    Envelope = arv;
                    Level = arv;
                    SpeechSeconds += deltaTime;
                }
            }
            else
            {
                var tauE = arv > Envelope ? EnvelopeAttackSeconds : EnvelopeReleaseSeconds;
                Envelope += (arv - Envelope) * Clamp01(1f - MathF.Exp(-deltaTime / tauE));
                var gate = MathF.Max(SpeechFloor, Envelope * RelativeGate);
                if (arv > gate)
                {
                    var tauL = Level > Envelope ? LevelDropSeconds : LevelClimbSeconds;
                    var tau = Math.Clamp(SpeechSeconds, LevelWarmupSeconds, tauL);
                    Level += (Envelope - Level) * Clamp01(1f - MathF.Exp(-deltaTime / tau));
                    SpeechSeconds += deltaTime;
                }
            }

            var targetDb = 0f;
            if (Level > 0f)
            {
                targetDb = 20f * MathF.Log10(MathF.Max(ReferenceArv / Level, 1e-4f));
                targetDb *= Clamp01(SpeechSeconds / ConfidenceSeconds);
            }
            var rate = SpeechSeconds < SettleSeconds ? SettleDbPerSecond
                     : targetDb > GainDb ? SlewUpDbPerSecond : SlewDownDbPerSecond;
            GainDb = MoveTowards(GainDb, targetDb, rate * deltaTime);
        }
        return MathF.Pow(10f, GainDb / 20f) * (MathF.Max(targetArv, MinTargetArv) / ReferenceArv);
    }

    private static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;

    private static float MoveTowards(float current, float target, float maxDelta) =>
        MathF.Abs(target - current) <= maxDelta ? target : current + MathF.Sign(target - current) * maxDelta;
}
