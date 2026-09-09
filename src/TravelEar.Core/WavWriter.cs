using System;
using System.IO;
using System.Text;

namespace TravelEar.Core;

/// <summary>
/// A streaming 32-bit float mono WAV writer for the calibration captures: samples are appended
/// as they come and the RIFF sizes are patched on <see cref="Dispose"/>. Not for real-time use
/// on the audio thread of anything that cannot afford a file write; the mod's encoder thread
/// can (it already writes the Sink pipe).
/// </summary>
public sealed class WavWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;
    private readonly object _lock = new();
    private long _samples;
    private bool _disposed;

    public int SampleRate { get; }
    public string Path { get; }

    /// <summary>Samples written so far (the position in seconds is this over <see cref="SampleRate"/>).</summary>
    public long Samples { get { lock (_lock) return _samples; } }

    public WavWriter(string path, int sampleRate)
    {
        Path = path;
        SampleRate = sampleRate;
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        _writer = new BinaryWriter(_stream);
        WriteHeader(0);
    }

    private void WriteHeader(long samples)
    {
        var dataBytes = samples * 4;
        _writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        _writer.Write((uint)Math.Min(uint.MaxValue, 36 + dataBytes));
        _writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        _writer.Write(Encoding.ASCII.GetBytes("fmt "));
        _writer.Write(16);
        _writer.Write((ushort)3); // IEEE float
        _writer.Write((ushort)1);
        _writer.Write(SampleRate);
        _writer.Write(SampleRate * 4);
        _writer.Write((ushort)4);
        _writer.Write((ushort)32);
        _writer.Write(Encoding.ASCII.GetBytes("data"));
        _writer.Write((uint)Math.Min(uint.MaxValue, dataBytes));
    }

    public void Write(ReadOnlySpan<float> samples)
    {
        lock (_lock)
        {
            if (_disposed) return;
            for (var i = 0; i < samples.Length; i++) _writer.Write(samples[i]);
            _samples += samples.Length;
        }
    }

    public void WriteSilence(int count)
    {
        lock (_lock)
        {
            if (_disposed) return;
            for (var i = 0; i < count; i++) _writer.Write(0f);
            _samples += count;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _writer.Flush();
                _stream.Position = 0;
                WriteHeader(_samples);
                _writer.Flush();
            }
            finally
            {
                _writer.Dispose();
                _stream.Dispose();
            }
        }
    }
}
