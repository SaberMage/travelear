using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TravelEar.Core;

namespace TravelEar;

/// <summary>
/// The Tap (CONTEXT.md): a Harmony postfix on <c>AudioFilterMixer.OnAudioFilterRead</c>, filtered
/// to the mixer that belongs to the Local Voice renderer's <c>AudioSourceController</c>. The
/// mixer runs its filter list (the <c>VoicePlayer</c> at index 0 injects the voice, then the
/// game's per-voice filters) and, in synthesizer mode, multiplies the result by the source's own
/// gain envelope (the streaming clip is constant 1.0, so what arrives here is voice x spatial
/// gain, clamped). That is the complete Filter Stage output, so the postfix sees exactly what
/// the game would mix. It copies the block into the Sink ring and zeroes it in place.
/// <para>Runs on Unity's audio thread: no allocation beyond the interop wrappers, no logging.</para>
/// </summary>
[HarmonyPatch]
internal static class TapFilter
{
    private static long _target;

    /// <summary>Interleaved float32 at the DSP rate, sized for 2 s of 8-channel audio.</summary>
    public static readonly VoiceRingBuffer Ring = new(48_000 * 8 * 2);

    /// <summary>Channel count of the last tapped block (0 until the first block).</summary>
    public static volatile int Channels;
    public static volatile int BlockLength;
    public static long Blocks;
    public static float LastPeak;

    /// <summary>
    /// Capture timestamps keyed by Sink ring position (<c>REQ-OFFSET-MEASURE</c>): the Tap marks
    /// each block it stores with the timestamp it resolved from the provider ring; the pump
    /// resolves the position it reads to stamp the Sink frame header.
    /// </summary>
    public static readonly FrameStampTable SinkStamps = new();

    private static volatile VoicePlayer _player;
    private static volatile int _providerRingLength;
    public static long StampsResolved;

    /// <summary>Points the Tap at a mixer (its IL2CPP object pointer); <see cref="IntPtr.Zero"/> disables it.</summary>
    public static void SetTarget(IntPtr mixer) => Interlocked.Exchange(ref _target, (long)mixer);

    /// <summary>The VoicePlayer whose read head the Tap resolves against, and its provider ring length (samples).</summary>
    public static void SetReader(VoicePlayer player, int providerRingLength)
    {
        _providerRingLength = providerRingLength;
        _player = player;
    }

    [HarmonyTargetMethod]
    private static MethodBase TargetMethod() => GameSymbols.AudioFilterMixerOnAudioFilterRead;

    // [impl->REQ-TAP-DIVERT]
    // [impl->REQ-HAZARD-NO-GAME-AUDIO-LEAK]
    [HarmonyPostfix]
    private static void Postfix(AudioFilterMixer __instance, Il2CppStructArray<float> data, int channels)
    {
        if ((long)__instance.Pointer != Interlocked.Read(ref _target) || data is null) return;

        Span<float> block = data;
        var peak = 0f;
        for (var i = 0; i < block.Length; i++)
        {
            var a = Math.Abs(block[i]);
            if (a > peak) peak = a;
        }
        LastPeak = peak;
        Channels = channels;
        BlockLength = block.Length;
        Interlocked.Increment(ref Blocks);

        // [impl->REQ-OFFSET-MEASURE]
        // The VoicePlayer (filter 0) advanced its read head by this block before we ran, so the
        // block's first sample sat block.Length behind it. Resolve that provider ring position to
        // the encode timestamp and carry it on the Sink ring position we are about to write.
        var stamp = FrameStampTable.NoStamp;
        var player = _player;
        var ringLength = _providerRingLength;
        if (player is not null && ringLength > 0)
        {
            try
            {
                long blockStart = (long)player._readHead - block.Length;
                if (RoundTripProvider.Stamps.TryResolve(blockStart, ringLength, out stamp))
                    Interlocked.Increment(ref StampsResolved);
            }
            catch (Exception)
            {
                stamp = FrameStampTable.NoStamp; // never let Offset bookkeeping disturb the audio thread
            }
        }
        if (block.Length > 0) SinkStamps.Mark(Ring.WritePosition, block.Length, stamp);

        TapDivert.Divert(block, Ring);
    }
}
