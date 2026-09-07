namespace TravelEar.Core;

/// <summary>
/// Port of the per-sample voice processing every remote voice gets in the game's
/// <c>SamplePlaybackComponent.ProcessSamples</c> (docs/reference/big-walk-voice-dsp.md section 1),
/// applied to decoded Outbound Voice before it enters the round-trip provider so Local Voice
/// sounds the way peers hear it (<c>REQ-RENDER-CLEAN</c>): a per-sample gain ramp to the makeup
/// gain, the <see cref="VoiceCompressor"/>, then a hyperbolic soft clip with a 0.95 ceiling.
/// Meters what the game meters: the mean absolute input level before gain and compression
/// (<see cref="Arv"/>, the makeup-gain loop's input), the mean output level, the pre-clip peak
/// and the block's deepest gain reduction.
/// </summary>
public sealed class VoiceDynamics
{
    public const float SoftClipKnee = 0.85f;
    public const float SoftClipCeiling = 0.95f;
    public const float SoftClipHeadroom = 0.099999964f;

    private readonly VoiceCompressor _compressor = new();
    private float _currentGain = 1f;

    /// <summary>Mean |input sample| of the last block, before gain and compression.</summary>
    public float Arv { get; private set; }
    /// <summary>Mean |output sample| of the last block, capped at 1.</summary>
    public float OutputArv { get; private set; }
    /// <summary>Largest |sample| seen after the compressor and before the soft clip.</summary>
    public float PreClipPeak { get; private set; }
    /// <summary>Deepest compressor gain in the last block (1 = none).</summary>
    public float Reduction { get; private set; } = 1f;
    /// <summary>Blocks processed since the last <see cref="Reset"/>.</summary>
    public long Blocks { get; private set; }

    // [impl->REQ-RENDER-CLEAN]
    /// <summary>The game's session-change reset: meters cleared, gain snapped to <paramref name="makeupGain"/>, compressor state zeroed.</summary>
    public void Reset(float makeupGain)
    {
        Arv = 0f;
        PreClipPeak = 0f;
        OutputArv = 0f;
        Reduction = 1f;
        _currentGain = makeupGain;
        _compressor.Reset();
        Blocks = 0;
    }

    // [impl->REQ-RENDER-CLEAN]
    /// <summary>
    /// Processes one mono block in place: gain ramped from the previous block's makeup gain to
    /// <paramref name="makeupGain"/> across the block, compressor at <paramref name="threshold"/>,
    /// soft clip. Empty blocks leave the meters and gain untouched.
    /// </summary>
    public void Process(Span<float> mono, float makeupGain, float threshold, int sampleRate)
    {
        var count = mono.Length;
        if (count == 0) return;
        _compressor.Prepare(sampleRate, threshold);

        var step = (makeupGain - _currentGain) / count;
        var gain = _currentGain;
        var arvSum = 0f;
        var outSum = 0f;
        var reduction = 1f;
        var preClipPeak = 0f;
        for (var i = 0; i < count; i++)
        {
            var s = mono[i];
            arvSum += MathF.Abs(s);
            var y = _compressor.Process(s * gain);
            reduction = MathF.Min(reduction, _compressor.Reduction);
            preClipPeak = MathF.Max(preClipPeak, MathF.Abs(y));
            y = SoftClip(y);
            mono[i] = y;
            outSum += MathF.Abs(y);
            gain += step;
        }

        _currentGain = makeupGain;
        Arv = arvSum / count;
        OutputArv = MathF.Min(outSum / count, 1f);
        PreClipPeak = preClipPeak;
        Reduction = reduction;
        Blocks++;
    }

    // [impl->REQ-RENDER-CLEAN]
    /// <summary>Untouched below the knee (0.85); above it a hyperbolic bend that never reaches 0.95.</summary>
    public static float SoftClip(float sample)
    {
        var magnitude = MathF.Abs(sample);
        if (magnitude <= SoftClipKnee) return sample;
        var over = magnitude - SoftClipKnee;
        var y = SoftClipKnee + over * SoftClipHeadroom / (over + SoftClipHeadroom);
        return sample < 0f ? -y : y;
    }
}
