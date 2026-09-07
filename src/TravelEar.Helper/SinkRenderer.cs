using System.IO.Pipes;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TravelEar.Core;

namespace TravelEar.Helper;

/// <summary>
/// Owns the audio side of the Helper on a dedicated thread: selects the render endpoint, then either
/// plays a test tone (<c>--tone</c>) or connects to the Sink pipe and renders every frame it
/// receives through NAudio <see cref="WasapiOut"/> (shared mode, event-driven). Exits when the
/// pipe closes, when the endpoint cannot be found, or when <see cref="Stop"/> is called.
/// </summary>
internal sealed class SinkRenderer : IDisposable
{
    private const int LatencyMs = 40;
    private const int ToneSampleRate = 48_000;
    private const int ToneChannels = 2;
    private const float ToneFrequencyHz = 440f;
    private const float ToneAmplitude = 0.25f; // about -12 dBFS
    private const int PipeRetryMs = 1000;
    private const int StatusIntervalMs = 500;

    private readonly HelperOptions _options;
    private readonly Action<string> _report;
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;
    private int _exitCode = Program.ExitOk;
    private int _exited;

    public event Action<int>? Exited;

    public SinkRenderer(HelperOptions options, Action<string> report)
    {
        _options = options;
        _report = report;
    }

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Run) { Name = "TravelEar.SinkRenderer", IsBackground = true };
        _thread.Start();
    }

    public void Stop()
    {
        _stop.Cancel();
        // The pipe read is blocking; closing the stream unblocks it (see Run).
        _pipe?.Dispose();
        if (_thread is not null && _thread != Thread.CurrentThread) _thread.Join(3000);
        _backThread?.Join(1500);
    }

    public void Dispose()
    {
        Stop();
        _stop.Dispose();
    }

    private NamedPipeClientStream? _pipe;
    private readonly System.Collections.Concurrent.ConcurrentQueue<OffsetReport> _reports = new();
    private const int MaxQueuedReports = 256;
    private Thread? _backThread;
    private NamedPipeClientStream? _backPipe;

    private void Run()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = SelectDevice(enumerator);
            if (device is null)
            {
                _exitCode = Program.ExitNoEndpoint;
                return;
            }
            using (device)
            {
                if (_options.Tone) RunTone(device);
                else RunPipe(device);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            HelperLog.Write($"Renderer failed: {e}");
            _report($"Renderer failed:\n{e.Message}");
            _exitCode = Program.ExitAudioFailure;
        }
        finally
        {
            if (Interlocked.Exchange(ref _exited, 1) == 0)
                Exited?.Invoke(_exitCode);
        }
    }

    // [impl->REQ-SINK-ENDPOINT-CONFIG]
    /// <summary>Resolves <c>--endpoint</c> against the active render devices; null (and a logged list) when nothing matches.</summary>
    private MMDevice? SelectDevice(MMDeviceEnumerator enumerator)
    {
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        var names = devices.Select(d => d.FriendlyName).ToList();

        if (!SinkEndpointSelector.TryChoose(names, _options.EndpointSetting, out var index))
        {
            var listing = string.Join("\n  ", names);
            HelperLog.Write($"No render device matches '{_options.EndpointSetting}'. Active devices:\n  {listing}");
            _report($"No playback device matches '{_options.EndpointSetting}'.\nActive devices:\n  {listing}");
            foreach (var d in devices) d.Dispose();
            return null;
        }

        MMDevice chosen;
        if (index == SinkEndpointSelector.DefaultDevice)
        {
            chosen = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            foreach (var d in devices) d.Dispose();
        }
        else
        {
            chosen = devices[index];
            for (var i = 0; i < devices.Count; i++) if (i != index) devices[i].Dispose();
        }
        HelperLog.Write($"Render endpoint: {chosen.FriendlyName}");
        return chosen;
    }

    private void RunTone(MMDevice device)
    {
        var provider = new ToneWaveProvider(ToneSampleRate, ToneChannels, ToneFrequencyHz, ToneAmplitude);
        using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, LatencyMs);
        output.Init(provider);
        output.Play();
        HelperLog.Write($"Tone: {ToneFrequencyHz} Hz on {device.FriendlyName}");

        var started = DateTime.UtcNow;
        while (!_stop.IsCancellationRequested)
        {
            _report($"Endpoint : {device.FriendlyName}\nMode     : test tone {ToneFrequencyHz} Hz, {ToneSampleRate} Hz {ToneChannels} ch\n" +
                    $"Playing  : {(DateTime.UtcNow - started):hh\\:mm\\:ss}\n\nClose this window to stop.");
            _stop.Token.WaitHandle.WaitOne(StatusIntervalMs);
        }
        output.Stop();
    }

    private void RunPipe(MMDevice device)
    {
        _pipe = new NamedPipeClientStream(".", _options.PipeName, PipeDirection.In, PipeOptions.None);
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                _report($"Endpoint : {device.FriendlyName}\nPipe     : {_options.PipeName}\nState    : waiting for the game...");
                _pipe.Connect(PipeRetryMs);
                break;
            }
            catch (TimeoutException) { }
            catch (IOException) { _stop.Token.WaitHandle.WaitOne(PipeRetryMs); }
        }
        if (_stop.IsCancellationRequested) return;
        HelperLog.Write("Pipe connected.");

        var scratch = Array.Empty<byte>();
        var samples = Array.Empty<float>();
        WasapiOut? output = null;
        RingWaveProvider? provider = null;
        long frames = 0;
        var lastStatus = DateTime.MinValue;
        var lastLog = DateTime.UtcNow;

        try
        {
            while (!_stop.IsCancellationRequested)
            {
                bool got;
                try
                {
                    got = SinkFrame.TryReadFrom(_pipe, out var header, ref samples, ref scratch);
                    if (!got) break; // clean EOF: the game closed the pipe

                    if (provider is null || provider.Channels != header.Channels || provider.SampleRate != header.SampleRate)
                    {
                        output?.Stop();
                        output?.Dispose();
                        provider = new RingWaveProvider((int)header.SampleRate, header.Channels, (int)header.SampleRate * header.Channels / 2);
                        output = new WasapiOut(device, AudioClientShareMode.Shared, true, LatencyMs);
                        output.Init(provider);
                        provider.Prime();
                        var clock = output;
                        provider.FallbackLatencySeconds = LatencyMs / 1000.0;
                        provider.PlayedSeconds = () =>
                        {
                            try { return (double)clock.GetPosition() / clock.OutputWaveFormat.AverageBytesPerSecond; }
                            catch (Exception) { return null; }
                        };
                        provider.Reported = report =>
                        {
                            if (_reports.Count < MaxQueuedReports) _reports.Enqueue(report);
                        };
                        output.Play();
                        StartBackPipe();
                        HelperLog.Write($"Stream format: {header.SampleRate} Hz, {header.Channels} ch");
                    }

                    // [impl->REQ-OFFSET-MEASURE]
                    var writePosition = provider.Ring.WritePosition;
                    provider.Ring.Write(samples.AsSpan(0, header.SampleCount));
                    if (header.SampleCount > 0) provider.Stamps.Mark(writePosition, header.SampleCount, header.CaptureTimestamp);
                    frames++;
                }
                catch (IOException) when (_stop.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) { break; }
                catch (InvalidDataException e)
                {
                    HelperLog.Write($"Pipe framing error, closing: {e.Message}");
                    _report($"Pipe framing error:\n{e.Message}");
                    _exitCode = Program.ExitAudioFailure;
                    break;
                }

                var now = DateTime.UtcNow;
                if ((now - lastLog).TotalSeconds >= 10 && provider is not null)
                {
                    lastLog = now;
                    HelperLog.Write($"Stream: frames {frames}, buffered {provider.Ring.Count} smp, underruns {provider.Ring.Underruns}, dropped {provider.Ring.DroppedSamples} smp, trims {provider.Trims} ({provider.TrimmedSamples} smp), offset reports {provider.Reports}");
                }
                if ((now - lastStatus).TotalMilliseconds >= StatusIntervalMs && provider is not null)
                {
                    lastStatus = now;
                    _report($"Endpoint : {device.FriendlyName}\nPipe     : {_options.PipeName}\n" +
                            $"Stream   : {provider.SampleRate} Hz {provider.Channels} ch\n" +
                            $"Frames   : {frames}   buffered {provider.Ring.Count} samples\n" +
                            $"Underruns: {provider.Ring.Underruns}   dropped {provider.Ring.DroppedSamples}\n" +
                            $"Trims    : {provider.Trims} ({provider.TrimmedSamples} samples; cap {RingWaveProvider.MaxBacklogMs} ms)\n" +
                            $"Offset   : {provider.Reports} reports{(_backPipe?.IsConnected == true ? "" : " (return pipe not connected)")}");
                }
            }
        }
        finally
        {
            output?.Stop();
            output?.Dispose();
        }
        HelperLog.Write($"Pipe closed after {frames} frames.");
    }

    // [impl->REQ-OFFSET-MEASURE]
    /// <summary>
    /// Drains <see cref="_reports"/> to the return pipe on its own thread. The mod serves the pipe;
    /// while it is not there, reports are dropped and the connection retried every second. Nothing
    /// here can stall the render thread or the Sink read.
    /// </summary>
    private void StartBackPipe()
    {
        if (_backThread is not null) return;
        _backThread = new Thread(RunBackPipe) { Name = "TravelEar.OffsetBack", IsBackground = true };
        _backThread.Start();
    }

    private void RunBackPipe()
    {
        var scratch = new byte[OffsetReportFrame.Size];
        var name = HelperOptions.BackPipeName(_options.PipeName);
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.None);
                _backPipe = pipe;
                try { pipe.Connect(PipeRetryMs); }
                catch (TimeoutException) { while (_reports.TryDequeue(out _)) { } continue; }
                HelperLog.Write("Return pipe connected.");

                while (!_stop.IsCancellationRequested)
                {
                    if (!_reports.TryDequeue(out var report))
                    {
                        _stop.Token.WaitHandle.WaitOne(20);
                        continue;
                    }
                    OffsetReportFrame.WriteTo(pipe, report, scratch);
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or UnauthorizedAccessException)
            {
                HelperLog.Write($"Return pipe closed ({e.GetType().Name}); retrying.");
                _stop.Token.WaitHandle.WaitOne(PipeRetryMs);
            }
            catch (Exception e)
            {
                HelperLog.Write($"Return pipe error: {e.Message}");
                _stop.Token.WaitHandle.WaitOne(PipeRetryMs);
            }
            finally
            {
                _backPipe = null;
            }
        }
    }
}
