using System.Reflection;
using HarmonyLib;
using NAudio.Wave;
using TravelEar.Core;
using UnityEngine;

namespace TravelEar;

/// <summary>
/// The round-trip provider (docs/DESIGN.md): a mod-owned instance of the game's own
/// <c>LocalVoiceProvider</c>, which already implements the <c>IVoiceDataProvider</c> ring contract
/// a game <c>VoicePlayer</c> consumes, so no IL2CPP interface has to be implemented from managed
/// code (M1 open question 3). Decoded Outbound Voice is pushed through its
/// <c>IMicrophoneSubscriber.ReceiveMicrophoneData</c> proxy exactly as the mic feed would be.
/// <para>
/// Ring semantics (from the game's bodies): mono input is duplicated to the DSP channel count,
/// no resampling (the mic format must match the DSP rate), and the reader starts two DSP buffer
/// sets behind the write head.
/// </para>
/// <para>
/// The game's <c>LocalVoiceProvider.Start</c> subscribes the instance to the mic; a Harmony
/// prefix skips that for the mod-owned instance only, so it never receives raw mic audio.
/// </para>
/// </summary>
[HarmonyPatch]
internal static class RoundTripProvider
{
    private static long _ownedPointer;
    private static WaveFormat _format;

    public static LocalVoiceProvider Provider { get; private set; }
    public static long FramesPushed;

    /// <summary>
    /// Capture timestamps keyed by provider ring position (<c>REQ-OFFSET-MEASURE</c>): every push,
    /// voice or silence, marks the span it wrote, so the Tap can resolve the block it is handed to
    /// the encode timestamp of the frame it came from. Producers (encoder and main thread) are
    /// serialized by <see cref="PushLock"/>; the Tap resolves on the audio thread lock-free.
    /// </summary>
    public static readonly FrameStampTable Stamps = new();
    private static readonly object PushLock = new();

    /// <summary>Adds the provider to <paramref name="host"/>, which must be inactive so Awake/Start run after setup.</summary>
    public static LocalVoiceProvider Create(GameObject host)
    {
        Provider = host.AddComponent<LocalVoiceProvider>();
        Interlocked.Exchange(ref _ownedPointer, (long)Provider.Pointer);
        _format = new WaveFormat(LocalVoiceDecoder.SampleRate, LocalVoiceDecoder.Channels);
        return Provider;
    }

    // [impl->REQ-VOICE-ROUNDTRIP]
    // [impl->REQ-OFFSET-MEASURE]
    /// <summary>
    /// Pushes one decoded frame of <paramref name="samples"/> mono samples and marks the ring span it
    /// occupies (<paramref name="samples"/> x the ring's channel count, starting at the write head
    /// before the push) with <paramref name="captureTimestamp"/> (<see cref="FrameStampTable.NoStamp"/>
    /// for silence the mod generated). Safe from any IL2CPP-attached thread.
    /// </summary>
    public static void Push(Il2CppSystem.ArraySegment<float> pcm, int samples, int ringChannels, long captureTimestamp)
    {
        var provider = Provider;
        if (provider is null) return;
        lock (PushLock)
        {
            var start = provider.CachedVoiceWriteHead;
            provider.Dissonance_Audio_Capture_IMicrophoneSubscriber_ReceiveMicrophoneData(pcm, _format);
            if (samples > 0) Stamps.Mark(start, samples * Math.Max(1, ringChannels), captureTimestamp);
        }
        Interlocked.Increment(ref FramesPushed);
    }

    [HarmonyTargetMethod]
    private static MethodBase TargetMethod() => GameSymbols.LocalVoiceProviderStart;

    [HarmonyPrefix]
    private static bool SkipMicSubscription(LocalVoiceProvider __instance)
    {
        if ((long)__instance.Pointer != Interlocked.Read(ref _ownedPointer))
            return true; // the game's own provider: untouched
        Plugin.Logger.LogInfo("Round-trip provider: mic subscription skipped for the mod-owned LocalVoiceProvider.");
        return false;
    }
}
