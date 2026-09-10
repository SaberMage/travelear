namespace TravelEar.Core;

/// <summary>
/// A phase-vocoder pitch shifter: pitch without tempo, the way Unity's mixer "Pitch Shifter"
/// effect works (FFT 1024, overlap 4 on the game's voice mixer <c>Dry</c> group and its
/// <c>Master Super Wet</c> chain; docs/reference/big-walk-voice-effects-catalog.md section 5.5).
/// The structure is the classic smbPitchShift: an input FIFO of one frame, a Hann-windowed
/// analysis FFT per hop, per-bin instantaneous frequency from the phase difference, bins
/// re-mapped by the ratio, synthesis by phase accumulation, overlap-add into an output FIFO.
/// Latency is <c>FrameSize - HopSize</c> samples (768 at overlap 4: 16 ms at 48 kHz). Streaming
/// and allocation-free after construction; one instance per thread. A ratio of 1 is a
/// near-identity (the analysis/synthesis windows satisfy COLA), so the effect can stay in the
/// chain as it does in the game. Downward ratios (the game's only use: <c>VoicePitch</c> and
/// <c>SuperWetPitch</c> are 1 minus a deduction) keep the level; upward ratios come out
/// quieter because the bin re-mapping leaves target bins empty.
/// </summary>
public sealed class PitchShifter
{
    public const int DefaultFrameSize = 1024;
    public const int DefaultOverlap = 4;
    public const float MinRatio = 0.25f;
    public const float MaxRatio = 4f;

    private readonly int _frameSize;
    private readonly int _hop;
    private readonly int _overlap;
    private readonly int _latency;
    private readonly float[] _inFifo;
    private readonly float[] _outFifo;
    private readonly float[] _work;
    private readonly float[] _lastPhase;
    private readonly float[] _sumPhase;
    private readonly float[] _outputAccum;
    private readonly float[] _anaMagn;
    private readonly float[] _anaFreq;
    private readonly float[] _synMagn;
    private readonly float[] _synFreq;
    private readonly float[] _window;
    private readonly float _freqPerBin;
    private readonly float _expectedPhase;
    private int _rover;

    public int SampleRate { get; }
    public int FrameSize => _frameSize;
    public int HopSize => _hop;
    /// <summary>Samples of delay between input and output.</summary>
    public int LatencySamples => _latency;
    /// <summary>The ratio the last frame was rendered with.</summary>
    public float Ratio { get; private set; } = 1f;

    public PitchShifter(int sampleRate, int frameSize = DefaultFrameSize, int overlap = DefaultOverlap)
    {
        if (!Fft.IsValidLength(frameSize)) throw new ArgumentException("The frame size must be a power of two.", nameof(frameSize));
        if (overlap < 1 || frameSize % overlap != 0) throw new ArgumentException("The overlap must divide the frame size.", nameof(overlap));
        SampleRate = sampleRate;
        _frameSize = frameSize;
        _overlap = overlap;
        _hop = frameSize / overlap;
        _latency = frameSize - _hop;
        _inFifo = new float[frameSize];
        _outFifo = new float[frameSize];
        _work = new float[2 * frameSize];
        _lastPhase = new float[frameSize / 2 + 1];
        _sumPhase = new float[frameSize / 2 + 1];
        _outputAccum = new float[2 * frameSize];
        _anaMagn = new float[frameSize];
        _anaFreq = new float[frameSize];
        _synMagn = new float[frameSize];
        _synFreq = new float[frameSize];
        _window = new float[frameSize];
        for (var k = 0; k < frameSize; k++) _window[k] = -0.5f * MathF.Cos(2f * MathF.PI * k / frameSize) + 0.5f;
        _freqPerBin = (float)sampleRate / frameSize;
        _expectedPhase = 2f * MathF.PI * _hop / frameSize;
        _rover = _latency;
    }

    /// <summary>Clears every FIFO and phase accumulator (a new talk burst after a gap).</summary>
    public void Reset()
    {
        Array.Clear(_inFifo);
        Array.Clear(_outFifo);
        Array.Clear(_lastPhase);
        Array.Clear(_sumPhase);
        Array.Clear(_outputAccum);
        _rover = _latency;
    }

    // [impl->REQ-MIXER-RESYNTH]
    /// <summary>
    /// Shifts <paramref name="mono"/> in place by <paramref name="ratio"/> (0.5 = an octave down,
    /// clamped to <see cref="MinRatio"/>..<see cref="MaxRatio"/>). The output lags the input by
    /// <see cref="LatencySamples"/>.
    /// </summary>
    public void Process(Span<float> mono, float ratio)
    {
        ratio = float.IsNaN(ratio) ? 1f : Math.Clamp(ratio, MinRatio, MaxRatio);
        Ratio = ratio;
        var n = _frameSize;
        var half = n / 2;
        for (var i = 0; i < mono.Length; i++)
        {
            _inFifo[_rover] = mono[i];
            mono[i] = _outFifo[_rover - _latency];
            _rover++;
            if (_rover < n) continue;
            _rover = _latency;

            // Analysis: windowed frame -> bins -> magnitude and true frequency per bin.
            for (var k = 0; k < n; k++)
            {
                _work[2 * k] = _inFifo[k] * _window[k];
                _work[2 * k + 1] = 0f;
            }
            Fft.Transform(_work, n, -1);
            for (var k = 0; k <= half; k++)
            {
                var re = _work[2 * k];
                var im = _work[2 * k + 1];
                var magn = 2f * MathF.Sqrt(re * re + im * im);
                var phase = MathF.Atan2(im, re);
                var tmp = phase - _lastPhase[k];
                _lastPhase[k] = phase;
                tmp -= k * _expectedPhase;
                var qpd = (int)(tmp / MathF.PI);
                if (qpd >= 0) qpd += qpd & 1; else qpd -= qpd & 1;
                tmp -= MathF.PI * qpd;
                tmp = _overlap * tmp / (2f * MathF.PI);
                tmp = k * _freqPerBin + tmp * _freqPerBin;
                _anaMagn[k] = magn;
                _anaFreq[k] = tmp;
            }

            // Processing: re-map the bins by the ratio.
            Array.Clear(_synMagn, 0, half + 1);
            Array.Clear(_synFreq, 0, half + 1);
            for (var k = 0; k <= half; k++)
            {
                var index = (int)(k * ratio);
                if (index > half) break;
                _synMagn[index] += _anaMagn[k];
                _synFreq[index] = _anaFreq[k] * ratio;
            }

            // Synthesis: phase accumulation -> bins -> inverse FFT -> overlap-add.
            for (var k = 0; k <= half; k++)
            {
                var magn = _synMagn[k];
                var tmp = _synFreq[k];
                tmp -= k * _freqPerBin;
                tmp /= _freqPerBin;
                tmp = 2f * MathF.PI * tmp / _overlap;
                tmp += k * _expectedPhase;
                _sumPhase[k] += tmp;
                var phase = _sumPhase[k];
                _work[2 * k] = magn * MathF.Cos(phase);
                _work[2 * k + 1] = magn * MathF.Sin(phase);
            }
            for (var k = n + 2; k < 2 * n; k++) _work[k] = 0f;
            Fft.Transform(_work, n, 1);
            // 2 / (N/2 * overlap) undoes the unnormalised FFT pair and the analysis factor of 2;
            // the Hann analysis x synthesis windows then overlap-add to overlap * 3/8 (1.5 at 4),
            // which is divided out so a ratio of 1 keeps the level.
            var scale = 2f / (half * _overlap) / (_overlap * 0.375f);
            for (var k = 0; k < n; k++) _outputAccum[k] += _window[k] * _work[2 * k] * scale;
            for (var k = 0; k < _hop; k++) _outFifo[k] = _outputAccum[k];
            Array.Copy(_outputAccum, _hop, _outputAccum, 0, n);
            Array.Copy(_inFifo, _hop, _inFifo, 0, _latency);
        }
    }
}
