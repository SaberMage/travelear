using NAudio.Wave;
using TravelEar.Core;

namespace TravelEar.Helper;

/// <summary>
/// NAudio source that drains a <see cref="VoiceRingBuffer"/>. WASAPI's render thread pulls
/// float32 interleaved samples; the ring pads with silence when the pipe falls behind, so the
/// stream never stalls. When the pipe runs ahead (start-up burst, clock drift between the game's
/// DSP and the endpoint) the backlog is trimmed back to <see cref="TargetBacklogMs"/>, so the
/// ring never becomes unbounded latency: it holds at most <see cref="MaxBacklogMs"/> before a trim.
/// <para>
/// The band between the two is the jitter budget: a pad (underrun) is a gap and a trim is a cut,
/// and either lands mid-word as a click. M1 ran 30-80 ms, and M2 run 4 heard "debris" with the
/// game rendering; the per-frame Offset spread inside one 10 s window was ~200 ms, so the band is
/// now 100-200 ms, and the ring is primed with <see cref="TargetBacklogMs"/> of silence at stream
/// start (<see cref="Prime"/>): without that it floated at whatever the first frames left, 40-120 ms
/// in M2 run 5, and a ~200 ms stall drained it (five pads in one second, heard as a crackle burst).
/// The cost is ~100 ms more Offset, which the Offset line reports.
/// </para>
/// </summary>
internal sealed class RingWaveProvider : IWaveProvider
{
    public const int MaxBacklogMs = 200;
    public const int TargetBacklogMs = 100;

    public int SampleRate { get; }
    public int Channels { get; }
    public VoiceRingBuffer Ring { get; }
    public WaveFormat WaveFormat { get; }
    public long Trims { get; private set; }
    public long TrimmedSamples { get; private set; }

    /// <summary>Capture timestamps keyed by ring position: the pipe thread marks each frame it stores (<c>REQ-OFFSET-MEASURE</c>).</summary>
    public FrameStampTable Stamps { get; } = new();

    /// <summary>Seconds of audio the endpoint has played so far, from the output's audio clock; null = unknown (fall back to nominal latency).</summary>
    public Func<double?>? PlayedSeconds { get; set; }

    /// <summary>Nominal output latency used when <see cref="PlayedSeconds"/> is unavailable.</summary>
    public double FallbackLatencySeconds { get; set; }

    /// <summary>Raised on the render thread with one report per resolved frame; must not block.</summary>
    public Action<OffsetReport>? Reported { get; set; }

    public long Reports { get; private set; }
    private long _bytesProvided;

    private readonly int _maxBacklogSamples;
    private readonly int _targetBacklogSamples;

    public RingWaveProvider(int sampleRate, int channels, int ringCapacitySamples)
    {
        SampleRate = sampleRate;
        Channels = channels;
        Ring = new VoiceRingBuffer(ringCapacitySamples);
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        _maxBacklogSamples = sampleRate * channels * MaxBacklogMs / 1000;
        _targetBacklogSamples = sampleRate * channels * TargetBacklogMs / 1000;
    }

    /// <summary>Queues <see cref="TargetBacklogMs"/> of silence ahead of the first frame; the pipe thread calls it once before playback starts.</summary>
    public void Prime()
    {
        Ring.Write(new float[_targetBacklogSamples]);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        var sampleCount = count / sizeof(float);

        var backlog = Ring.Count;
        if (backlog > _maxBacklogSamples)
        {
            var excess = backlog - _targetBacklogSamples;
            excess -= excess % Channels; // keep channel alignment
            TrimmedSamples += Ring.Discard(excess);
            Trims++;
        }

        // [impl->REQ-OFFSET-MEASURE]
        // The first sample of this read is at the ring's read position; if a capture stamp covers
        // it, its render time is now plus whatever is still queued ahead of it in the endpoint.
        var readPosition = Ring.ReadPosition;
        if (Stamps.TryResolve(readPosition, 0, out var captured))
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var providedSeconds = (double)_bytesProvided / WaveFormat.AverageBytesPerSecond;
            var played = PlayedSeconds?.Invoke();
            var queuedSeconds = played is double p ? Math.Max(0, providedSeconds - p) : FallbackLatencySeconds;
            var renderAt = now + (long)(queuedSeconds * System.Diagnostics.Stopwatch.Frequency);
            Reports++;
            Reported?.Invoke(new OffsetReport(captured, renderAt));
        }

        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, sampleCount * sizeof(float)));
        Ring.Read(floats);
        _bytesProvided += sampleCount * sizeof(float);
        return sampleCount * sizeof(float);
    }
}
