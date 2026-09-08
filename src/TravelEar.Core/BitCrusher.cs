namespace TravelEar.Core;

/// <summary>
/// The game's <c>BitCrusher</c> (AudioSystem) ported from its managed Burst fallback
/// (<c>Process$BurstManaged</c>, docs/reference/big-walk-local-voice-wiring.md and the T2 notes in
/// M3-PLAN.md): per hold of <c>step = sampleRate / crushRate</c> samples, the block's first sample
/// is quantized to <c>bitDepth</c> bits and held, ramping linearly toward the next block's quantized
/// first sample by the <c>smooth</c> fraction, then mixed <c>dryWet</c> with the input. Quantization:
/// <c>ampVal = 2^(bitDepth-1)</c>, <c>crushScale = log(bitDepth+1) / log(25) / ampVal</c>,
/// <c>q(x) = clamp(trunc(x * ampVal +- 0.5) * crushScale, -1, 1)</c>. The game runs it at 24 bits,
/// where the quantization is inaudible and the sample-hold is the whole effect. Mono; stateless
/// across frames, as the game's kernel is across DSP blocks.
/// </summary>
public sealed class BitCrusher
{
    public int BitDepth { get; }
    public int CrushRateHz { get; private set; }
    public float DryWet { get; private set; }
    public float Smooth { get; private set; }

    private readonly float _ampVal;
    private readonly float _crushScale;

    public BitCrusher(int bitDepth, int crushRateHz, float dryWet, float smooth)
    {
        BitDepth = Math.Max(1, bitDepth);
        _ampVal = MathF.Pow(2f, BitDepth - 1f);
        _crushScale = MathF.Log(BitDepth + 1f) / MathF.Log(25f) / _ampVal;
        Set(crushRateHz, dryWet, smooth);
    }

    /// <summary>The per-frame parameters the game's <c>VoicePlayer.Update</c> drives.</summary>
    public void Set(int crushRateHz, float dryWet, float smooth)
    {
        CrushRateHz = Math.Max(1, crushRateHz);
        DryWet = Math.Clamp(dryWet, 0f, 1f);
        Smooth = Math.Clamp(smooth, 0f, 1f);
    }

    /// <summary>The game's hold length: <c>max(1, outputSampleRate / crushRate)</c> (integer division).</summary>
    public int StepFor(int sampleRate) => Math.Max(1, sampleRate / CrushRateHz);

    private float Quantize(float x)
    {
        var v = x * _ampVal;
        v = v >= 0f ? v + 0.5f : v - 0.5f;
        var q = (int)v * _crushScale; // truncation toward zero, as cvttss2si
        return q > 1f ? 1f : q < -1f ? -1f : q;
    }

    // [impl->REQ-RENDER-MEGAPHONE]
    /// <summary>Processes <paramref name="mono"/> in place at <paramref name="sampleRate"/>; no allocation.</summary>
    public void Process(Span<float> mono, int sampleRate)
    {
        var n = mono.Length;
        if (n == 0) return;
        var step = StepFor(sampleRate);
        var wet = DryWet;
        var dry = 1f - DryWet;
        if (step == 1)
        {
            for (var i = 0; i < n; i++) mono[i] = Quantize(mono[i]) * wet + mono[i] * dry;
            return;
        }
        for (var start = 0; start < n; start += step)
        {
            var q0 = Quantize(mono[start]);
            var next = start + step;
            var slope = 0f;
            if (next < n && Smooth > 0f)
                slope = (Quantize(mono[next]) - q0) / step * Smooth;
            for (var k = 0; k < step; k++)
            {
                var idx = start + k;
                if (idx >= n) break;
                mono[idx] = (q0 + k * slope) * wet + mono[idx] * dry;
            }
        }
    }
}
