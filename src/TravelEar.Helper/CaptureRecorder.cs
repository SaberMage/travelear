using System;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TravelEar.Core;

namespace TravelEar.Helper;

/// <summary>
/// Recording mode (T4a reference capture): records one WASAPI capture endpoint (the other
/// machine's audio output arriving on a capture card or line-in) to a WAV until the window
/// closes. The format is whatever the device delivers (usually 32-bit float, 2 ch, 48 kHz); the
/// analysis script resamples and downmixes. Counters go to the status window and Helper.log.
/// </summary>
internal sealed class CaptureRecorder : IDisposable
{
    private readonly HelperOptions _options;
    private readonly Action<string> _report;
    private WasapiCapture? _capture;
    private WaveFileWriter? _writer;
    private MMDevice? _device;
    private long _bytes;
    private float _peak;
    private readonly System.Threading.Timer _timer;
    private readonly DateTime _started = DateTime.Now;

    public event Action<int>? Exited;

    public string OutputPath { get; }

    public CaptureRecorder(HelperOptions options, Action<string> report)
    {
        _options = options;
        _report = report;
        OutputPath = options.CaptureFile ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TravelEar", "calibration",
            $"peer-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
        _timer = new System.Threading.Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
            var names = devices.Select(d => d.FriendlyName).ToList();
            var wanted = _options.CaptureDevice ?? "";
            var index = names.FindIndex(n => n.Contains(wanted, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                var listing = string.Join("\n  ", names);
                HelperLog.Write($"Capture: no capture device matches '{wanted}'. Active capture devices:\n  {listing}");
                _report($"No capture device matches '{wanted}'.\nActive capture devices:\n  {listing}");
                foreach (var d in devices) d.Dispose();
                Exited?.Invoke(Program.ExitNoDevice);
                return;
            }
            _device = devices[index];
            for (var i = 0; i < devices.Count; i++) if (i != index) devices[i].Dispose();

            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath)!);
            _capture = new WasapiCapture(_device, false, 20);
            _writer = new WaveFileWriter(OutputPath, _capture.WaveFormat);
            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null) HelperLog.Write($"Capture: stopped with {e.Exception.Message}");
            };
            _capture.StartRecording();
            HelperLog.Write($"Capture: recording '{_device.FriendlyName}' ({_capture.WaveFormat}) to {OutputPath}");
            _timer.Change(1000, 1000);
            Tick();
        }
        catch (Exception e)
        {
            HelperLog.Write($"Capture: could not start ({e.Message}).");
            _report($"Capture could not start:\n{e.Message}");
            Exited?.Invoke(Program.ExitNoDevice);
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var writer = _writer;
        if (writer is null) return;
        writer.Write(e.Buffer, 0, e.BytesRecorded);
        _bytes += e.BytesRecorded;
        if (_capture?.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            var peak = 0f;
            for (var i = 0; i + 4 <= e.BytesRecorded; i += 4)
            {
                var v = Math.Abs(BitConverter.ToSingle(e.Buffer, i));
                if (v > peak) peak = v;
            }
            _peak = Math.Max(_peak * 0.9f, peak);
        }
    }

    private void Tick()
    {
        var format = _capture?.WaveFormat;
        var seconds = format is null ? 0 : _bytes / (double)format.AverageBytesPerSecond;
        var text = $"Device   : {_device?.FriendlyName}\nMode     : reference capture ({format})\n" +
                   $"File     : {OutputPath}\nRecorded : {seconds:F1} s, peak {_peak:F3}\nStarted  : {_started:HH:mm:ss}\n\nClose this window to stop.";
        _report(text);
        if (((long)seconds) % 10 == 0) HelperLog.Write($"Capture: {seconds:F0} s recorded, peak {_peak:F3}");
    }

    public void Dispose()
    {
        _timer.Dispose();
        try { _capture?.StopRecording(); } catch (Exception) { /* closing */ }
        _capture?.Dispose();
        _writer?.Dispose();
        _device?.Dispose();
        HelperLog.Write($"Capture: closed {OutputPath} ({_bytes} bytes).");
    }
}
