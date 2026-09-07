using TravelEar.Core;

namespace TravelEar;

/// <summary>
/// The Sink's input when the feed point is the encoder thread (ADR-0005, config
/// <c>Fidelity.SinkFeed = Encoder</c>): the renderer writes each processed frame (or its gate
/// silence) straight into this ring, mono at the decoder rate, stamped with the encode timestamp
/// so Offset still resolves by ring position (<c>REQ-OFFSET-MEASURE</c>). No game object, no
/// provider ring, no Tap: nothing of Local Voice ever enters Unity's audio, so the no-leak
/// invariant (docs/KNOWN-HAZARDS.md 1.1) holds by construction. Single producer (the encoder
/// thread), single consumer (the pump).
/// </summary>
internal static class SinkFeed
{
    public const int Channels = 1;

    /// <summary>Mono float32 at the decoder rate, sized for 2 s.</summary>
    public static readonly VoiceRingBuffer Ring = new(LocalVoiceDecoder.SampleRate * 2);

    /// <summary>Capture timestamps keyed by ring position; the pump resolves the position it reads.</summary>
    public static readonly FrameStampTable Stamps = new();

    public static long Blocks;
    public static long SilenceBlocks;
    public static volatile float LastPeak;

    private static float[] _silence = Array.Empty<float>();

    // [impl->REQ-OFFSET-MEASURE]
    /// <summary>Encoder thread: stores one processed frame with its encode timestamp.</summary>
    public static void Write(ReadOnlySpan<float> mono, long captureTimestamp)
    {
        if (mono.Length == 0) return;
        var peak = 0f;
        for (var i = 0; i < mono.Length; i++)
        {
            var a = Math.Abs(mono[i]);
            if (a > peak) peak = a;
        }
        LastPeak = peak;
        Stamps.Mark(Ring.WritePosition, mono.Length, captureTimestamp);
        Ring.Write(mono);
        Interlocked.Increment(ref Blocks);
    }

    // [impl->REQ-VOICE-CONTINUOUS]
    /// <summary>Encoder thread: stores a zero frame (gate closed) with no stamp, so the stream stays continuous.</summary>
    public static void WriteSilence(int samples)
    {
        if (samples <= 0) return;
        if (_silence.Length < samples) _silence = new float[samples];
        Stamps.Mark(Ring.WritePosition, samples, FrameStampTable.NoStamp);
        Ring.Write(_silence.AsSpan(0, samples));
        Interlocked.Increment(ref SilenceBlocks);
    }
}
