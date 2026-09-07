using System.Diagnostics;
using System.IO.Pipes;
using TravelEar.Core;

namespace TravelEar;

/// <summary>
/// Drains the Tap ring into the Sink: a named-pipe server (<c>TravelEar.Sink</c>, outbound bytes)
/// that the Helper connects to as a client, fed with <see cref="SinkFrame"/>s on a dedicated
/// thread. Pacing follows the audio thread's clock: the pump sends whatever the Tap has produced
/// since the last tick instead of asking for a fixed amount, so it never pads with silence on its
/// own. Every disconnect (Helper closed, never started) just re-arms the server; nothing here
/// can reach gameplay or the game's audio (docs/KNOWN-HAZARDS.md 2.1).
/// </summary>
internal sealed class SinkPump : IDisposable
{
    private const int TickMs = 5;
    private const int MinFramesPerSend = 240;   // 5 ms at 48 kHz
    private const int MaxFramesPerSend = 4800;  // 100 ms at 48 kHz

    private readonly VoiceRingBuffer _ring;
    private readonly Func<int> _channels;
    private readonly Func<int> _sampleRate;
    private readonly CancellationTokenSource _stop = new();
    private Thread _thread;

    public long FramesSent;
    public long Connections;
    public volatile bool Connected;
    public volatile string LastError;

    public SinkPump(VoiceRingBuffer ring, Func<int> channels, Func<int> sampleRate)
    {
        _ring = ring;
        _channels = channels;
        _sampleRate = sampleRate;
    }

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
                    _ring.Read(samples);
                    var header = new SinkFrameHeader((ushort)channels, (uint)_sampleRate(), Stopwatch.GetTimestamp(), count);
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
