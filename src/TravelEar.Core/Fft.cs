namespace TravelEar.Core;

/// <summary>
/// In-place radix-2 complex FFT over interleaved (re, im) pairs, the shape the phase vocoder
/// wants (<see cref="PitchShifter"/>). Unnormalised in both directions, like the classic
/// smbFft: a forward then inverse pass scales by the frame length. Allocation-free; the twiddles
/// are computed per stage.
/// </summary>
public static class Fft
{
    /// <summary>True for a power of two at least 2.</summary>
    public static bool IsValidLength(int n) => n >= 2 && (n & (n - 1)) == 0;

    // [impl->REQ-MIXER-RESYNTH]
    /// <summary>
    /// Transforms <paramref name="buffer"/> (2 * n floats, re/im interleaved) in place; sign -1 is
    /// the forward transform, +1 the inverse.
    /// </summary>
    public static void Transform(Span<float> buffer, int n, int sign)
    {
        if (!IsValidLength(n)) throw new ArgumentException("The length must be a power of two.", nameof(n));
        if (buffer.Length < 2 * n) throw new ArgumentException("The buffer holds fewer than n complex values.", nameof(buffer));

        // Bit-reversal permutation.
        var bits = 0;
        for (var t = n; t > 1; t >>= 1) bits++;
        for (var i = 0; i < n; i++)
        {
            var r = ReverseBits(i, bits);
            if (r > i)
            {
                (buffer[2 * i], buffer[2 * r]) = (buffer[2 * r], buffer[2 * i]);
                (buffer[2 * i + 1], buffer[2 * r + 1]) = (buffer[2 * r + 1], buffer[2 * i + 1]);
            }
        }

        // Danielson-Lanczos stages.
        for (var le = 2; le <= n; le <<= 1)
        {
            var half = le >> 1;
            var angle = sign * MathF.PI / half;
            var wpr = MathF.Cos(angle);
            var wpi = MathF.Sin(angle);
            var wr = 1f;
            var wi = 0f;
            for (var j = 0; j < half; j++)
            {
                for (var i = j; i < n; i += le)
                {
                    var k = i + half;
                    var tr = wr * buffer[2 * k] - wi * buffer[2 * k + 1];
                    var ti = wr * buffer[2 * k + 1] + wi * buffer[2 * k];
                    buffer[2 * k] = buffer[2 * i] - tr;
                    buffer[2 * k + 1] = buffer[2 * i + 1] - ti;
                    buffer[2 * i] += tr;
                    buffer[2 * i + 1] += ti;
                }
                var next = wr * wpr - wi * wpi;
                wi = wr * wpi + wi * wpr;
                wr = next;
            }
        }
    }

    private static int ReverseBits(int value, int bits)
    {
        var r = 0;
        for (var b = 0; b < bits; b++)
        {
            r = (r << 1) | (value & 1);
            value >>= 1;
        }
        return r;
    }

    /// <summary>The magnitude of bin <paramref name="k"/> of a transformed buffer.</summary>
    public static float Magnitude(ReadOnlySpan<float> buffer, int k)
        => MathF.Sqrt(buffer[2 * k] * buffer[2 * k] + buffer[2 * k + 1] * buffer[2 * k + 1]);

    /// <summary>
    /// The bin with the largest magnitude in <c>[1, n/2)</c> of a real signal's forward transform,
    /// as a frequency in Hz — the test-side "what pitch is this" probe.
    /// </summary>
    public static float DominantFrequency(ReadOnlySpan<float> signal, int n, int sampleRate)
    {
        var work = new float[2 * n];
        for (var i = 0; i < n && i < signal.Length; i++)
        {
            var window = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / n);
            work[2 * i] = signal[i] * window;
        }
        Transform(work, n, -1);
        var best = 1;
        var bestMag = 0f;
        for (var k = 1; k < n / 2; k++)
        {
            var m = Magnitude(work, k);
            if (m > bestMag) { bestMag = m; best = k; }
        }
        return best * (float)sampleRate / n;
    }
}
