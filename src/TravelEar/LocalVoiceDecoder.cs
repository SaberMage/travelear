using Dissonance.Audio.Codecs;
using Dissonance.Audio.Codecs.Opus;
using Il2CppInterop.Runtime;
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

    // Layout of Il2CppSystem.Nullable<ArraySegment<byte>> in the IL2CPP heap, read from the runtime.
    private readonly int _nullableValueOffset;
    private readonly int _nullableHasValueOffset;
    private readonly int _segmentSize;

    public LocalVoiceDecoder()
    {
        _decoder = new OpusDecoder(new WaveFormat(SampleRate, Channels), true);
        _pcm = new Il2CppStructArray<float>(MaxFrameSamples);
        _output = new Il2CppSystem.ArraySegment<float>(_pcm);

        var nullableClass = Il2CppClassPointerStore<Il2CppSystem.Nullable<Il2CppSystem.ArraySegment<byte>>>.NativeClassPtr;
        _nullableValueOffset = (int)IL2CPP.il2cpp_field_get_offset(IL2CPP.GetIl2CppField(nullableClass, "value"));
        _nullableHasValueOffset = (int)IL2CPP.il2cpp_field_get_offset(IL2CPP.GetIl2CppField(nullableClass, "hasValue"));
        uint align = 0;
        _segmentSize = IL2CPP.il2cpp_class_value_size(Il2CppClassPointerStore<Il2CppSystem.ArraySegment<byte>>.NativeClassPtr, ref align);
        if (_nullableValueOffset <= 0 || _nullableHasValueOffset <= 0 || _segmentSize <= 0)
            throw new InvalidOperationException($"Nullable<ArraySegment<byte>> layout not resolved (value @{_nullableValueOffset}, hasValue @{_nullableHasValueOffset}, segment {_segmentSize} bytes).");
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
        var input = new EncodedBuffer(WrapNullable(encoded), false);

        var count = _decoder.Decode(input, _output);
        pcm = count == MaxFrameSamples ? _output : new Il2CppSystem.ArraySegment<float>(_pcm, 0, Math.Max(0, count));
        return count;
    }

    /// <summary>Copies the first <paramref name="count"/> decoded samples into managed memory.</summary>
    public void CopyTo(float[] destination, int count)
    {
        for (var i = 0; i < count; i++) destination[i] = _pcm[i];
    }

    /// <summary>Writes <paramref name="count"/> processed samples back over the decoded ones.</summary>
    public void CopyFrom(float[] source, int count)
    {
        for (var i = 0; i < count; i++) _pcm[i] = source[i];
    }

    /// <summary>
    /// Builds <c>Nullable&lt;ArraySegment&lt;byte&gt;&gt;</c> by copying the segment's payload into a
    /// zeroed boxed Nullable. The interop-generated <c>Nullable(T value)</c> constructor cannot be
    /// used: for a struct proxy <c>T</c> (a managed class) it passes the boxed object pointer where
    /// IL2CPP expects the struct payload, so the decoder would read a garbage array (first in-game
    /// run of M1 T3 crashed on exactly that).
    /// </summary>
    private unsafe Il2CppSystem.Nullable<Il2CppSystem.ArraySegment<byte>> WrapNullable(Il2CppSystem.ArraySegment<byte> segment)
    {
        var nullable = new Il2CppSystem.Nullable<Il2CppSystem.ArraySegment<byte>>();
        var box = (byte*)nullable.Pointer;                              // field offsets are boxed-relative
        var payload = (byte*)IL2CPP.il2cpp_object_unbox(segment.Pointer); // the segment's { array, offset, count }
        Buffer.MemoryCopy(payload, box + _nullableValueOffset, _segmentSize, _segmentSize);
        box[_nullableHasValueOffset] = 1;
        return nullable;
    }
}
