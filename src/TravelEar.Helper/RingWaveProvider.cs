using NAudio.Wave;
using TravelEar.Core;

namespace TravelEar.Helper;

/// <summary>
/// NAudio source that drains a <see cref="VoiceRingBuffer"/>. WASAPI's render thread pulls
/// float32 interleaved samples; the ring pads with silence when the pipe falls behind, so the
/// stream never stalls. When the pipe runs ahead (start-up burst, clock drift between the game's
/// DSP and the endpoint) the backlog is trimmed back to <see cref="TargetBacklogMs"/>, so the
/// ring never becomes latency: it holds at most <see cref="MaxBacklogMs"/> before a trim.
/// </summary>
internal sealed class RingWaveProvider : IWaveProvider
{
    public const int MaxBacklogMs = 80;
    public const int TargetBacklogMs = 30;

    public int SampleRate { get; }
    public int Channels { get; }
    public VoiceRingBuffer Ring { get; }
    public WaveFormat WaveFormat { get; }
    public long Trims { get; private set; }
    public long TrimmedSamples { get; private set; }

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

        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, sampleCount * sizeof(float)));
        Ring.Read(floats);
        return sampleCount * sizeof(float);
    }
}
