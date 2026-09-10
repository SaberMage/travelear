using System.Diagnostics;
using System.IO.Pipes;
using BepInEx.Logging;
using TravelEar.Core;

namespace TravelEar;

/// <summary>
/// Offset measurement, mod side (<c>REQ-OFFSET-MEASURE</c>, docs/DESIGN.md "Offset"): a named-pipe
/// server on the return pipe (<c>TravelEar.Sink.Back</c>) that the Helper connects to and writes
/// one <see cref="OffsetReport"/> per Sink frame whose first sample it rendered: the capture
/// timestamp that travelled with the frame and the render timestamp, both on the machine's
/// performance counter. The difference is Offset. A rolling 10 s average is logged every 10 s and
/// kept in <see cref="LastAverageMs"/>; the Helper's window shows the same figure from its own
/// pairs (M3 T4d). Every failure is absorbed and the
/// server re-arms on the same cadence as the Sink pipe (docs/KNOWN-HAZARDS.md 2.1).
/// </summary>
internal sealed class OffsetMonitor : IDisposable
{
    private const double LogIntervalMs = 10_000;

    private readonly ManualLogSource _log;
    private readonly string _pipeName;
    private readonly OffsetAverager _averager = new();
    private readonly HelperLifecycle _lifecycle = new();
    private readonly CancellationTokenSource _stop = new();
    private Thread _thread;
    private double _lastAverageMs = double.NaN;

    public long Reports;
    public volatile bool Connected;

    public OffsetMonitor(ManualLogSource log, string pipeName)
    {
        _log = log;
        _pipeName = pipeName;
    }

    /// <summary>The last logged rolling average, in milliseconds; NaN before the first.</summary>
    public double LastAverageMs => Volatile.Read(ref _lastAverageMs);

    public void Start()
    {
        _thread = new Thread(Run) { Name = "TravelEar.OffsetMonitor", IsBackground = true };
        _thread.Start();
    }

    private static double NowMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    // [impl->REQ-OFFSET-MEASURE]
    private void Run()
    {
        var token = _stop.Token;
        var scratch = new byte[OffsetReportFrame.Size];
        var nextLog = NowMs() + LogIntervalMs;

        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream server = null;
            try
            {
                var delay = (int)_lifecycle.DelayBeforeArmMs(NowMs());
                if (delay > 0 && token.WaitHandle.WaitOne(delay)) break;

                server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                server.WaitForConnectionAsync(token).GetAwaiter().GetResult();
                Connected = true;
                _log.LogInfo("Offset: Helper connected to the return pipe.");

                while (!token.IsCancellationRequested)
                {
                    if (!OffsetReportFrame.TryReadFrom(server, out var report, scratch)) break;
                    var now = NowMs();
                    var offsetMs = (report.RenderTimestamp - report.CaptureTimestamp) * 1000.0 / Stopwatch.Frequency;
                    _averager.Add(offsetMs, now);
                    Interlocked.Increment(ref Reports);

                    if (now >= nextLog)
                    {
                        nextLog = now + LogIntervalMs;
                        if (_averager.TryAverage(now, out var avg, out var min, out var max, out var count))
                        {
                            Volatile.Write(ref _lastAverageMs, avg);
                            _log.LogInfo($"Offset: {avg:F0} ms rolling 10 s average ({count} frames, {min:F0}-{max:F0} ms).");
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidDataException)
            {
                if (Connected) _log.LogInfo($"Offset: return pipe closed ({e.GetType().Name}); waiting for a new connection.");
            }
            catch (Exception e)
            {
                _log.LogWarning($"Offset: monitor error, re-arming: {e.Message}");
            }
            finally
            {
                Connected = false;
                server?.Dispose();
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _thread?.Join(500);
    }
}
