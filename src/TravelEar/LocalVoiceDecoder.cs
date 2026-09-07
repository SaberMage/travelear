using Dissonance.Audio.Codecs;
using Dissonance.Audio.Codecs.Opus;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using NAudio.Wave;

namespace TravelEar;

/// <summary>
/// Decodes Outbound Voice frames with the game's own <c>OpusDecoder</c> (48 kHz mono, FEC on:
/// the session codec Dissonance logs at start) into an IL2CPP float array that is handed straight
/// to the round-trip provider, so the PCM never crosses into managed memory. One instance,
/// used from the encoder thread only.
/// </summary>
internal sealed class LocalVoiceDecoder
{
    public const int SampleRate = 48_000;
    public const int Channels = 1;

    /// <summary>Largest Opus frame (120 ms at 48 kHz); the game uses 2880 (60 ms).</summary>
    private const int MaxFrameSamples = 5760;

    private readonly OpusDecoder _decoder;
    private readonly Il2CppStructArray<float> _pcm;
    private readonly Il2CppSystem.ArraySegment<float> _output;

    public LocalVoiceDecoder()
    {
        _decoder = new OpusDecoder(new WaveFormat(SampleRate, Channels), true);
        _pcm = new Il2CppStructArray<float>(MaxFrameSamples);
        _output = new Il2CppSystem.ArraySegment<float>(_pcm);
    }

    // [impl->REQ-VOICE-ROUNDTRIP]
    /// <summary>
    /// Decodes one frame. Returns the sample count and a segment over the decoder's own IL2CPP
    /// buffer, valid until the next call.
    /// </summary>
    public int Decode(byte[] opus, out Il2CppSystem.ArraySegment<float> pcm)
    {
        Il2CppStructArray<byte> bytes = opus;
        var encoded = new Il2CppSystem.ArraySegment<byte>(bytes);
        var input = new EncodedBuffer(new Il2CppSystem.Nullable<Il2CppSystem.ArraySegment<byte>>(encoded), false);

        var count = _decoder.Decode(input, _output);
        pcm = count == MaxFrameSamples ? _output : new Il2CppSystem.ArraySegment<float>(_pcm, 0, Math.Max(0, count));
        return count;
    }
}
