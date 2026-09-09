using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx.Logging;
using TravelEar.Core;

namespace TravelEar;

/// <summary>
/// T4a reference capture (M3-PLAN): while <c>Calibration.Capture</c> is on, everything needed to
/// compare Local Voice with what a real listener renders for the same speech is written under
/// <c>%LOCALAPPDATA%\TravelEar\calibration\&lt;timestamp&gt;\</c>:
/// <list type="bullet">
/// <item><c>input.wav</c>: the decoded outbound voice before any of the mod's processing, one
/// frame per encoded frame, gate silence included, so sample position is time.</item>
/// <item><c>local-voice.wav</c>: the chain's output exactly as sent to the Helper (tails and
/// silence included), sample-aligned with <c>input.wav</c>.</item>
/// <item><c>frames.csv</c>: per frame, its sample offset, the encode timestamp and wall clock, and
/// whether the gate passed it, rendered a tail, or silenced it.</item>
/// <item><c>floats.csv</c>: every exposed mixer float the game wrote, with elapsed ms.</item>
/// <item><c>session.txt</c>: start time and the config in force.</item>
/// </list>
/// The Helper's <c>--capture</c> mode records the other machine's output beside these as
/// <c>peer.wav</c>; <c>tools/calibrate.py</c> aligns and compares the three.
/// </summary>
internal sealed class CalibrationCapture : IDisposable
{
    private readonly ManualLogSource _log;
    private readonly WavWriter _input;
    private readonly WavWriter _output;
    private readonly StreamWriter _frames;
    private readonly StreamWriter _floats;
    private readonly object _floatsLock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _inputSamples;
    private long _frameIndex;
    private long _floatWrites;
    private bool _disposed;

    /// <summary>The live capture, set by the plugin while <c>Calibration.Capture</c> is on; read on the encoder thread.</summary>
    public static volatile CalibrationCapture Instance;

    public string Directory { get; }

    public static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TravelEar", "calibration");

    public CalibrationCapture(ManualLogSource log, string sessionNotes)
    {
        _log = log;
        Directory = Path.Combine(Root, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        System.IO.Directory.CreateDirectory(Directory);
        _input = new WavWriter(Path.Combine(Directory, "input.wav"), LocalVoiceDecoder.SampleRate);
        _output = new WavWriter(Path.Combine(Directory, "local-voice.wav"), LocalVoiceDecoder.SampleRate);
        _frames = new StreamWriter(Path.Combine(Directory, "frames.csv"), false, Encoding.ASCII) { AutoFlush = false };
        _frames.WriteLine("frame,input_sample,elapsed_ms,captured_at_ticks,wallclock,disposition");
        _floats = new StreamWriter(Path.Combine(Directory, "floats.csv"), false, Encoding.ASCII) { AutoFlush = false };
        _floats.WriteLine("elapsed_ms,name,value");
        File.WriteAllText(Path.Combine(Directory, "session.txt"),
            $"started {DateTime.Now:O}\nsample_rate {LocalVoiceDecoder.SampleRate}\nstopwatch_frequency {Stopwatch.Frequency}\n{sessionNotes}\n");
        MixerFloats.Observer = OnFloat;
        _log.LogInfo($"Calibration capture: recording to {Directory}");
    }

    /// <summary>Encoder thread: one decoded frame before the chain, and how the gate treated it.</summary>
    public void Input(ReadOnlySpan<float> pcm, long capturedAtTicks, string disposition)
    {
        if (_disposed) return;
        var offset = _inputSamples;
        _input.Write(pcm);
        _inputSamples += pcm.Length;
        var index = _frameIndex++;
        lock (_frames)
        {
            _frames.Write(index.ToString(CultureInfo.InvariantCulture)); _frames.Write(',');
            _frames.Write(offset.ToString(CultureInfo.InvariantCulture)); _frames.Write(',');
            _frames.Write(_clock.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)); _frames.Write(',');
            _frames.Write(capturedAtTicks.ToString(CultureInfo.InvariantCulture)); _frames.Write(',');
            _frames.Write(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)); _frames.Write(',');
            _frames.WriteLine(disposition);
            if ((index & 63) == 0) _frames.Flush();
        }
    }

    /// <summary>Encoder thread: what went to the Helper for the frame just reported by <see cref="Input"/>.</summary>
    public void Output(ReadOnlySpan<float> frame)
    {
        if (_disposed) return;
        _output.Write(frame);
    }

    public void OutputSilence(int count)
    {
        if (_disposed) return;
        _output.WriteSilence(count);
    }

    private void OnFloat(string name, float value)
    {
        if (_disposed) return;
        lock (_floatsLock)
        {
            _floats.Write(_clock.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)); _floats.Write(',');
            _floats.Write(name); _floats.Write(',');
            _floats.WriteLine(value.ToString("R", CultureInfo.InvariantCulture));
            if ((++_floatWrites & 255) == 0) _floats.Flush();
        }
    }

    public string Status => $"calibration capture: {_inputSamples / (double)LocalVoiceDecoder.SampleRate:F0} s, {_frameIndex} frames, {_floatWrites} float writes";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        MixerFloats.Observer = null;
        try { _input.Dispose(); } catch (Exception) { /* best effort */ }
        try { _output.Dispose(); } catch (Exception) { /* best effort */ }
        lock (_frames) { try { _frames.Dispose(); } catch (Exception) { /* best effort */ } }
        lock (_floatsLock) { try { _floats.Dispose(); } catch (Exception) { /* best effort */ } }
        _log.LogInfo($"Calibration capture: closed {Directory} ({_inputSamples / (double)LocalVoiceDecoder.SampleRate:F0} s).");
    }
}
