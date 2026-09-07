using NAudio.Wave;
using TravelEar.Core;

namespace TravelEar.Helper;

/// <summary>
/// NAudio source that drains a <see cref="VoiceRingBuffer"/>. WASAPI's render thread pulls
/// float32 interleaved samples; the ring pads with silence when the pipe falls behind, so the
/// stream never stalls.
/// </summary>
internal sealed class RingWaveProvider : IWaveProvider
{
    public int SampleRate { get; }
    public int Channels { get; }
    public VoiceRingBuffer Ring { get; }
    public WaveFormat WaveFormat { get; }

    public RingWaveProvider(int sampleRate, int channels, int ringCapacitySamples)
    {
        SampleRate = sampleRate;
        Channels = channels;
        Ring = new VoiceRingBuffer(ringCapacitySamples);
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        var sampleCount = count / sizeof(float);
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, sampleCount * sizeof(float)));
        Ring.Read(floats);
        return sampleCount * sizeof(float);
    }
}
