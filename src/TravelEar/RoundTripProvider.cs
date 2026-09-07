using System.Reflection;
using HarmonyLib;
using NAudio.Wave;
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

    /// <summary>Adds the provider to <paramref name="host"/>, which must be inactive so Awake/Start run after setup.</summary>
    public static LocalVoiceProvider Create(GameObject host)
    {
        Provider = host.AddComponent<LocalVoiceProvider>();
        Interlocked.Exchange(ref _ownedPointer, (long)Provider.Pointer);
        _format = new WaveFormat(LocalVoiceDecoder.SampleRate, LocalVoiceDecoder.Channels);
        return Provider;
    }

    // [impl->REQ-VOICE-ROUNDTRIP]
    /// <summary>Pushes one decoded frame. Safe from any IL2CPP-attached thread; the provider locks its ring.</summary>
    public static void Push(Il2CppSystem.ArraySegment<float> pcm)
    {
        var provider = Provider;
        if (provider is null) return;
        provider.Dissonance_Audio_Capture_IMicrophoneSubscriber_ReceiveMicrophoneData(pcm, _format);
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
