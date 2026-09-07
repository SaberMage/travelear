using NAudio.Wave;

namespace TravelEar.Helper;

/// <summary>Continuous sine tone for <c>--tone</c>: lets the OBS capture spike run with no game.</summary>
internal sealed class ToneWaveProvider : IWaveProvider
{
    private readonly int _channels;
    private readonly float _amplitude;
    private readonly double _phaseStep;
    private double _phase;

    public WaveFormat WaveFormat { get; }

    public ToneWaveProvider(int sampleRate, int channels, float frequencyHz, float amplitude)
    {
        _channels = channels;
        _amplitude = amplitude;
        _phaseStep = 2 * Math.PI * frequencyHz / sampleRate;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        var frames = count / (sizeof(float) * _channels);
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, frames * _channels * sizeof(float)));
        for (var f = 0; f < frames; f++)
        {
            var s = (float)(Math.Sin(_phase) * _amplitude);
            _phase += _phaseStep;
            if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
            for (var c = 0; c < _channels; c++) floats[f * _channels + c] = s;
        }
        return frames * _channels * sizeof(float);
    }
}
