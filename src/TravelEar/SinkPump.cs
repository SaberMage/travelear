using System.Diagnostics;
using System.IO.Pipes;
using TravelEar.Core;

namespace TravelEar;

/// <summary>
/// Drains the Tap ring into the Sink: a named-pipe server (<c>TravelEar.Sink</c>, outbound bytes)
/// that the Helper connects to as a client, fed with <see cref="SinkFrame"/>s on a dedicated
/// thread. Pacing follows the audio thread's clock: the pump sends whatever the Tap has produced
/// since the last tick instead of asking for a fixed amount, so it never pads with silence on its
/// own. Every disconnect (Helper closed, never started) re-arms the server, no more often than
/// every 5 s (<see cref="HelperLifecycle"/>, <c>REQ-SINK-LIFECYCLE</c>); nothing here can reach
/// gameplay or the game's audio (docs/KNOWN-HAZARDS.md 2.1). With config <c>Downmix</c> on, each
/// block is folded to mono before framing (<c>REQ-SINK-FORMAT</c>).
/// </summary>
internal sealed class SinkPump : IDisposable
{
    private const int TickMs = 5;
    private const int MinFramesPerSend = 240;   // 5 ms at 48 kHz
    private const int MaxFramesPerSend = 4800;  // 100 ms at 48 kHz

    private readonly VoiceRingBuffer _ring;
    private readonly Func<int> _channels;
    private readonly Func<int> _sampleRate;
    private readonly Func<bool> _downmix;
    private readonly HelperLifecycle _lifecycle = new();
    private readonly CancellationTokenSource _stop = new();
    private Thread _thread;

    public long FramesSent;
    public long Connections;
    public volatile bool Connected;
    public volatile string LastError;

    public SinkPump(VoiceRingBuffer ring, Func<int> channels, Func<int> sampleRate, Func<bool> downmix = null)
    {
        _ring = ring;
        _channels = channels;
        _sampleRate = sampleRate;
        _downmix = downmix ?? (() => false);
    }

    private static double NowMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    public void Start()
    {
        _thread = new Thread(Run) { Name = "TravelEar.SinkPump", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Run()
    {
        var token = _stop.Token;
        var buffer = new float[MaxFramesPerSend * 8];
        var scratch = Array.Empty<byte>();

        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream server = null;
            try
            {
                // [impl->REQ-SINK-LIFECYCLE]
                var delay = (int)_lifecycle.DelayBeforeArmMs(NowMs());
                if (delay > 0 && token.WaitHandle.WaitOne(delay)) break;

                server = new NamedPipeServerStream(HelperOptions.DefaultPipeName, PipeDirection.Out, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                server.WaitForConnectionAsync(token).GetAwaiter().GetResult();
                Connected = true;
                Interlocked.Increment(ref Connections);
                Plugin.Logger.LogInfo("Sink: Helper connected to the pipe.");
                _ring.Clear(); // start fresh; stale audio would only add latency

                while (!token.IsCancellationRequested)
                {
                    var channels = _channels();
                    if (channels <= 0)
                    {
                        Thread.Sleep(20);
                        continue;
                    }

                    var available = _ring.Count;
                    var frames = Math.Min(available / channels, MaxFramesPerSend);
                    if (frames < MinFramesPerSend)
                    {
                        Thread.Sleep(TickMs);
                        continue;
                    }

                    var count = frames * channels;
                    var samples = buffer.AsSpan(0, count);
                    // [impl->REQ-OFFSET-MEASURE]
                    var readPosition = _ring.ReadPosition;
                    _ring.Read(samples);
                    if (!TapFilter.SinkStamps.TryResolve(readPosition, 0, out var captured)) captured = FrameStampTable.NoStamp;

                    // [impl->REQ-SINK-FORMAT]
                    if (channels > 1 && _downmix())
                    {
                        Downmixer.ToMono(samples, channels, samples);
                        samples = samples.Slice(0, frames);
                        channels = 1;
                        count = frames;
                    }

                    var header = new SinkFrameHeader((ushort)channels, (uint)_sampleRate(), captured, count);
                    SinkFrame.WriteTo(server, header, samples, ref scratch);
                    Interlocked.Increment(ref FramesSent);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                if (Connected) Plugin.Logger.LogInfo($"Sink: Helper disconnected ({e.GetType().Name}); waiting for a new connection.");
                LastError = e.Message;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Plugin.Logger.LogWarning($"Sink: pump error, re-arming in 1 s: {e}");
                Thread.Sleep(1000);
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
