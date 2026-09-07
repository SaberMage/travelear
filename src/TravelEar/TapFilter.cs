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

    /// <summary>Points the Tap at a mixer (its IL2CPP object pointer); <see cref="IntPtr.Zero"/> disables it.</summary>
    public static void SetTarget(IntPtr mixer) => Interlocked.Exchange(ref _target, (long)mixer);

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

        TapDivert.Divert(block, Ring);
    }
}
